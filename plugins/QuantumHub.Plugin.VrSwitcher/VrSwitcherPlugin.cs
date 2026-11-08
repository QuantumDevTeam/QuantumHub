using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Plugin;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.VrSwitcher;

/// <summary>Ответ session.get и тело события vr.session.changed.</summary>
/// <param name="Active">Запущен ли SteamVR.</param>
/// <param name="Preset">Выбранная гарнитура.</param>
/// <param name="OnHeadset">Переведён ли звук на гарнитуру этим плагином.</param>
/// <param name="Status">Что сейчас происходит, для показа человеку.</param>
public sealed record VrSession(bool Active, string Preset, bool OnHeadset, string Status);

/// <summary>Запрос audio.target.set.</summary>
/// <param name="Target">"headset" или "desktop".</param>
public sealed record AudioTarget(string Target);

/// <summary>
/// Определяет SteamVR и переводит звук на гарнитуру. Здесь только связь с хабом: таймер, настройки и
/// интерфейс. Вся логика лежит в <see cref="VrAudioEngine"/>, а сам звук в возможности audio.devices.
/// </summary>
[HubPlugin]
public sealed class VrSwitcherPlugin : IHubPlugin, IDependencyAware, IUiProvider
{
    /// <summary>Топик начала и конца VR-сессии. Тело: <see cref="VrSession"/> в JSON.</summary>
    public const string SessionTopic = "vr.session.changed";

    private sealed record DeviceName(string Id, string Name);

    private sealed record Current(DeviceName? Playback, DeviceName? Recording);

    private readonly ActivityVrProbe _probe = new();
    private IPluginContext _context = null!;
    private VrAudioEngine _engine = null!;
    private IDisposable? _subscription;
    private CancellationTokenSource? _stop;
    private Task? _loop;

