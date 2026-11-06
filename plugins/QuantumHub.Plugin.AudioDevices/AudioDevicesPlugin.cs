using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Plugin;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.AudioDevices;

/// <summary>
/// Отдаёт звуковые устройства наружу как возможность audio.devices. Смену устройства замечает, кто бы её
/// ни сделал: плагин, другая программа или сам пользователь.
/// </summary>
[HubPlugin]
public sealed class AudioDevicesPlugin : IHubPlugin, IUiProvider
{
    /// <summary>Топик смены устройства по умолчанию. Тело: <see cref="AudioCurrent"/> в JSON.</summary>
    public const string ChangedTopic = "audio.default.changed";

    private const int FadeStepMs = 20;

    private readonly WasapiAudioDevices _audio = new();
    private readonly Lock _gate = new();
    private IPluginContext _context = null!;
    private AudioCurrent? _last;
    private CancellationTokenSource? _stop;
    private Task? _loop;

    /// <inheritdoc/>
    public Task StartAsync(IPluginContext context, CancellationToken token)
    {
        _context = context;
        context.Bus.Handle("devices.list", (_, _) => ValueTask.FromResult(HubPayload.Json(_audio.List())));
        context.Bus.Handle("devices.current", (_, _) => ValueTask.FromResult(HubPayload.Json(_audio.Current())));
        context.Bus.Handle("devices.set", (request, _) =>
        {
            var id = request.Payload.AsJson<SetDefaultRequest>()?.Id;
            if (string.IsNullOrEmpty(id)) throw new HubCallException(StatusCode.InvalidArgument, "Нужен id устройства.");

            _audio.SetDefault(id);
            CheckForChange();
            return ValueTask.FromResult(HubPayload.Empty);
        });

        context.Bus.Handle("sessions.list", (_, _) => ValueTask.FromResult(HubPayload.Json(_audio.Sessions())));
        context.Bus.Handle("sessions.volume", async (request, token) =>
        {
            var change = request.Payload.AsJson<SetSessionVolumeRequest>();
            if (string.IsNullOrEmpty(change?.Process)) throw new HubCallException(StatusCode.InvalidArgument, "Нужно имя процесса.");

            await FadeAsync(change, token);
            return HubPayload.Empty;
        });

        CheckForChange();
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => WatchAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken token)
    {
        if (_stop is not null) await _stop.CancelAsync();
        if (_loop is not null) await _loop;
    }

    private async Task FadeAsync(SetSessionVolumeRequest change, CancellationToken token)
    {
        var start = _audio.Sessions().FirstOrDefault(session => session.Process.Equals(change.Process, StringComparison.OrdinalIgnoreCase));
        var steps = start is null ? 1 : Math.Max(1, change.FadeMs / FadeStepMs);
        for (var i = 1; i <= steps; i++)
        {
            var volume = start is null ? change.Volume : start.Volume + (change.Volume - start.Volume) * i / steps;
            _audio.SetSessionVolume(change.Process, volume);
            if (i < steps) await Task.Delay(FadeStepMs, token);
        }
    }

    private async Task WatchAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(_context.Config.Get("checkSeconds", 2d)), token);
                try
                {
                    CheckForChange();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _context.Logger.Warning($"Не удалось прочитать устройства: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Плагин останавливается.
        }
    }

    private void CheckForChange()
    {
        var current = _audio.Current();
        lock (_gate)
        {
            if (current == _last) return;
            _last = current;
        }

        _context.Ui.Set("playback", current.Playback?.Name ?? "нет");
        _context.Ui.Set("recording", current.Recording?.Name ?? "нет");
        _ = _context.Bus.PublishAsync(ChangedTopic, HubPayload.Json(current));
    }

    /// <inheritdoc/>
    public IEnumerable<UiPage> GetPages()
    {
        var all = _audio.List();
        _context.Ui.Set("playbackDevices", string.Join('\n', all.Playback.Select(device => device.Name)));
        _context.Ui.Set("recordingDevices", string.Join('\n', all.Recording.Select(device => device.Name)));
        _context.Ui.Set("sessions", string.Join('\n', _audio.Sessions().OrderByDescending(session => session.Peak)
            .Select(session => $"{session.Process}: громкость {session.Volume:P0}, уровень {session.Peak:0.00}")));

        yield return Ui.Page("main", "Устройства", "E767",
            Ui.KeyValue("Наушники сейчас", "playback"),
            Ui.KeyValue("Микрофон сейчас", "recording"),
            Ui.Text("Наушники и динамики", "subtitle"),
            Ui.BoundText("playbackDevices", "mono"),
            Ui.Text("Микрофоны", "subtitle"),
            Ui.BoundText("recordingDevices", "mono"),
            Ui.Text("Приложения со звуком, на момент открытия", "subtitle"),
            Ui.BoundText("sessions", "mono"));
    }

    /// <inheritdoc/>
    public void OnUiEvent(UiEvent uiEvent)
    {
    }
}
