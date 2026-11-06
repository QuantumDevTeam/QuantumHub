using QuantumHub.Contracts.Ui;
using System.Text.Json.Nodes;

namespace QuantumHub.Plugin.Discord;

/// <summary>Настройки, которые движок читает при подключении.</summary>
/// <param name="ClientId">Client ID приложения. Пока пусто, к Discord никто не подключается.</param>
/// <param name="ClientSecret">Client Secret.</param>
/// <param name="RedirectUri">Redirect URI.</param>
/// <param name="AutoConnect">Подключаться автоматически.</param>
/// <param name="Scopes">Разрешения OAuth.</param>
public sealed record DiscordOptions(string ClientId, string ClientSecret, string RedirectUri, bool AutoConnect, IReadOnlyList<string> Scopes);

/// <summary>Участник голосового канала.</summary>
/// <param name="Id">Id пользователя.</param>
/// <param name="Name">Имя на сервере или имя пользователя.</param>
/// <param name="Muted">Отключён ли у него микрофон.</param>
/// <param name="Speaking">Говорит ли он прямо сейчас.</param>
public sealed record DiscordMember(string Id, string Name, bool Muted, bool Speaking);

/// <summary>Голосовой канал.</summary>
/// <param name="Id">Id канала.</param>
/// <param name="Name">Название.</param>
/// <param name="GuildId">Id сервера. Пусто в личных звонках.</param>
/// <param name="GuildName">Название сервера.</param>
/// <param name="Members">Участники.</param>
public sealed record DiscordChannel(string Id, string Name, string? GuildId, string? GuildName, IReadOnlyList<DiscordMember> Members);

/// <summary>Всё, что известно о Discord. Ответ discord.state и тело события discord.state.changed.</summary>
/// <param name="Connected">Есть ли связь с приложением. Этого хватает для Rich Presence.</param>
/// <param name="Authorized">Есть ли доступ к голосу и каналам.</param>
/// <param name="User">Имя текущего пользователя.</param>
/// <param name="UserId">Id текущего пользователя.</param>
/// <param name="Muted">Отключён ли микрофон.</param>
/// <param name="Deafened">Отключён ли звук.</param>
/// <param name="Channel">Голосовой канал, в котором находится пользователь.</param>
/// <param name="VoiceState">Состояние голосового соединения, например VOICE_CONNECTED.</param>
/// <param name="PingMs">Последний пинг голосового сервера.</param>
/// <param name="AveragePingMs">Средний пинг голосового сервера.</param>
/// <param name="Status">Что сейчас происходит, для показа человеку.</param>
public sealed record DiscordState(
    bool Connected = false,
    bool Authorized = false,
    string? User = null,
    string? UserId = null,
    bool Muted = false,
    bool Deafened = false,
    DiscordChannel? Channel = null,
    string? VoiceState = null,
    double? PingMs = null,
    double? AveragePingMs = null,
    string Status = "");