    /// <inheritdoc/>
    public async Task StartAsync(IPluginContext context, CancellationToken token)
    {
        _context = context;
        _engine = new VrAudioEngine(new BusAudioDevices(context.Bus), _probe, new ConfigMemory(context.Config), ReadOptions, context.Logger.Info);

        _engine.StatusChanged += PushState;
        _engine.SessionChanged += OnSessionChanged;
        context.Config.Changed += OnConfigChanged;

        context.Bus.Handle("session.get", (_, _) => ValueTask.FromResult(HubPayload.Json(Session())));
        context.Bus.Handle("audio.target.set", async (request, _) =>
        {
            switch (request.Payload.AsJson<AudioTarget>()?.Target)
            {
                case "headset":
                    await _engine.ApplyNowAsync();
                    break;
                case "desktop":
                    await _engine.RestoreNowAsync();
                    break;
                default:
                    throw new HubCallException(StatusCode.InvalidArgument, Loc.T("Цель должна быть \"headset\" или \"desktop\".", "Target must be \"headset\" or \"desktop\"."));
            }

            return HubPayload.Json(Session());
        });

        await _probe.StartAsync(context.Bus, token);
        _subscription = await context.Bus.SubscribeAsync("audio.default.changed", e => ShowCurrent(e.Payload), token);
        ShowCurrent(await context.Bus.CallAsync("cap:audio.devices", "devices.current", token: token));

        PushState();
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    /// <inheritdoc/>
    public async Task OnDependencyRestartedAsync(string providerId, CancellationToken token)
    {
        await _probe.RefreshAsync(_context.Bus, token);
        ShowCurrent(await _context.Bus.CallAsync("cap:audio.devices", "devices.current", token: token));
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken token)
    {
        _subscription?.Dispose();
        _probe.Dispose();
        if (_stop is not null) await _stop.CancelAsync();
        if (_loop is not null) await _loop;
        await _engine.ShutdownAsync();
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _engine.PollAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _context.Logger.Error(Loc.T("Проверка SteamVR не удалась", "The SteamVR check failed"), ex);
                }

                var seconds = Math.Clamp(_context.Config.Get("pollSeconds", 2d), 1, 60);
                await Task.Delay(TimeSpan.FromSeconds(seconds), token);
            }
        }
        catch (OperationCanceledException)
        {
            // Плагин останавливается.
        }
    }

    private VrAudioOptions ReadOptions()
    {
        var config = _context.Config;
        var preset = VrAudioPreset.Resolve(config.Get<string>("preset"), config.Get<string>("customPlayback"), config.Get<string>("customRecording"));
        return new VrAudioOptions(preset, config.Get("autoSwitch", true), config.Get("pcPlayback", string.Empty)!, config.Get("pcRecording", string.Empty)!);
    }

    private VrSession Session() =>
        new(_engine.SessionActive, ReadOptions().Preset.Name, _engine.OnHeadset, _engine.Status);

    private void OnSessionChanged(bool active)
    {
        PushState();
        _ = _context.Bus.PublishAsync(SessionTopic, HubPayload.Json(Session()));
    }

    private void OnConfigChanged(string key)
    {
        if (key is "preset" or "customPlayback" or "customRecording") _engine.OnPresetChanged();
    }

    private void ShowCurrent(HubPayload payload)
    {
        var current = payload.AsJson<Current>();
        if (current?.Playback is { } playback) _engine.NoteDefault(AudioFlow.Playback, new AudioDevice(playback.Id, playback.Name));
        if (current?.Recording is { } recording) _engine.NoteDefault(AudioFlow.Recording, new AudioDevice(recording.Id, recording.Name));
        _context.Ui.Set("playback", current?.Playback?.Name ?? Loc.T("нет", "none"));
        _context.Ui.Set("recording", current?.Recording?.Name ?? Loc.T("нет", "none"));
    }

    private void PushState()
    {
        _context.Ui.Set("session", _engine.SessionActive ? Loc.T("запущен", "running") : Loc.T("не запущен", "not running"));
        _context.Ui.Set("status", _engine.Status);
    }

    /// <inheritdoc/>
    public IEnumerable<UiPage> GetPages()
    {
        yield return Ui.Page("main", "VR", "E7F6",
            Ui.KeyValue("SteamVR", "session"),
            Ui.KeyValue(Loc.T("Наушники сейчас", "Current output"), "playback"),
            Ui.KeyValue(Loc.T("Микрофон сейчас", "Current microphone"), "recording"),
            Ui.Card(Loc.T("Состояние", "Status"), null, Ui.BoundText("status")),
            Ui.Row(
                Ui.Button("apply", Loc.T("На гарнитуру", "To headset"), accent: true),
                Ui.Button("restore", Loc.T("Вернуть обычный звук", "Back to desktop sound"))));
    }

    /// <inheritdoc/>
    public void OnUiEvent(UiEvent uiEvent)
    {
        switch (uiEvent.NodeId)
        {
            case "apply":
                _ = _engine.ApplyNowAsync();
                break;
            case "restore":
                _ = _engine.RestoreNowAsync();
                break;
        }
    }

    /// <summary>Хранит прежние устройства в конфиге плагина.</summary>
    private sealed class ConfigMemory(IPluginConfig config) : IVrAudioMemory
    {
        public string? Playback
        {
            get => Read("savedPlayback");
            set => config.Set("savedPlayback", value ?? string.Empty);
        }

        public string? Recording
        {
            get => Read("savedRecording");
            set => config.Set("savedRecording", value ?? string.Empty);
        }

        public string? DesktopPlayback
        {
            get => Read("desktopPlayback");
            set => config.Set("desktopPlayback", value ?? string.Empty);
        }

        public string? DesktopRecording
        {
            get => Read("desktopRecording");
            set => config.Set("desktopRecording", value ?? string.Empty);
        }

        private string? Read(string key) => config.Get<string>(key) is { Length: > 0 } value ? value : null;
    }
}
