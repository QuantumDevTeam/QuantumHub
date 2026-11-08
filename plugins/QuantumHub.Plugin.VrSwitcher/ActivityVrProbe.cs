using QuantumHub.Contracts.Bus;

namespace QuantumHub.Plugin.VrSwitcher;

/// <summary>Узнаёт о запуске VR от возможности user.activity: сам плагин за процессами не следит.</summary>
public sealed class ActivityVrProbe : IVrProbe, IDisposable
{
    private sealed record VrState(bool Active);

    private volatile bool _running;
    private IDisposable? _subscription;

    /// <summary>Подписывается на изменения и читает текущее состояние.</summary>
    /// <param name="bus">Шина хаба.</param>
    /// <param name="token">Отмена.</param>
    public async Task StartAsync(IHubBus bus, CancellationToken token)
    {
        _subscription = await bus.SubscribeAsync("activity.vr.changed", e => Apply(e.Payload), token);
        Apply(await bus.CallAsync("cap:user.activity", "vr.get", token: token));
    }

    /// <summary>Читает состояние заново, когда плагин активности перезапустился.</summary>
    /// <param name="bus">Шина хаба.</param>
    /// <param name="token">Отмена.</param>
    public async Task RefreshAsync(IHubBus bus, CancellationToken token) =>
        Apply(await bus.CallAsync("cap:user.activity", "vr.get", token: token));

    private void Apply(HubPayload payload) => _running = payload.AsJson<VrState>()?.Active == true;

    /// <inheritdoc/>
    public bool IsRunning() => _running;

    /// <summary>Снимает подписку.</summary>
    public void Dispose() => _subscription?.Dispose();
}