/// <summary>
/// Держит соединение с Discord и его состояние. Окно разрешения в Discord появляется только по прямой команде
/// <see cref="AuthorizeAsync"/>: неожиданный диалог посреди игры никому не нужен.
/// </summary>
/// <param name="connector">Подключение к Discord.</param>
/// <param name="tokens">Обмен токенов.</param>
/// <param name="store">Хранилище токенов.</param>
/// <param name="options">Текущие настройки.</param>
/// <param name="delay">Пауза. Подменяется в тестах.</param>
/// <param name="log">Куда писать смену статуса.</param>
public sealed class DiscordEngine(
    IDiscordConnector connector,
    ITokenService tokens,
    ITokenStore store,
    Func<DiscordOptions> options,
    Func<TimeSpan, CancellationToken, Task> delay,
    Action<string> log)
{
    /// <summary>Код Discord для недействительного токена.</summary>
    private const int InvalidToken = 4009;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    /// <summary>Пользователю нужно время, чтобы прочитать окно в Discord и нажать кнопку.</summary>
    private static readonly TimeSpan AuthorizeTimeout = TimeSpan.FromMinutes(3);

    private static readonly string[] GlobalEvents = ["VOICE_SETTINGS_UPDATE", "VOICE_CHANNEL_SELECT", "VOICE_CONNECTION_STATUS"];
    private static readonly string[] ChannelEvents = ["VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP"];

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly HashSet<string> _speaking = [];
    private volatile DiscordRpcClient? _rpc;
    private DiscordState _state = new();

    /// <summary>Текущее состояние.</summary>
    public DiscordState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    /// <summary>Состояние изменилось.</summary>
    public event Action? StateChanged;

    /// <summary>Любое событие Discord как есть. Для тех, кому мало готовых полей.</summary>
    public event Action<string, JsonNode?>? RawEvent;

    /// <summary>Настройки изменились: переподключиться с новыми.</summary>
    public async Task ReconnectAsync()
    {
        if (_rpc is { } rpc) await rpc.DisposeAsync();
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>Держит соединение, пока не отменён <paramref name="token"/>.</summary>
    /// <param name="token">Отмена.</param>
    public async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var settings = options();
                if (!settings.AutoConnect || string.IsNullOrWhiteSpace(settings.ClientId))
                {
                    Update(state => new DiscordState(Status: settings.AutoConnect ? Loc.T("Укажи Client ID в настройках", "Set the Client ID in the settings") : Loc.T("Автоподключение выключено", "Auto connect is off")));
                    await _wake.WaitAsync(token);
                    continue;
                }

                await SessionAsync(settings, token);
                await Task.WhenAny(delay(RetryDelay, token), _wake.WaitAsync(token));
                token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            // Плагин останавливается.
        }

        if (_rpc is { } rpc) await rpc.DisposeAsync();
    }

    private async Task SessionAsync(DiscordOptions settings, CancellationToken token)
    {
        Stream? stream;
        try
        {
            stream = await connector.ConnectAsync(token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream = null;
            log(ex.Message);
        }

        if (stream is null)
        {
            Update(_ => new DiscordState(Status: Loc.T("Discord не запущен", "Discord is not running")));
            return;
        }

        var rpc = new DiscordRpcClient(stream);
        rpc.EventReceived += OnEvent;
        try
        {
            var ready = await rpc.ConnectAsync(settings.ClientId, token);
            _rpc = rpc;
            var user = ready["user"];
            Update(_ => new DiscordState(true, User: user?["username"]?.GetValue<string>(), UserId: user?["id"]?.GetValue<string>(),
                Status: Loc.T("Подключено. Для голоса и канала нужна авторизация", "Connected. Voice and channel need authorization")));

            await TryRestoreAuthorizationAsync(rpc, settings, token);
            await rpc.Closed.WaitAsync(token);
            Update(_ => new DiscordState(Status: Loc.T("Соединение с Discord потеряно", "Lost connection to Discord")));
        }
        catch (Exception ex) when (ex is DiscordRpcException or TimeoutException)
        {
            Update(_ => new DiscordState(Status: ex.Message));
        }
        finally
        {
            _rpc = null;
            lock (_gate) _speaking.Clear();
            await rpc.DisposeAsync();
        }
    }

    private async Task TryRestoreAuthorizationAsync(DiscordRpcClient rpc, DiscordOptions settings, CancellationToken token)
    {
        if (store.Load() is not { } saved) return;

        try
        {
            await AuthenticateAsync(rpc, saved.Access, token);
        }
        catch (DiscordRpcException ex) when (ex.Code == InvalidToken && saved.Refresh is not null)
        {
            try
            {
                var fresh = await tokens.RefreshAsync(saved.Refresh, settings, token);
                store.Save(fresh);
                await AuthenticateAsync(rpc, fresh.Access, token);
            }
            catch (Exception refreshFailure) when (refreshFailure is DiscordRpcException or HttpRequestException)
            {
                store.Save(null);
                Update(state => state with { Status = Loc.T("Авторизация устарела, авторизуйся заново", "Authorization expired, authorize again") });
            }
        }
        catch (DiscordRpcException)
        {
            store.Save(null);
            Update(state => state with { Status = Loc.T("Авторизация устарела, авторизуйся заново", "Authorization expired, authorize again") });
        }
    }

    /// <summary>Показывает окно разрешения в Discord и получает доступ к голосу и каналам.</summary>
    /// <param name="token">Отмена.</param>
    /// <exception cref="DiscordRpcException">Пользователь отказал или настройки неверны.</exception>
    public async Task AuthorizeAsync(CancellationToken token = default)
    {
        var rpc = Connection();
        var settings = options();
        if (string.IsNullOrWhiteSpace(settings.ClientSecret))
            throw new DiscordRpcException(0, Loc.T("Укажи Client Secret в настройках.", "Set the Client Secret in the settings."));

        Update(state => state with { Status = Loc.T("Подтверди доступ в окне Discord", "Confirm access in the Discord window") });
        var response = await rpc.CommandAsync("AUTHORIZE",
            new JsonObject { ["client_id"] = settings.ClientId, ["scopes"] = new JsonArray(settings.Scopes.Select(scope => (JsonNode?)scope).ToArray()) },
            timeout: AuthorizeTimeout, token: token);

        var code = response?["code"]?.GetValue<string>() ?? throw new DiscordRpcException(0, Loc.T("Discord не вернул код.", "Discord returned no code."));
        var issued = await tokens.ExchangeAsync(code, settings, token);
        store.Save(issued);
        await AuthenticateAsync(rpc, issued.Access, token);
    }

    private async Task AuthenticateAsync(DiscordRpcClient rpc, string accessToken, CancellationToken token)
    {
        await rpc.CommandAsync("AUTHENTICATE", new JsonObject { ["access_token"] = accessToken }, token: token);
        foreach (var name in GlobalEvents) await rpc.CommandAsync("SUBSCRIBE", eventName: name, token: token);

        Update(state => state with { Authorized = true, Status = Loc.T("Подключено", "Connected") });
        ApplyVoice(await rpc.CommandAsync("GET_VOICE_SETTINGS", token: token));
        await RefreshChannelAsync(rpc, null);
    }

    private DiscordRpcClient Connection() => _rpc ?? throw new DiscordRpcException(0, Loc.T("Нет связи с Discord.", "No connection to Discord."));

    private DiscordRpcClient AuthorizedConnection() =>
        State.Authorized ? Connection() : throw new DiscordRpcException(4006, Loc.T("Нужна авторизация: нажми её на странице плагина.", "Authorization is needed: press it on the plugin page."));

    /// <summary>Выполняет любую команду Discord RPC.</summary>
    /// <param name="command">Имя команды.</param>
    /// <param name="arguments">Аргументы.</param>
    /// <param name="eventName">Имя события для SUBSCRIBE.</param>
    /// <returns>Поле data ответа.</returns>
    public Task<JsonNode?> CommandAsync(string command, JsonObject? arguments = null, string? eventName = null) =>
        Connection().CommandAsync(command, arguments, eventName);

    /// <summary>Включает или выключает микрофон.</summary>
    /// <param name="mute">Новое значение. <see langword="null"/> переключает.</param>
    public async Task SetMuteAsync(bool? mute)
    {
        var value = mute ?? !State.Muted;
        ApplyVoice(await AuthorizedConnection().CommandAsync("SET_VOICE_SETTINGS", new JsonObject { ["mute"] = value }));
    }

    /// <summary>Включает или выключает звук.</summary>
    /// <param name="deaf">Новое значение. <see langword="null"/> переключает.</param>
    public async Task SetDeafAsync(bool? deaf)
    {
        var value = deaf ?? !State.Deafened;
        ApplyVoice(await AuthorizedConnection().CommandAsync("SET_VOICE_SETTINGS", new JsonObject { ["deaf"] = value }));
    }

    /// <summary>Заходит в голосовой канал.</summary>
    /// <param name="channelId">Id канала. <see langword="null"/> выходит из текущего.</param>
    public async Task SelectChannelAsync(string? channelId) =>
        await AuthorizedConnection().CommandAsync("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = channelId, ["force"] = true });

    /// <summary>Устанавливает Rich Presence. Работает без авторизации.</summary>
    /// <param name="activity">Описание активности. <see langword="null"/> убирает её.</param>
    /// <param name="processId">Процесс, от имени которого показывается активность.</param>
    public Task SetActivityAsync(JsonObject? activity, int processId) =>
        Connection().CommandAsync("SET_ACTIVITY", new JsonObject { ["pid"] = processId, ["activity"] = activity });

    private void OnEvent(string name, JsonNode? data)
    {
        RawEvent?.Invoke(name, data);

        switch (name)
        {
            case "VOICE_SETTINGS_UPDATE":
                ApplyVoice(data);
                break;
            case "VOICE_CONNECTION_STATUS":
                Update(state => state with
                {
                    VoiceState = data?["state"]?.GetValue<string>(),
                    PingMs = Number(data?["last_ping"]),
                    AveragePingMs = Number(data?["average_ping"]),
                });
                break;
            case "VOICE_CHANNEL_SELECT":
                if (_rpc is { } rpc) _ = RefreshSafelyAsync(rpc, State.Channel?.Id);
                break;
            case "VOICE_STATE_CREATE" or "VOICE_STATE_UPDATE" or "VOICE_STATE_DELETE":
                if (_rpc is { } current) _ = RefreshSafelyAsync(current, State.Channel?.Id);
                break;
            case "SPEAKING_START" or "SPEAKING_STOP":
                ApplySpeaking(data?["user_id"]?.GetValue<string>(), name == "SPEAKING_START");
                break;
        }
    }

    private static double? Number(JsonNode? node) => node?.GetValue<double>();

    private void ApplyVoice(JsonNode? data)
    {
        if (data is null) return;
        Update(state => state with
        {
            Muted = data["mute"]?.GetValue<bool>() ?? state.Muted,
            Deafened = data["deaf"]?.GetValue<bool>() ?? state.Deafened,
        });
    }

    private void ApplySpeaking(string? userId, bool speaking)
    {
        if (userId is null) return;
        Update(state =>
        {
            if (speaking) _speaking.Add(userId);
            else _speaking.Remove(userId);

            return state.Channel is not { } channel
                ? state
                : state with { Channel = channel with { Members = channel.Members.Select(member => member with { Speaking = _speaking.Contains(member.Id) }).ToList() } };
        });
    }

    /// <summary>Канал меняется по событию на потоке чтения, а ответа надо ждать: поэтому отдельная задача.</summary>
    private async Task RefreshSafelyAsync(DiscordRpcClient rpc, string? previousChannel)
    {
        try
        {
            await RefreshChannelAsync(rpc, previousChannel);
        }
        catch (Exception ex) when (ex is DiscordRpcException or TimeoutException or IOException or ObjectDisposedException)
        {
            log($"Не удалось прочитать канал: {ex.Message}");
        }
    }

    private async Task RefreshChannelAsync(DiscordRpcClient rpc, string? previousChannel)
    {
        var data = await rpc.CommandAsync("GET_SELECTED_VOICE_CHANNEL");
        var id = data?["id"]?.GetValue<string>();

        if (id != previousChannel)
        {
            lock (_gate) _speaking.Clear();
            if (previousChannel is not null) await SwitchChannelEventsAsync(rpc, "UNSUBSCRIBE", previousChannel);
            if (id is not null) await SwitchChannelEventsAsync(rpc, "SUBSCRIBE", id);
        }

        if (id is null)
        {
            Update(state => state with { Channel = null, VoiceState = null, PingMs = null, AveragePingMs = null });
            return;
        }

        var guildId = data?["guild_id"]?.GetValue<string>();
        var guildName = id == State.Channel?.Id ? State.Channel.GuildName : await ReadGuildNameAsync(rpc, guildId);
        var members = new List<DiscordMember>();
        foreach (var voice in data?["voice_states"]?.AsArray() ?? [])
        {
            var userId = voice?["user"]?["id"]?.GetValue<string>();
            if (userId is null) continue;

            var name = voice?["nick"]?.GetValue<string>() ?? voice?["user"]?["username"]?.GetValue<string>() ?? userId;
            var state = voice?["voice_state"];
            var muted = state?["mute"]?.GetValue<bool>() == true || state?["self_mute"]?.GetValue<bool>() == true;
            lock (_gate) members.Add(new DiscordMember(userId, name, muted, _speaking.Contains(userId)));
        }

        var channel = new DiscordChannel(id, data?["name"]?.GetValue<string>() ?? id, guildId, guildName, members);
        Update(state => state with { Channel = channel });
    }

    private static async Task SwitchChannelEventsAsync(DiscordRpcClient rpc, string command, string channelId)
    {
        foreach (var name in ChannelEvents)
            await rpc.CommandAsync(command, new JsonObject { ["channel_id"] = channelId }, name);
    }

    private static async Task<string?> ReadGuildNameAsync(DiscordRpcClient rpc, string? guildId)
    {
        if (guildId is null) return null;
        try
        {
            return (await rpc.CommandAsync("GET_GUILD", new JsonObject { ["guild_id"] = guildId }))?["name"]?.GetValue<string>();
        }
        catch (DiscordRpcException)
        {
            return null; // Название сервера не стоит потери всего канала.
        }
    }

    private void Update(Func<DiscordState, DiscordState> change)
    {
        DiscordState next;
        string? status = null;
        lock (_gate)
        {
            next = change(_state);
            if (next == _state) return;
            if (next.Status != _state.Status) status = next.Status;
            _state = next;
        }

        if (status is { Length: > 0 }) log(status);
        StateChanged?.Invoke();
    }
}
