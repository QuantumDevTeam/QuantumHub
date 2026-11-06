using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Plugin;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.PicoAntiSleep;

/// <summary>Состояние VR-сессии из возможности vr.session. У плагинов общий только формат JSON.</summary>
/// <param name="Active">Запущен ли SteamVR.</param>
public sealed record VrSession(bool Active);

/// <summary>Ответ sleep.get и sleep.set и тело события vr.pico.sleep.changed.</summary>
/// <param name="Enabled">Включён ли режим.</param>
/// <param name="Status">Что сейчас происходит.</param>
public sealed record SleepState(bool Enabled, string Status);

/// <summary>Запрос sleep.set.</summary>
/// <param name="Enabled">Включить или выключить.</param>
public sealed record SleepRequest(bool Enabled);

/// <summary>
/// Аддон к VR Switcher: не даёт шлему Pico заснуть. Здесь только связь с хабом, цикл лежит в
/// <see cref="AntiSleepEngine"/>. Про SteamVR он узнаёт от другого плагина, через возможность vr.session.
/// </summary>
[HubPlugin]
public sealed class PicoAntiSleepPlugin : IHubPlugin, IUiProvider
{
    /// <summary>Топик смены состояния.</summary>
    public const string StateTopic = "vr.pico.sleep.changed";

    private IPluginContext _context = null!;
    private AntiSleepEngine _engine = null!;
    private IDisposable? _subscription;

    /// <inheritdoc/>
    public async Task StartAsync(IPluginContext context, CancellationToken token)
    {
        _context = context;
        var runner = new AdbProcessRunner(() => AdbLocator.Find(context.Config.Get<string>("adbPath"), context.Paths.InstallDir));
        _engine = new AntiSleepEngine(new AdbClient(runner, Task.Delay), ReadOptions, Task.Delay, context.Logger.Info);
        _engine.StatusChanged += PushState;
        PushState();

        context.Bus.Handle("sleep.get", (_, _) => ValueTask.FromResult(HubPayload.Json(State())));
        context.Bus.Handle("sleep.set", async (request, _) =>
        {
            if (request.Payload.AsJson<SleepRequest>()?.Enabled == true) await _engine.StartAsync();
            else await _engine.StopAsync();
            return HubPayload.Json(State());
        });

        _subscription = await context.Bus.SubscribeAsync("vr.session.changed", e => OnSession(e.Payload.AsJson<VrSession>()), token);

        var current = await context.Bus.CallAsync("cap:vr.session", "session.get", token: token);
        OnSession(current.AsJson<VrSession>());
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken token)
    {
        _subscription?.Dispose();
        await _engine.StopAsync();
    }

    private SleepState State() => new(_engine.IsRunning, _engine.Status);

    private AntiSleepOptions ReadOptions()
    {
        var seconds = Math.Clamp(_context.Config.Get("intervalSeconds", 60d), 10, 3600);
        return new AntiSleepOptions(_context.Config.Get("address", "192.168.1.150")!, TimeSpan.FromSeconds(seconds));
    }

    private void OnSession(VrSession? session)
    {
        if (session is null || !_context.Config.Get("autoWithVr", false)) return;
        _ = session.Active ? _engine.StartAsync() : _engine.StopAsync();
    }

    private void PushState()
    {
        _context.Ui.Set("status", _engine.Status);
        _ = _context.Bus.PublishAsync(StateTopic, HubPayload.Json(State()));
    }

    /// <inheritdoc/>
    public IEnumerable<UiPage> GetPages()
    {
        yield return Ui.Page("main", Loc.T("Сон", "Sleep"), "E708",
            Ui.Card(Loc.T("Состояние", "Status"), null, Ui.BoundText("status")),
            Ui.Row(
                Ui.Button("start", Loc.T("Включить", "Turn on"), accent: true),
                Ui.Button("stop", Loc.T("Выключить", "Turn off"))));
    }

    /// <inheritdoc/>
    public void OnUiEvent(UiEvent uiEvent)
    {
        switch (uiEvent.NodeId)
        {
            case "start":
                _ = _engine.StartAsync();
                break;
            case "stop":
                _ = _engine.StopAsync();
                break;
        }
    }
}
