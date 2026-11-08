using QuantumHub.Contracts.Plugin;
using QuantumHub.Contracts.Proto;

namespace QuantumHub.Core;

/// <summary>Журнал хаба: файл на каждый день, недавние записи в памяти и событие для GUI.</summary>
public sealed class HubLog : IDisposable
{
    /// <summary>Сколько записей держать в памяти.</summary>
    private const int MemoryCapacity = 2000;

    /// <summary>Недавние записи. Заодно служит блокировкой для файла.</summary>
    private readonly Queue<LogEntry> _recent = new();
    private StreamWriter _file;
    private bool _closed;

    /// <summary>Начало имени файла журнала.</summary>
    private const string FilePrefix = "hub-";
    /// <summary>Папка журналов.</summary>
    private readonly string _logsDir;
    /// <summary>Файл журнала на сегодня.</summary>
    private string _currentFile;
    /// <summary>Какой день пишется в файл. Хаб работает неделями, и новый день начинает новый файл.</summary>
    private DateOnly _currentDay;
    /// <summary>Сколько дней хранить файлы. 0 значит всегда.</summary>
    private int _retentionDays;

    /// <summary>Открывает файл журнала на сегодня.</summary>
    /// <param name="logsDir">Папка журналов.</param>
    /// <param name="echoToConsole">Дублировать в консоль.</param>
    public HubLog(string logsDir, bool echoToConsole = false)
    {
        EchoToConsole = echoToConsole;
        _logsDir = logsDir;
        _currentDay = DateOnly.FromDateTime(DateTime.Now);
        (_file, _currentFile) = Open(_currentDay);
    }

    /// <summary>Открывает файл дня.</summary>
    private (StreamWriter File, string Path) Open(DateOnly day)
    {
        var path = Path.Combine(_logsDir, $"{FilePrefix}{day:yyyy-MM-dd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return (new StreamWriter(stream) { AutoFlush = true }, path);
    }

    /// <summary>Начинает файл нового дня и убирает старые. Вызывается под блокировкой.</summary>
    /// <param name="now">Текущее время.</param>
    private void RollOver(DateTime now)
    {
        var day = DateOnly.FromDateTime(now);
        if (day == _currentDay) return;

        try
        {
            var (file, path) = Open(day);
            _file.Dispose();
            _file = file;
            _currentFile = path;
            _currentDay = day;
            PruneFiles(_retentionDays);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Продолжаем писать в старый файл: потерять запись хуже, чем положить её не в тот день.
        }
    }

    /// <summary>Текущее время. Тесты подменяют его, чтобы не ждать полуночи.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>Дублируются ли записи в консоль.</summary>
    public bool EchoToConsole { get; }

    /// <summary>Уровень записи по умолчанию.</summary>
    public const string DefaultLevel = "info";
    /// <summary>Сколько дней хранить файлы по умолчанию. Хаб работает всегда, и без предела журнал рос бы вечно.</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>Записи ниже этого уровня не сохраняются: "debug", "info", "warning" или "error".</summary>
    public string MinLevel { get; set; } = DefaultLevel;

    /// <summary>Вес уровня.</summary>
    private static int Rank(string level) => level switch
    {
        "debug" => 0,
        "warning" => 2,
        "error" => 3,
        _ => 1,
    };

    /// <summary>Удаляет файлы журнала старше указанного срока. Сегодняшний файл не трогает.</summary>
    /// <param name="days">Сколько дней хранить. 0 значит хранить всегда.</param>
    /// <returns>Сколько файлов удалено.</returns>
    public int Prune(int days)
    {
        lock (_recent)
        {
            _retentionDays = days;
            return PruneFiles(days);
        }
    }

    /// <summary>Удаляет старые файлы. Вызывается под блокировкой.</summary>
    private int PruneFiles(int days)
    {
        if (days <= 0) return 0;

        var removed = 0;
        var limit = DateTime.Now.AddDays(-days);
        foreach (var file in new DirectoryInfo(_logsDir).EnumerateFiles($"{FilePrefix}*.log"))
        {
            if (file.LastWriteTime >= limit || string.Equals(file.FullName, Path.GetFullPath(_currentFile), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                file.Delete();
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Файл открыт другой программой, удалится в следующий раз.
            }
        }

        return removed;
    }

    /// <summary>Новая запись. Так журнал попадает в GUI.</summary>
    public event Action<LogEntry>? Written;

    /// <summary>Добавляет запись.</summary>
    /// <param name="level">"debug", "info", "warning" или "error".</param>
    /// <param name="source">Кто пишет: "hub" или id плагина.</param>
    /// <param name="message">Текст.</param>
    public void Write(string level, string source, string message)
    {
        if (Rank(level) < Rank(MinLevel)) return;

        var entry = new LogEntry
        {
            UnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Level = level,
            Source = source,
            Message = message,
        };

        var now = Clock();
        var line = $"{now:HH:mm:ss.fff} {level,-7} [{source}] {message}";
        lock (_recent)
        {
            _recent.Enqueue(entry);
            if (_recent.Count > MemoryCapacity) _recent.Dequeue();

            // Соединения закрываются в фоне и могут писать в журнал уже после завершения.
            if (!_closed)
            {
                RollOver(now);
                _file.WriteLine(line);
            }
        }

        if (EchoToConsole) Console.WriteLine(line);
        Written?.Invoke(entry);
    }

    /// <summary>Запись уровня info от имени хаба.</summary>
    /// <param name="message">Текст.</param>
    public void Info(string message) => Write("info", "hub", message);
    /// <summary>Запись уровня warning от имени хаба.</summary>
    /// <param name="message">Текст.</param>
    public void Warning(string message) => Write("warning", "hub", message);
    /// <summary>Запись уровня error от имени хаба.</summary>
    /// <param name="message">Текст.</param>
    public void Error(string message) => Write("error", "hub", message);

    /// <summary>Копия недавних записей.</summary>
    public LogEntry[] Recent()
    {
        lock (_recent) return _recent.ToArray();
    }

    /// <summary>Журнал от имени плагина.</summary>
    /// <param name="source">Id плагина.</param>
    public IPluginLogger For(string source) => new SourceLogger(this, source);

    /// <summary>Закрывает файл. Поздние записи останутся только в памяти.</summary>
    public void Dispose()
    {
        lock (_recent)
        {
            _closed = true;
            _file.Dispose();
        }
    }

    /// <summary><see cref="IPluginLogger"/>, подставляющий имя источника.</summary>
    private sealed class SourceLogger(HubLog log, string source) : IPluginLogger
    {
        /// <inheritdoc/>
        public void Debug(string message) => log.Write("debug", source, message);
        /// <inheritdoc/>
        public void Info(string message) => log.Write("info", source, message);
        /// <inheritdoc/>
        public void Warning(string message) => log.Write("warning", source, message);

        /// <inheritdoc/>
        public void Error(string message, Exception? exception = null) =>
            log.Write("error", source, exception is null ? message : $"{message}: {exception}");
    }
}
