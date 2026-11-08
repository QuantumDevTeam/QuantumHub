using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Plugin;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.MicSwitcher;

/// <summary>Ответ mic.current.</summary>
/// <param name="Id">Id микрофона.</param>
/// <param name="Name">Имя для показа.</param>
public sealed record CurrentMic(string Id, string Name);

/// <summary>Ответ mode.set.</summary>
/// <param name="Mode">"off", "hint" или "auto".</param>
/// <param name="Status">Что сейчас происходит, для показа человеку.</param>
public sealed record MicSwitcherState(string Mode, string Status);

/// <summary>Запрос mode.set.</summary>
/// <param name="Mode">"off", "hint" или "auto".</param>
public sealed record SetModeRequest(string Mode);

/// <summary>Запрос mic.switch.</summary>
/// <param name="Id">Id микрофона.</param>
public sealed record SwitchRequest(string Id);

/// <summary>
/// Замечает, что заговорили в другой микрофон, и переключает на него. Здесь только связь с хабом: таймер, настройки и
/// интерфейс. Вся логика лежит в <see cref="MicSwitchEngine"/>, а сам звук в возможности audio.devices.
/// </summary>
[HubPlugin]
public sealed class MicSwitcherPlugin : IHubPlugin, IDependencyAware, IUiProvider
{
    private const int PageRefreshMs = 500;
    private const string SwitchPrefix = "switch:";
    private const string ModePrefix = "mode:";

    private static readonly TimeSpan PageReadTimeout = TimeSpan.FromSeconds(2);

    private sealed record NotificationClick(string Key, string Action);

    private readonly BusVrState _vr = new();
    private IPluginContext _context = null!;
    private MicSwitchEngine _engine = null!;
    private IDisposable? _subscription;
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private string? _lastError;

