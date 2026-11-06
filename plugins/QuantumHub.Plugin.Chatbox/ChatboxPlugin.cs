using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Plugin;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.Chatbox;

/// <summary>Состояние плагина. Ответ chatbox.state и тело события chatbox.changed.</summary>
/// <param name="GameRunning">Запущен ли VRChat.</param>
/// <param name="Sending">Включена ли отправка.</param>
/// <param name="Status">Личный статус.</param>
/// <param name="Statuses">Сохранённые статусы, новые первыми.</param>
/// <param name="Preview">Что сейчас показывается в чатбоксе.</param>
public sealed record ChatboxState(bool GameRunning, bool Sending, string Status, IReadOnlyList<string> Statuses, string Preview);

/// <summary>
/// Чатбокс VRChat. Сам плагин знает только про личный статус и время, остальное берёт у других: окно, VR и запуск
/// игры у user.activity, музыку у media.player, загрузку у system.stats, а отправляет через vrchat.osc.
/// </summary>
[HubPlugin]
public sealed class ChatboxPlugin : IHubPlugin, IUiProvider
{
    /// <summary>Топик изменения состояния. Его слушает мини-окно.</summary>
    public const string ChangedTopic = "chatbox.changed";

    private const string GameProcess = "VRChat";
    private const int MaxStatuses = 30;

    private sealed record Names(string[] Value);

    private sealed record ProcessChange(string Name, bool Running);

    private IPluginContext _context = null!;
    private ChatboxEngine _engine = null!;
    private CompanionProcess _panel = null!;
    private IDisposable? _subscription;
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private string _published = string.Empty;

    /// <inheritdoc/>
    public async Task StartAsync(IPluginContext context, CancellationToken token)
    {
        _context = context;
        _engine = new ChatboxEngine(new BusChatData(context.Bus, AwayAfter), new OscChatSink(context.Bus), ReadOptions);
        _panel = new CompanionProcess(Path.Combine(context.Paths.InstallDir, "companion", "QuantumHub.VrChatPanel.exe"), context);

        context.Bus.Handle("chatbox.state", (_, _) => ValueTask.FromResult(HubPayload.Json(State())));
        context.Bus.Handle("chatbox.status.set", (request, _) =>
        {
            SetStatus(request.Payload.AsText());
            return ValueTask.FromResult(HubPayload.Empty);
        });
        context.Bus.Handle("chatbox.status.remove", (request, _) =>
        {
            var text = request.Payload.AsText();
            context.Config.Set("statuses", Statuses().Where(status => status != text).ToList());
            Publish();
            return ValueTask.FromResult(HubPayload.Empty);
        });
        context.Bus.Handle("chatbox.chat.send", async (request, _) =>
        {
            if (!await _engine.SendMessageAsync(request.Payload.AsText(), DateTimeOffset.Now))
                throw new HubCallException(StatusCode.FailedPrecondition, Loc.T("Сообщение не отправлено: VRChat не запущен или отправка выключена.", "Message not sent: VRChat is not running or sending is off."));
            return HubPayload.Empty;
        });
        context.Bus.Handle("chatbox.sending.set", (request, _) =>
        {
            context.Config.Set("sending", request.Payload.AsText() == "true");
            Publish();
            return ValueTask.FromResult(HubPayload.Empty);
        });

        context.Config.Changed += OnConfigChanged;

        _subscription = await context.Bus.SubscribeAsync("activity.process.changed", OnProcessChanged, token);
        var names = HubPayload.Json(new { names = new[] { GameProcess } });
        await context.Bus.CallAsync("cap:user.activity", "process.watch", names, token: token);
        var running = (await context.Bus.CallAsync("cap:user.activity", "process.list", names, token: token)).AsJson<ProcessList>();
        SetGameRunning(running?.Names.Length > 0);

        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    private sealed record ProcessList(string[] Names);

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken token)
    {
        _subscription?.Dispose();
        if (_stop is not null) await _stop.CancelAsync();
        if (_loop is not null) await _loop;
        _panel.Dispose();
    }

    private void OnProcessChanged(HubEvent hubEvent)
    {
        if (hubEvent.Payload.AsJson<ProcessChange>() is { Name: GameProcess } change) SetGameRunning(change.Running);
    }

