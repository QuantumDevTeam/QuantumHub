using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.MicSwitcher;

/// <summary>Микрофоны через возможность audio.devices. Кто её предоставляет, этому плагину неважно.</summary>
/// <param name="bus">Шина хаба.</param>
public sealed class BusMicAudio(IHubBus bus) : IMicAudio
{
    private const string Target = "cap:audio.devices";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MicLevel>> ReadAsync(IReadOnlyList<string> listen) =>
        (await bus.CallAsync(Target, "mics.levels", HubPayload.Json(new { ids = listen }))).AsJson<List<MicLevel>>() ?? [];

    /// <inheritdoc/>
    public async Task SetDefaultAsync(string deviceId) =>
        await bus.CallAsync(Target, "devices.set", HubPayload.Json(new { id = deviceId }));
}

/// <summary>Подсказка через возможность ui.notifications.</summary>
/// <param name="bus">Шина хаба.</param>
public sealed class BusMicPrompt(IHubBus bus) : IMicPrompt
{
    /// <summary>Ключ окошка: по нему плагин узнаёт свои кнопки.</summary>
    public const string Key = "mic-switcher";

    /// <summary>Кнопка «переключить».</summary>
    public const string SwitchAction = "switch";

    /// <summary>Кнопка «не сейчас».</summary>
    public const string DismissAction = "dismiss";

    private const string Target = "cap:ui.notifications";

    /// <inheritdoc/>
    public async Task ShowAsync(MicState mic, TimeSpan duration) =>
        await bus.CallAsync(Target, "notify.show", HubPayload.Json(new
        {
            key = Key,
            title = Loc.T("Сигнал на другом микрофоне", "Signal on another microphone"),
            subtitle = mic.Name,
            durationSeconds = duration.TotalSeconds,
            actions = new[]
            {
                new { id = SwitchAction, label = Loc.T("Переключить", "Switch") },
                new { id = DismissAction, label = Loc.T("Не сейчас", "Not now") },
            },
        }));

    /// <inheritdoc/>
    public async Task HideAsync() =>
        await bus.CallAsync(Target, "notify.hide", HubPayload.Json(new { key = Key }));
}

/// <summary>Узнаёт о VR от возможности vr.session. Её может и не быть: тогда VR просто не учитывается.</summary>
public sealed class BusVrState : IVrState, IDisposable
{
    private sealed record VrSession(bool Active);

    private volatile bool _active;
    private IDisposable? _subscription;

    /// <inheritdoc/>
    public bool Active => _active;

    /// <summary>Подписывается на изменения и читает текущее состояние.</summary>
    /// <param name="bus">Шина хаба.</param>
    /// <param name="token">Отмена.</param>
    public async Task StartAsync(IHubBus bus, CancellationToken token)
    {
        _subscription = await bus.SubscribeAsync("vr.session.changed", e => Apply(e.Payload), token);
        try
        {
            Apply(await bus.CallAsync("cap:vr.session", "session.get", token: token));
        }
        catch (HubCallException)
        {
            // VR Switcher не установлен. Если он появится позже, о сессии расскажет событие.
        }
    }

    private void Apply(HubPayload payload) => _active = payload.AsJson<VrSession>()?.Active == true;

    /// <summary>Снимает подписку.</summary>
    public void Dispose() => _subscription?.Dispose();
}
