using QuantumHub.Contracts.Ui;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using QuantumHub.Contracts.Proto;
using Google.Protobuf;

namespace QuantumHub.Core.Config;

/// <summary>Источник истины для настроек: по одному JSONC-документу на плагин.</summary>
/// <remarks>
/// Запись сразу меняет значение в памяти и попадает на диск немного позже одним разом, поэтому
/// разговорчивый плагин не превращается в постоянную запись на диск. Комментарии при записи не сохраняются.
/// </remarks>
public sealed class ConfigStore : IDisposable
{
    /// <summary>Пауза перед записью на диск.</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(250);

    private readonly string _dir;
    private readonly HubLog _log;
    /// <summary>Открытые документы. Ключ — владелец.</summary>
    private readonly ConcurrentDictionary<string, Document> _documents = new();
    /// <summary>Откладывает запись. Каждое изменение сдвигает её заново.</summary>
    private readonly Timer _timer;
    /// <summary>Запись на диск идёт только одна за раз.</summary>
    private readonly object _flushGate = new();

    /// <summary>Создаёт хранилище.</summary>
    /// <param name="dir">Папка конфигов.</param>
    /// <param name="log">Журнал.</param>
    public ConfigStore(string dir, HubLog log)
    {
        _dir = dir;
        _log = log;
        _timer = new Timer(_ => Flush());
    }

    /// <summary>Значение изменилось. Аргументы: id плагина и ключ.</summary>
    public event Action<string, string>? Changed;

    /// <summary>Находит документ или читает его с диска. Испорченный файл откладывается в сторону, его не перезаписывают.</summary>
    /// <exception cref="InvalidDataException">Такое имя вывело бы файл за папку конфигов.</exception>
    private Document Open(string owner) => _documents.GetOrAdd(owner, id =>
    {
        if (id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id is "." or "..")
            throw new InvalidDataException(Loc.T($"'{id}' не может быть владельцем настроек.", $"'{id}' cannot own a config."));

        var path = Path.Combine(_dir, $"{id}.jsonc");
        JsonObject values = [];
        if (File.Exists(path))
        {
            try
            {
                values = Jsonc.Parse(File.ReadAllText(path)) as JsonObject ?? [];
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                // Оставляем сломанный файл пользователю и продолжаем со значениями по умолчанию.
                _log.Warning(Loc.T($"Настройки '{path}' не читаются, файл отложен в сторону: {ex.Message}", $"Config '{path}' is unreadable and was set aside: {ex.Message}"));
                File.Move(path, $"{path}.broken-{DateTime.Now:yyyyMMddHHmmss}", true);
            }
        }

        return new Document(path, values);
    });

    /// <summary>Заполняет значениями из манифеста ключи, которые пользователь ещё не задавал.</summary>
    /// <param name="manifest">Манифест плагина.</param>
    public void ApplyDefaults(PluginManifest manifest)
    {
        var document = Open(manifest.Id);
        var repaired = false;
        lock (document)
        {
            foreach (var setting in manifest.Settings)
            {
                if (setting.Type == "list" && document.Values[setting.Key] is JsonArray list && Repaired(list) is { } fixedList)
                {
                    document.Values[setting.Key] = fixedList;
                    document.Dirty = true;
                    repaired = true;
                    _log.Warning(Loc.T($"Починен список '{setting.Key}' плагина {manifest.Id}", $"Repaired the list '{setting.Key}' of {manifest.Id}"));
                }

                if (setting.Default is null || document.Values.ContainsKey(setting.Key)) continue;
                document.Values[setting.Key] = JsonNode.Parse(JsonFormatter.Default.Format(setting.Default));
            }
        }

        if (repaired) _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Чинит список, в котором несколько значений слиплись в одну строку. Так сохраняла их версия 0.1.0: поле WinUI
    /// разделяет строки символом \r, а хаб делил только по \n.
    /// </summary>
    /// <returns>Исправленный список или <see langword="null"/>, если он в порядке.</returns>
    private static JsonArray? Repaired(JsonArray list)
    {
        var values = list.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).ToList();
        if (!values.Any(text => text is not null && text.AsSpan().IndexOfAny('\r', '\n') >= 0)) return null;

        var result = new JsonArray();
        foreach (var (item, text) in list.Zip(values))
        {
            if (text is null)
            {
                result.Add(item?.DeepClone());
                continue;
            }

            foreach (var part in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) result.Add(part);
        }

        return result;
    }

    /// <summary>Читает значение.</summary>
    /// <param name="owner">Id плагина.</param>
    /// <param name="key">Ключ.</param>
    /// <returns>Копия значения или <see langword="null"/>.</returns>
    public JsonNode? Get(string owner, string key)
    {
        var document = Open(owner);
        lock (document) return document.Values.TryGetPropertyValue(key, out var value) ? value?.DeepClone() : null;
    }

    /// <summary>Копия всего документа.</summary>
    /// <param name="owner">Id плагина.</param>
    public JsonObject Snapshot(string owner)
    {
        var document = Open(owner);
        lock (document) return document.Values.DeepClone().AsObject();
    }

    /// <summary>Записывает значение. Если оно не изменилось, ничего не происходит.</summary>
    /// <param name="owner">Id плагина.</param>
    /// <param name="key">Ключ.</param>
    /// <param name="value">Значение.</param>
    public void Set(string owner, string key, JsonNode? value)
    {
        var document = Open(owner);
        lock (document)
        {
            if (JsonNode.DeepEquals(document.Values[key], value) && document.Values.ContainsKey(key)) return;
            document.Values[key] = value?.DeepClone();
            document.Dirty = true;
        }

        _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        Changed?.Invoke(owner, key);
    }

    /// <summary>Записывает на диск всё, что изменилось.</summary>
    public void Flush()
    {
        // Таймер и завершение хаба могут прийти сюда одновременно, а временный файл у документа один.
        lock (_flushGate)
        {
            foreach (var document in _documents.Values)
            {
                string text;
                lock (document)
                {
                    if (!document.Dirty) continue;
                    document.Dirty = false;
                    text = document.Values.ToJsonString(Jsonc.WriteOptions);
                }

                try
                {
                    // Через временный файл, чтобы сбой на середине записи не съел настройки.
                    var temporary = document.Path + ".tmp";
                    File.WriteAllText(temporary, text);
                    File.Move(temporary, document.Path, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Файл мог держать антивирус. Значение не теряем: уйдёт при следующей записи или при завершении.
                    lock (document) document.Dirty = true;
                    _log.Error(Loc.T($"Не удалось сохранить '{document.Path}': {ex.Message}", $"Could not save '{document.Path}': {ex.Message}"));
                }
            }
        }
    }

    /// <summary>Останавливает таймер и сохраняет несохранённое.</summary>
    public void Dispose()
    {
        _timer.Dispose();
        Flush();
    }

    /// <summary>Один файл конфига в памяти. Сам объект служит блокировкой для своих значений.</summary>
    private sealed class Document(string path, JsonObject values)
    {
        public string Path { get; } = path;
        public JsonObject Values { get; } = values;
        public bool Dirty { get; set; }
    }
}