    private void SetGameRunning(bool running)
    {
        _engine.GameRunning = running;
        _context.Ui.Set("game", running ? Loc.T("запущен", "running") : Loc.T("не запущен", "not running"));
        if (running && _context.Config.Get("openPanel", true)) _panel.Ensure();
        if (!running) _panel.Stop();
        Publish();
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _engine.TickAsync();
                    _context.Ui.Set("preview", _engine.Preview);
                    if (_engine.Preview != _published) Publish();
                }
                catch (HubCallException ex)
                {
                    _context.Logger.Warning(ex.Message);
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(_context.Config.Get("intervalSeconds", 3d), 2)), token);
            }
        }
        catch (OperationCanceledException)
        {
            // Плагин останавливается.
        }
    }

    /// <summary>Через сколько секунд простоя показывать Away. <see langword="null"/>, если это выключено.</summary>
    private double? AwayAfter() => _context.Config.Get("showAway", true) ? _context.Config.Get("awaySeconds", 300d) : null;

    private ChatboxOptions ReadOptions()
    {
        var config = _context.Config;
        return new ChatboxOptions(
            config.Get("sending", true),
            config.Get("status", string.Empty)!,
            new ChatModules(
                config.Get("modStatus", true),
                config.Get("modWindow", true),
                config.Get("modComponent", false),
                config.Get("modTime", true),
                config.Get("modMedia", true)),
            TimeSpan.FromSeconds(config.Get("chatHoldSeconds", 8d)),
            new ChatFormat(
                config.Get("vrText", "In VR")!,
                config.Get("timeFormat", "HH:mm")!,
                config.Get("showProgress", true),
                config.Get("showCpu", true),
                config.Get("showGpu", true),
                config.Get("showRam", true),
                config.Get("showVram", true),
                config.Get("windowSource", "process")!,
                config.Get("template", new List<string>()),
                config.Get("awayText", "Away")!));
    }

    private void OnConfigChanged(string key)
    {
        if (key is "status" or "sending") Publish();
    }

    private List<string> Statuses() => _context.Config.Get("statuses", new List<string>())!;

    private void SetStatus(string text)
    {
        var status = text.Trim();
        _context.Config.Set("status", status);
        if (status.Length > 0)
        {
            var statuses = Statuses();
            statuses.Remove(status);
            statuses.Insert(0, status);
            if (statuses.Count > MaxStatuses) statuses.RemoveRange(MaxStatuses, statuses.Count - MaxStatuses);
            _context.Config.Set("statuses", statuses);
        }

        Publish();
    }

    private ChatboxState State() => new(_engine.GameRunning, _context.Config.Get("sending", true),
        _context.Config.Get("status", string.Empty)!, Statuses(), _engine.Preview);

    private void Publish()
    {
        _published = _engine.Preview;
        _ = _context.Bus.PublishAsync(ChangedTopic, HubPayload.Json(State()));
    }

    /// <inheritdoc/>
    public IEnumerable<UiPage> GetPages()
    {
        yield return Ui.Page("main", Loc.T("Чатбокс", "Chatbox"), "E8BD",
            Ui.KeyValue("VRChat", "game"),
            Ui.Card(Loc.T("Сейчас в чатбоксе", "In the chatbox now"), null, Ui.BoundText("preview", "mono")),
            Ui.Card(Loc.T("Личный статус", "Personal status"), Loc.T("Enter сохраняет.", "Enter saves."), Ui.TextBox("status", Loc.T("Что у тебя сейчас?", "What's up?")).Bind("value", "config:status")),
            Ui.Card(Loc.T("Сообщение", "Message"), Loc.T("Enter отправляет.", "Enter sends."), Ui.TextBox("chat", Loc.T("Написать в чат", "Write to chat"))),
            Ui.Button("panel", Loc.T("Открыть мини-окно", "Open the mini window"), accent: true));
    }

    /// <inheritdoc/>
    public void OnUiEvent(UiEvent uiEvent)
    {
        switch (uiEvent.NodeId)
        {
            case "panel":
                _panel.Ensure();
                break;
            case "chat" when uiEvent.Event == "submit":
                _ = _engine.SendMessageAsync(uiEvent.Value, DateTimeOffset.Now);
                break;
        }
    }

    /// <summary>Данные из других плагинов. Если кого-то нет, его строка просто не показывается.</summary>
    private sealed class BusChatData(IHubBus bus, Func<double?> awayAfter) : IChatData
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

        private sealed record Window(string Process, string Title);

        private sealed record VrState(bool Active);

        private sealed record IdleState(double IdleSeconds);

        public async Task<ChatInputs> ReadAsync(string status, ChatModules modules)
        {
            var window = modules.Window ? await TryAsync<Window>("cap:user.activity", "window.current") : null;
            var after = modules.Status ? awayAfter() : null;
            var vr = modules.Window || after is not null ? await TryAsync<VrState>("cap:user.activity", "vr.get") : null;
            var idle = after is not null ? await TryAsync<IdleState>("cap:user.activity", "idle.get") : null;
            var stats = modules.Component ? await TryAsync<StatsInfo>("cap:system.stats", "stats.get") : null;
            var media = modules.Media ? await TryAsync<MediaInfo>("cap:media.player", "player.current") : null;

            // В шлеме клавиатура и мышь простаивают всегда, поэтому в VR простой ни о чём не говорит.
            var away = vr?.Active != true && idle?.IdleSeconds >= after;
            return new ChatInputs(status, window?.Process, vr?.Active == true, stats, media, DateTimeOffset.Now, window?.Title, away);
        }

        private async Task<T?> TryAsync<T>(string target, string method) where T : class
        {
            try
            {
                return (await bus.CallAsync(target, method, timeout: Timeout)).AsJson<T>();
            }
            catch (HubCallException)
            {
                return null;
            }
        }
    }

    /// <summary>Отправка через возможность vrchat.osc.</summary>
    private sealed class OscChatSink(IHubBus bus) : IChatSink
    {
        public async Task SendAsync(string text, bool notify) =>
            await bus.CallAsync("cap:vrchat.osc", "chatbox.send", HubPayload.Json(new { text, notify }));
    }
}
