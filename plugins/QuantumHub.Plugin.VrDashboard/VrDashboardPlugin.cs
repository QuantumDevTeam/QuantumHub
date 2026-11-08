using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Plugin;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.VrDashboard;

/// <summary>Состояние из возможности user.activity.</summary>
/// <param name="Active">Запущен ли VR.</param>
public sealed record VrState(bool Active);

/// <summary>
/// Запускает панель SteamVR, пока работает VR. Сама панель — внешний процесс: библиотека OpenVR не должна жить в
/// хабе, и её сбой не должен его ронять. Здесь только запуск, настройки и страница состояния.
/// </summary>
[HubPlugin]
public sealed class VrDashboardPlugin : IHubPlugin, IDependencyAware, IUiProvider
{
    /// <summary>Как часто проверять, что панель жива.</summary>
    private static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(15);

    private IPluginContext _context = null!;
    private CompanionProcess _panel = null!;
    private IDisposable? _subscription;
    private CancellationTokenSource? _stop;
    private Task? _watch;
    private volatile bool _vrActive;

    /// <inheritdoc/>
    public async Task StartAsync(IPluginContext context, CancellationToken token)
    {
        _context = context;
        _panel = new CompanionProcess(Path.Combine(context.Paths.InstallDir, "companion", "QuantumHub.VrDashboard.exe"), context);

        _subscription = await context.Bus.SubscribeAsync("activity.vr.changed", e => OnVr(e.Payload.AsJson<VrState>()), token);
        OnVr((await context.Bus.CallAsync("cap:user.activity", "vr.get", token: token)).AsJson<VrState>());

        _stop = new CancellationTokenSource();
        _watch = Task.Run(() => WatchAsync(_stop.Token), CancellationToken.None);
    }

    /// <inheritdoc/>
    public async Task OnDependencyRestartedAsync(string providerId, CancellationToken token) =>
        OnVr((await _context.Bus.CallAsync("cap:user.activity", "vr.get", token: token)).AsJson<VrState>());

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken token)
    {
        _subscription?.Dispose();
        if (_stop is not null) await _stop.CancelAsync();
        if (_watch is not null) await _watch;
        _panel.Dispose();
    }

    /// <summary>VR запустился или закрылся.</summary>
    private void OnVr(VrState? state)
    {
        _vrActive = state?.Active == true;
        if (_vrActive) _panel.Ensure();
        else _panel.Stop();
        PushState();
    }

    /// <summary>Возвращает панель, если она упала, пока VR работает.</summary>
    private async Task WatchAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(WatchEvery);
            while (await timer.WaitForNextTickAsync(token))
            {
                if (_vrActive) _panel.Ensure();
                PushState();
            }
        }
        catch (OperationCanceledException)
        {
            // Плагин останавливается.
        }
    }

    private void PushState() => _context.Ui.Set("state",
        !_vrActive ? Loc.T("Ждём SteamVR", "Waiting for SteamVR")
        : _panel.IsRunning ? Loc.T("Панель открыта в меню SteamVR", "The panel is in the SteamVR menu")
        : Loc.T("Панель не запущена, смотри logs\\vr-dashboard.log", "The panel is not running, see logs\\vr-dashboard.log"));

    /// <inheritdoc/>
    public IEnumerable<UiPage> GetPages()
    {
        yield return Ui.Page("main", Loc.T("Панель", "Panel"), "E7F4",
            Ui.Card(Loc.T("Состояние", "Status"), null, Ui.BoundText("state")),
            Ui.Text(Loc.T("Панель появляется в меню SteamVR сама, когда он запущен. Что в ней показывать, настраивается на соседней вкладке.",
                "The panel appears in the SteamVR menu by itself while it runs. Choose what it shows on the next tab."), "caption"));
    }

    /// <inheritdoc/>
    public void OnUiEvent(UiEvent uiEvent) { }

    /// <inheritdoc/>
    public void OnLanguageChanged() => PushState();
}
