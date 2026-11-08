using QuantumHub.Contracts.Ui;
namespace QuantumHub.Plugin.AudioDevices;

/// <summary>Открытый поток записи одного микрофона.</summary>
public interface IMicMeter : IDisposable
{
    /// <summary>Поток оборвался: устройство отключили или его занял кто-то другой.</summary>
    bool Failed { get; }

    /// <summary>Текущий уровень от 0 до 1.</summary>
    double ReadPeak();
}

/// <summary>
/// Держит потоки записи открытыми, пока их спрашивают. Отдельного «закрыть» нет: микрофон отпускается сам, когда его
/// перестали спрашивать. Поэтому упавший или выключенный клиент не оставит микрофон занятым навсегда.
/// </summary>
/// <param name="open">Открывает поток по id устройства.</param>
/// <param name="clock">Часы.</param>
/// <param name="warn">Куда написать, что микрофон открыть не удалось.</param>
public sealed class MicMeterPool(Func<string, IMicMeter> open, TimeProvider clock, Action<string> warn) : IDisposable
{
    /// <summary>Сколько поток живёт после последнего вопроса.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(3);

    /// <summary>Как скоро снова пробовать микрофон, который не открылся или оборвался.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _open = [];
    private readonly Dictionary<string, DateTimeOffset> _retryAt = [];

    private sealed class Entry(IMicMeter meter, DateTimeOffset asked)
    {
        public IMicMeter Meter { get; } = meter;

        public DateTimeOffset Asked { get; set; } = asked;
    }

    /// <summary>Сколько потоков открыто.</summary>
    public int OpenCount
    {
        get
        {
            lock (_gate) return _open.Count;
        }
    }

    /// <summary>Читает уровни, при необходимости открывая потоки.</summary>
    /// <param name="ids">Какие микрофоны нужны.</param>
    /// <returns>Уровни тех, что удалось услышать.</returns>
    public IReadOnlyDictionary<string, double> Read(IEnumerable<string> ids)
    {
        var now = clock.GetUtcNow();
        var peaks = new Dictionary<string, double>();
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (Meter(id, now) is not { } entry) continue;

                entry.Asked = now;
                var peak = entry.Meter.ReadPeak();
                if (entry.Meter.Failed) Drop(id, now);
                else peaks[id] = peak;
            }

            Expire(now);
        }

        return peaks;
    }

    /// <summary>Закрывает потоки, которые давно не спрашивали.</summary>
    public void Expire()
    {
        lock (_gate) Expire(clock.GetUtcNow());
    }

    /// <summary>Закрывает все потоки.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _open.Values) entry.Meter.Dispose();
            _open.Clear();
        }
    }

    private Entry? Meter(string id, DateTimeOffset now)
    {
        if (_open.TryGetValue(id, out var entry)) return entry;
        if (_retryAt.TryGetValue(id, out var retry) && now < retry) return null;

        try
        {
            entry = new Entry(open(id), now);
            _open[id] = entry;
            _retryAt.Remove(id);
            return entry;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _retryAt[id] = now + RetryDelay;
            warn(Loc.T($"Не удалось открыть микрофон {id}: {ex.Message}", $"Could not open the microphone {id}: {ex.Message}"));
            return null;
        }
    }

    private void Drop(string id, DateTimeOffset now)
    {
        _open.Remove(id, out var entry);
        entry?.Meter.Dispose();
        _retryAt[id] = now + RetryDelay;
    }

    private void Expire(DateTimeOffset now)
    {
        foreach (var (id, entry) in _open.Where(pair => now - pair.Value.Asked >= Lease).ToList())
        {
            entry.Meter.Dispose();
            _open.Remove(id);
        }
    }
}
