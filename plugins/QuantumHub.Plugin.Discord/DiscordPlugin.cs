using System.Text.Json;
using System.Text.Json.Nodes;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Plugin;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.Discord;

/// <summary>Запрос voice.mute и voice.deafen.</summary>
/// <param name="Value">Новое значение. Если его нет, состояние переключается.</param>
public sealed record VoiceRequest(bool? Value);

/// <summary>Запрос channel.join.</summary>
/// <param name="ChannelId">Id голосового канала.</param>
public sealed record JoinRequest(string ChannelId);

/// <summary>Запрос channels.list.</summary>
/// <param name="GuildId">Id сервера.</param>
public sealed record GuildRequest(string GuildId);

/// <summary>Событие Discord как есть. Тело топика discord.event.</summary>
/// <param name="Name">Имя события.</param>
/// <param name="Data">Данные.</param>
public sealed record RawDiscordEvent(string Name, JsonNode? Data);

/// <summary>
/// Клиент Discord как сервис для других плагинов. Окон у него нет, а всё умение лежит в API возможности
/// discord.client. Логика соединения находится в <see cref="DiscordEngine"/>.
/// </summary>
[HubPlugin]
public sealed class DiscordPlugin : IHubPlugin, IUiProvider
{
    /// <summary>Топик изменения состояния. Тело: <see cref="DiscordState"/> в JSON.</summary>
    public const string StateTopic = "discord.state.changed";

    /// <summary>Топик необработанных событий Discord.</summary>
    public const string RawTopic = "discord.event";

    private IPluginContext _context = null!;
    private DiscordEngine _engine = null!;
    private CancellationTokenSource? _stop;
    private Task? _loop;

    /// <inheritdoc/>
    public Task StartAsync(IPluginContext context, CancellationToken token)
    {
        _context = context;
        _engine = new DiscordEngine(new NamedPipeDiscordConnector(), new HttpTokenService(), new ProtectedTokenStore(context.Config),
            ReadOptions, Task.Delay, context.Logger.Info);

        _engine.StateChanged += OnStateChanged;
        _engine.RawEvent += (name, data) => _ = context.Bus.PublishAsync(RawTopic, HubPayload.Json(new RawDiscordEvent(name, data)));
        context.Config.Changed += OnConfigChanged;

        Read("discord.state", () => _engine.State);
        Read("channel.current", () => _engine.State.Channel);
        Read("voice.get", () => new { _engine.State.Muted, _engine.State.Deafened, _engine.State.VoiceState, _engine.State.PingMs, _engine.State.AveragePingMs });

        Run<VoiceRequest>("voice.mute", request => _engine.SetMuteAsync(request.Value));
        Run<VoiceRequest>("voice.deafen", request => _engine.SetDeafAsync(request.Value));
        Run<JoinRequest>("channel.join", request => _engine.SelectChannelAsync(request.ChannelId));
        Run<object>("channel.leave", _ => _engine.SelectChannelAsync(null));
        Run<object>("authorize", _ => _engine.AuthorizeAsync());
        Run<JsonObject>("presence.set", activity => _engine.SetActivityAsync(activity, Environment.ProcessId));
        Run<object>("presence.clear", _ => _engine.SetActivityAsync(null, Environment.ProcessId));

        Command("guilds.list", _ => _engine.CommandAsync("GET_GUILDS"));
        Command("channels.list", payload =>
        {
            var guild = payload.AsJson<GuildRequest>()?.GuildId ?? throw new ArgumentException("Нужен guildId.");
            return _engine.CommandAsync("GET_CHANNELS", new JsonObject { ["guild_id"] = guild });
        });
        Command("rpc.command", payload =>
        {
            var request = payload.AsJson<JsonObject>() ?? throw new ArgumentException("Пустой запрос.");
            var name = request["cmd"]?.GetValue<string>() ?? throw new ArgumentException("Нужно поле cmd.");
            return _engine.CommandAsync(name, request["args"]?.DeepClone().AsObject(), request["evt"]?.GetValue<string>());
        });

        PushState();
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => _engine.RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken token)
    {
        _context.Config.Changed -= OnConfigChanged;
        if (_stop is not null) await _stop.CancelAsync();
        if (_loop is not null) await _loop;
    }

    private void Read<T>(string method, Func<T> read) =>
        _context.Bus.Handle(method, (_, _) => ValueTask.FromResult(HubPayload.Json(read())));

