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

    /// <summary>Открывает файл журнала на сегодня.</summary>
    /// <param name="logsDir">Папка журналов.</param>
    /// <param name="echoToConsole">Дублировать в консоль.</param>
    public HubLog(string logsDir, bool echoToConsole = false)
    {
        EchoToConsole = echoToConsole;
        var path = Path.Combine(logsDir, $"hub-{DateTime.Now:yyyy-MM-dd}.log");
        _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    /// <summary>Дублируются ли записи в консоль.</summary>
    public bool EchoToConsole { get; }

    /// <summary>Новая запись. Так журнал попадает в GUI.</summary>
    public event Action<LogEntry>? Written;

    /// <summary>Добавляет запись.</summary>
    /// <param name="level">"debug", "info", "warning" или "error".</param>
    /// <param name="source">Кто пишет: "hub" или id плагина.</param>
    /// <param name="message">Текст.</param>
    public void Write(string level, string source, string message)
    {
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
