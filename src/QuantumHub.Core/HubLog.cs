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
    private readonly StreamWriter _file;
    private bool _closed;

    /// <summary>Начало имени файла журнала.</summary>
    private const string FilePrefix = "hub-";
    /// <summary>Папка журналов.</summary>
    private readonly string _logsDir;
    /// <summary>Файл журнала на сегодня.</summary>
    private readonly string _currentFile;

    /// <summary>Открывает файл журнала на сегодня.</summary>
    /// <param name="logsDir">Папка журналов.</param>
    /// <param name="echoToConsole">Дублировать в консоль.</param>
    public HubLog(string logsDir, bool echoToConsole = false)
    {
        EchoToConsole = echoToConsole;
        _logsDir = logsDir;
        _currentFile = Path.Combine(logsDir, $"{FilePrefix}{DateTime.Now:yyyy-MM-dd}.log");
        _file = new StreamWriter(new FileStream(_currentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    /// <summary>Дублируются ли записи в консоль.</summary>
    public bool EchoToConsole { get; }

    /// <summary>Записи ниже этого уровня не сохраняются: "debug", "info", "warning" или "error".</summary>
    public string MinLevel { get; set; } = "debug";

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

        var line = $"{DateTime.Now:HH:mm:ss.fff} {level,-7} [{source}] {message}";
        lock (_recent)
        {
            _recent.Enqueue(entry);
            if (_recent.Count > MemoryCapacity) _recent.Dequeue();

            // Соединения закрываются в фоне и могут писать в журнал уже после завершения.
            if (!_closed) _file.WriteLine(line);
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