    private void Run<T>(string method, Func<T, Task> action) => _context.Bus.Handle(method, async (request, _) =>
    {
        await Guard(async () =>
        {
            // Для методов без запроса тело можно не присылать.
            var argument = request.Payload.IsEmpty ? default : request.Payload.AsJson<T>();
            await action(argument!);
            return null;
        });
        return HubPayload.Empty;
    });

    private void Command(string method, Func<HubPayload, Task<JsonNode?>> action) => _context.Bus.Handle(method, async (request, _) =>
        HubPayload.Json(await Guard(() => action(request.Payload))));

    /// <summary>Ошибки Discord становятся ошибками вызова: тот, кто вызвал, поймёт, что делать.</summary>
    private static async Task<JsonNode?> Guard(Func<Task<JsonNode?>> action)
    {
        try
        {
            return await action();
        }
        catch (DiscordRpcException ex)
        {
            throw new HubCallException(StatusCode.FailedPrecondition, ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            throw new HubCallException(StatusCode.InvalidArgument, ex.Message);
        }
        catch (TimeoutException)
        {
            throw new HubCallException(StatusCode.Timeout, "Discord не ответил вовремя.");
        }
    }

    private DiscordOptions ReadOptions()
    {
        var config = _context.Config;
        return new DiscordOptions(
            config.Get("clientId", string.Empty)!.Trim(),
            config.Get("clientSecret", string.Empty)!.Trim(),
            config.Get("redirectUri", "http://127.0.0.1")!,
            config.Get("autoConnect", true),
            config.Get("scopes", new List<string>())!);
    }

    private void OnConfigChanged(string key)
    {
        if (key is "clientId" or "autoConnect") _ = _engine.ReconnectAsync();
    }

    private void OnStateChanged()
    {
        PushState();
        _ = _context.Bus.PublishAsync(StateTopic, HubPayload.Json(_engine.State));
    }

    private void PushState()
    {
        var state = _engine.State;
        var ui = _context.Ui;
        ui.Set("status", state.Status);
        ui.Set("user", state.User ?? "—");
        ui.Set("voice", $"{(state.Muted ? "микрофон выключен" : "микрофон включён")}, {(state.Deafened ? "звук выключен" : "звук включён")}");
        ui.Set("channel", state.Channel is { } channel ? (channel.GuildName is null ? channel.Name : $"{channel.GuildName} / {channel.Name}") : "не в канале");
        ui.Set("ping", state.PingMs is { } ping ? $"{ping:0} мс" : "—");
        ui.Set("members", string.Join('\n', state.Channel?.Members.Select(member =>
            $"{(member.Speaking ? "● " : "  ")}{member.Name}{(member.Muted ? " (без микрофона)" : string.Empty)}") ?? []));
    }

    /// <inheritdoc/>
    public IEnumerable<UiPage> GetPages()
    {
        yield return Ui.Page("main", "Discord", "E8F2",
            Ui.Card("Состояние", null, Ui.BoundText("status")),
            Ui.KeyValue("Пользователь", "user"),
            Ui.KeyValue("Голос", "voice"),
            Ui.KeyValue("Канал", "channel"),
            Ui.KeyValue("Пинг", "ping"),
            Ui.Row(
                Ui.Button("authorize", "Авторизовать", accent: true),
                Ui.Button("mute", "Микрофон"),
                Ui.Button("deafen", "Звук"),
                Ui.Button("reconnect", "Переподключить")),
            Ui.Text("Участники канала", "subtitle"),
            Ui.BoundText("members", "mono"));
    }

    /// <inheritdoc/>
    public void OnUiEvent(UiEvent uiEvent)
    {
        _ = uiEvent.NodeId switch
        {
            "authorize" => ReportAsync(_engine.AuthorizeAsync()),
            "mute" => ReportAsync(_engine.SetMuteAsync(null)),
            "deafen" => ReportAsync(_engine.SetDeafAsync(null)),
            "reconnect" => ReportAsync(_engine.ReconnectAsync()),
            _ => Task.CompletedTask,
        };
    }

    /// <summary>У кнопки нет вызывающего, поэтому ошибка показывается на странице.</summary>
    private async Task ReportAsync(Task action)
    {
        try
        {
            await action;
        }
        catch (Exception ex) when (ex is DiscordRpcException or TimeoutException or HttpRequestException)
        {
            _context.Ui.Set("status", ex.Message);
        }
    }
}
