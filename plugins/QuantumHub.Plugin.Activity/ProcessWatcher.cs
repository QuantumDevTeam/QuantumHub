namespace QuantumHub.Plugin.Activity;

/// <summary>Изменение состояния процесса. Тело события activity.process.changed.</summary>
/// <param name="Name">Имя процесса без .exe.</param>
/// <param name="Running">Запущен ли он теперь.</param>
public sealed record ProcessChange(string Name, bool Running);

/// <summary>
/// Помнит, за какими процессами просили следить, и находит изменения. Чистая логика: сам список
/// процессов передаётся снаружи.
/// </summary>
public sealed class ProcessWatcher
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, bool> _watched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Добавляет процессы к наблюдению. Изначально они считаются незапущенными.</summary>
    /// <param name="names">Имена без .exe.</param>
    public void Watch(IEnumerable<string> names)
    {
        lock (_gate)
        {
            foreach (var name in names) _watched.TryAdd(name, false);
        }
    }

    /// <summary>Проверяет все процессы и возвращает изменившиеся.</summary>
    /// <param name="isRunning">Запущен ли процесс.</param>
    public IReadOnlyList<ProcessChange> Poll(Func<string, bool> isRunning)
    {
        lock (_gate)
        {
            var changes = new List<ProcessChange>();
            foreach (var name in _watched.Keys.ToList())
            {
                var running = isRunning(name);
                if (running == _watched[name]) continue;
                _watched[name] = running;
                changes.Add(new ProcessChange(name, running));
            }

            return changes;
        }
    }
}

/// <summary>Определяет переход между «за компьютером» и «отошёл».</summary>
public sealed class IdleTracker
{
    /// <summary>Отошёл ли пользователь.</summary>
    public bool IsIdle { get; private set; }

    /// <summary>Обновляет состояние.</summary>
    /// <param name="idle">Сколько прошло с последнего ввода.</param>
    /// <param name="threshold">После какого времени считать отошедшим.</param>
    /// <returns><see langword="true"/>, если состояние изменилось.</returns>
    public bool Update(TimeSpan idle, TimeSpan threshold)
    {
        var now = idle >= threshold;
        if (now == IsIdle) return false;
        IsIdle = now;
        return true;
    }
}