    /// <inheritdoc/>
    public async Task StartAsync(IPluginContext context, CancellationToken token)
    {
        _context = context;
        _engine = new MicSwitchEngine(new BusMicAudio(context.Bus), new BusMicPrompt(context.Bus), _vr, ReadOptions, TimeProvider.System, context.Logger.Info);
        _engine.StatusChanged += PushState;

        context.Bus.Handle("mic.current", async (_, _) =>
        {
            await _engine.RefreshAsync();
            return HubPayload.Json(_engine.Mics.FirstOrDefault(mic => mic.IsDefault) is { } mic ? new CurrentMic(mic.Id, mic.Name) : null);
        });
        context.Bus.Handle("mic.levels", async (_, _) =>
        {
            await _engine.RefreshAsync();
            return HubPayload.Json(_engine.Mics);
        });
        context.Bus.Handle("mic.switch", async (request, _) =>
        {
            var id = request.Payload.AsJson<SwitchRequest>()?.Id;
            if (string.IsNullOrEmpty(id)) throw new HubCallException(StatusCode.InvalidArgument, Loc.T("Нужен id микрофона.", "A microphone id is required."));

            await _engine.SwitchAsync(id);
            return HubPayload.Empty;
        });
        context.Bus.Handle("mode.set", (request, _) =>
        {
            var mode = request.Payload.AsJson<SetModeRequest>()?.Mode;
            if (mode is not ("off" or "hint" or "auto"))
                throw new HubCallException(StatusCode.InvalidArgument, Loc.T("Режим должен быть \"off\", \"hint\" или \"auto\".", "Mode must be \"off\", \"hint\" or \"auto\"."));

            context.Config.Set("mode", mode);
            return ValueTask.FromResult(HubPayload.Json(new MicSwitcherState(mode, _engine.Status)));
        });

        await _vr.StartAsync(context.Bus, token);
        _subscription = await context.Bus.SubscribeAsync("notify.action", e => OnNotificationClick(e.Payload), token);

        PushState();
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    /// <inheritdoc/>
    public Task OnDependencyRestartedAsync(string providerId, CancellationToken token) => _vr.RefreshAsync(_context.Bus, token);

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken token)
    {
        _subscription?.Dispose();
        _vr.Dispose();
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
                await TryAsync(_engine.PollAsync);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(_context.Config.Get("pollMs", 250d), 100, 2000)), token);
            }
        }
        catch (OperationCanceledException)
        {
            // Плагин останавливается.
        }
    }

    /// <summary>Сбой чаще всего повторяется на каждой проверке, поэтому в журнал попадает только новый.</summary>
    private async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            _lastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex.Message != _lastError) _context.Logger.Warning(Loc.T($"Микрофоны: {ex.Message}", $"Microphones: {ex.Message}"));
            _lastError = ex.Message;
        }
    }

    private void OnNotificationClick(HubPayload payload)
    {
        var click = payload.AsJson<NotificationClick>();
        if (click?.Key != BusMicPrompt.Key) return;

        _ = TryAsync(click.Action == BusMicPrompt.SwitchAction ? _engine.AcceptHintAsync : _engine.DismissHintAsync);
    }

    private MicSwitchOptions ReadOptions()
    {
        var config = _context.Config;
        return new MicSwitchOptions
        {
            Mode = ParseMode(config.Get("mode", "off")),
            Threshold = config.Get("thresholdPercent", 10d) / 100,
            Hold = TimeSpan.FromMilliseconds(config.Get("holdMs", 1200d)),
            Cooldown = TimeSpan.FromSeconds(config.Get("cooldownSeconds", 20d)),
            HintTime = TimeSpan.FromSeconds(config.Get("hintSeconds", 12d)),
            RequireCurrentSilent = config.Get("requireCurrentSilent", true),
            WatchBluetooth = config.Get("watchBluetooth", false),
            Whitelist = config.Get("whitelist", new List<string>())!,
            Blacklist = config.Get("blacklist", new List<string>())!,
        };
    }

    private static MicMode ParseMode(string? mode) => mode switch
    {
        "hint" => MicMode.Hint,
        "auto" => MicMode.Auto,
        _ => MicMode.Off,
    };

    private void PushState()
    {
        _context.Ui.Set("mode", ParseMode(_context.Config.Get("mode", "off")) switch
        {
            MicMode.Hint => Loc.T("только подсказка", "hint only"),
            MicMode.Auto => Loc.T("автоматически", "automatic"),
            _ => Loc.T("выключен", "off"),
        });
        _context.Ui.Set("status", _engine.Status);
        _context.Ui.Set("lastAction", _engine.LastAction);
    }

    private void PushLevels()
    {
        var mics = _engine.Mics;
        _context.Ui.Set("current", mics.FirstOrDefault(mic => mic.IsDefault)?.Name ?? Loc.T("нет", "none"));
        _context.Ui.Set("levels", MicLevelsText.Format(mics, _context.Config.Get("thresholdPercent", 10d) / 100));
    }

    /// <inheritdoc/>
    public void OnLanguageChanged() => PushState();

    /// <inheritdoc/>
    public IEnumerable<UiPage> GetPages()
    {
        // Кнопки нужны сразу, а пока режим выключен, проверки стоят и список пуст.
        try
        {
            Task.Run(_engine.RefreshAsync).Wait(PageReadTimeout);
        }
        catch (AggregateException ex)
        {
            _context.Logger.Debug(ex.InnerException?.Message ?? ex.Message);
        }

        PushState();
        PushLevels();

        var switches = _engine.Mics.Select(mic => Ui.Button(SwitchPrefix + mic.Id, mic.Name, accent: mic.IsDefault)).ToArray();
        yield return Ui.Page("main", Loc.T("Микрофоны", "Microphones"), "E720",
            Ui.KeyValue(Loc.T("Режим", "Mode"), "mode"),
            Ui.KeyValue(Loc.T("Микрофон сейчас", "Current microphone"), "current"),
            Ui.KeyValue(Loc.T("Состояние", "Status"), "status"),
            Ui.KeyValue(Loc.T("Последнее действие", "Last action"), "lastAction"),
            Ui.Row(
                Ui.Button(ModePrefix + "off", Loc.T("Выключить", "Turn off")),
                Ui.Button(ModePrefix + "hint", Loc.T("Только подсказка", "Hint only")),
                Ui.Button(ModePrefix + "auto", Loc.T("Автоматически", "Automatic"))),
            Ui.Info(Loc.T("Микрофоны заняты, пока плагин их слушает", "Microphones stay in use while the plugin listens"),
                Loc.T("Уровень виден только при открытом потоке записи. Пока режим включён или открыта эта страница, Windows показывает значок микрофона. Звук никуда не идёт.",
                    "The level is only visible while a recording stream is open. While a mode is on or this page is open, Windows shows the microphone icon. The sound goes nowhere.")),
            Ui.Text(Loc.T("Уровни", "Levels"), "subtitle"),
            Ui.Text(Loc.T("Черта в шкале показывает порог.", "The mark on the scale shows the threshold."), "caption"),
            Ui.BoundText("levels", "mono"),
            Ui.Text(Loc.T("Переключить сейчас", "Switch now"), "subtitle"),
            Ui.Stack(switches),
            Ui.Live("live", PageRefreshMs));
    }

    /// <inheritdoc/>
    public void OnUiEvent(UiEvent uiEvent)
    {
        var node = uiEvent.NodeId;
        if (node == "live")
        {
            if (uiEvent.Event == "closed")
            {
                _engine.StopListening();
                return;
            }

            _engine.KeepListening();
            PushLevels();
        }
        else if (node.StartsWith(ModePrefix, StringComparison.Ordinal))
        {
            _context.Config.Set("mode", node[ModePrefix.Length..]);
            PushState();
        }
        else if (node.StartsWith(SwitchPrefix, StringComparison.Ordinal))
        {
            _ = TryAsync(() => _engine.SwitchAsync(node[SwitchPrefix.Length..]));
        }
    }
}
