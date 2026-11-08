namespace QuantumHub.Core;

/// <summary>Решает, поднимать ли упавший хаб снова. Если хаб падает раз за разом, перезапуск только мешает.</summary>
public sealed class CrashPolicy
{
    /// <summary>Сколько падений подряд ещё терпим.</summary>
    public const int MaxCrashes = 5;
    /// <summary>За какое время считаются падения. Хаб, проработавший дольше, начинает счёт заново.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    /// <summary>Самая длинная пауза перед запуском.</summary>
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    private readonly Queue<DateTime> _crashes = new();

    /// <summary>Отмечает падение.</summary>
    /// <param name="now">Когда оно случилось.</param>
    /// <returns>Сколько подождать перед запуском или <see langword="null"/>, если запускать больше не надо.</returns>
    public TimeSpan? NoteCrash(DateTime now)
    {
        _crashes.Enqueue(now);
        while (_crashes.Peek() < now - Window) _crashes.Dequeue();
        if (_crashes.Count > MaxCrashes) return null;

        var delay = TimeSpan.FromSeconds(Math.Pow(2, _crashes.Count - 1));
        return delay < MaxDelay ? delay : MaxDelay;
    }
}
