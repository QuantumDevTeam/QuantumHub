using System.IO.Pipes;
using System.Text.Json.Nodes;
using QuantumHub.Plugin.Discord;
using Xunit;

namespace QuantumHub.Tests;

/// <summary>
/// Протокол Discord на подделке: настоящий именованный пайп, но со случайным именем и с поддельным
/// сервером на другом конце. Настоящий Discord здесь не затрагивается.
/// </summary>
public class DiscordTests
{
    private const string Channel = "111";

    private sealed class FakeDiscord : IDiscordConnector, IAsyncDisposable
    {
        private readonly List<NamedPipeServerStream> _servers = [];
        private NamedPipeServerStream? _current;

        public bool Running { get; set; } = true;
        public int Connections { get; private set; }
        public string? RejectWith { get; set; }
        public HashSet<string> ValidTokens { get; } = ["good"];
        public List<JsonObject> Commands { get; } = [];
        public bool VoiceMute { get; set; }
        public string? SelectedChannel { get; set; }
        public bool DenyAuthorize { get; set; }

        public async Task<Stream?> ConnectAsync(CancellationToken token)
        {
            if (!Running) return null;

            var name = $"qh-fake-discord-{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            var accept = server.WaitForConnectionAsync(token);
            await client.ConnectAsync(5000, token);
            await accept;

            Connections++;
            _servers.Add(server);
            _current = server;
            _ = Task.Run(() => ServeAsync(server), CancellationToken.None);
            return client;
        }

        private async Task ServeAsync(NamedPipeServerStream server)
        {
            try
            {
                while (true)
                {
                    var (opcode, payload) = await DiscordFrame.ReadAsync(server, CancellationToken.None);
                    if (opcode == DiscordOpcode.Handshake)
                    {
                        await HandshakeAsync(server);
                    }
                    else if (opcode == DiscordOpcode.Frame)
                    {
                        lock (Commands) Commands.Add(payload!.AsObject());
                        await RespondAsync(server, payload!.AsObject());
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                // Клиент ушёл.
            }
        }

        private async Task HandshakeAsync(Stream server)
        {
            if (RejectWith is not null)
            {
                await DiscordFrame.WriteAsync(server, DiscordOpcode.Close, new JsonObject { ["code"] = 4000, ["message"] = RejectWith }, CancellationToken.None);
                return;
            }

            await SendAsync(server, new JsonObject
            {
                ["cmd"] = "DISPATCH",
                ["evt"] = "READY",
                ["data"] = new JsonObject { ["user"] = new JsonObject { ["id"] = "42", ["username"] = "tester" } },
            });
        }

        /// <summary>Настоящий Discord пишет кадры по одному, а здесь пишут и ответы, и сам тест.</summary>
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        private async Task WriteFrameAsync(Stream server, DiscordOpcode opcode, JsonObject frame)
        {
            await _writeGate.WaitAsync();
            try
            {
                await DiscordFrame.WriteAsync(server, opcode, frame, CancellationToken.None);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private Task SendAsync(Stream server, JsonObject frame) => WriteFrameAsync(server, DiscordOpcode.Frame, frame);

        private async Task RespondAsync(Stream server, JsonObject request)
        {
            var command = request["cmd"]!.GetValue<string>();
            var args = request["args"] as JsonObject;
            var response = new JsonObject { ["cmd"] = command, ["nonce"] = request["nonce"]!.GetValue<string>() };

            void Fail(int code, string message)
            {
                response["evt"] = "ERROR";
                response["data"] = new JsonObject { ["code"] = code, ["message"] = message };
            }

            switch (command)
            {
                case "AUTHORIZE" when DenyAuthorize:
                    Fail(5000, "OAuth2 Error: access_denied");
                    break;
                case "AUTHORIZE":
                    response["data"] = new JsonObject { ["code"] = "the-code" };
                    break;
                case "AUTHENTICATE" when !ValidTokens.Contains(args!["access_token"]!.GetValue<string>()):
                    Fail(4009, "Invalid token");
                    break;
                case "SET_VOICE_SETTINGS":
                    if (args!["mute"] is { } mute) VoiceMute = mute.GetValue<bool>();
                    response["data"] = new JsonObject { ["mute"] = VoiceMute, ["deaf"] = false };
                    break;
                case "GET_VOICE_SETTINGS":
                    response["data"] = new JsonObject { ["mute"] = VoiceMute, ["deaf"] = false };
                    break;
                case "GET_SELECTED_VOICE_CHANNEL":
                    response["data"] = SelectedChannel is null ? null : ChannelData();
                    break;
                case "GET_GUILD":
                    response["data"] = new JsonObject { ["name"] = "QuantumDev" };
                    break;
                default:
                    response["data"] = new JsonObject();
                    break;
            }

            await SendAsync(server, response);
        }

        private JsonObject ChannelData() => new()
        {
            ["id"] = SelectedChannel,
            ["name"] = "General",
            ["guild_id"] = "7",
            ["voice_states"] = new JsonArray(new JsonObject
            {
                ["nick"] = "friend",
                ["user"] = new JsonObject { ["id"] = "9", ["username"] = "f" },
                ["voice_state"] = new JsonObject { ["self_mute"] = true },
            }),
        };

        public Task PushAsync(string name, JsonObject data) =>
            SendAsync(_current!, new JsonObject { ["cmd"] = "DISPATCH", ["evt"] = name, ["data"] = data });

        public Task PingAsync() => WriteFrameAsync(_current!, DiscordOpcode.Ping, new JsonObject { ["n"] = 1 });

        public void Drop() => _current!.Dispose();

        public bool Saw(string command, string? eventName = null)
        {
            lock (Commands) return Commands.Exists(item => item["cmd"]!.GetValue<string>() == command && (eventName is null || item["evt"]?.GetValue<string>() == eventName));
        }

        public ValueTask DisposeAsync()
        {
            foreach (var server in _servers) server.Dispose();
            return default;
        }
    }

    private sealed class FakeTokens : ITokenService
    {
        public string? ExchangedCode { get; private set; }
        public bool RefreshFails { get; set; }

        public Task<DiscordTokens> ExchangeAsync(string code, DiscordOptions options, CancellationToken token)
        {
            ExchangedCode = code;
            return Task.FromResult(new DiscordTokens("good", "refresh"));
        }

        public Task<DiscordTokens> RefreshAsync(string refreshToken, DiscordOptions options, CancellationToken token) =>
            RefreshFails ? throw new DiscordRpcException(400, "no") : Task.FromResult(new DiscordTokens("good", "refresh2"));
    }

    private sealed class MemoryStore : ITokenStore
    {
        public DiscordTokens? Tokens { get; set; }

        public DiscordTokens? Load() => Tokens;

        public void Save(DiscordTokens? tokens) => Tokens = tokens;
    }

    private readonly FakeDiscord _discord = new();
    private readonly FakeTokens _tokens = new();
    private readonly MemoryStore _store = new();
    private DiscordOptions _options = new("123", "secret", "http://127.0.0.1", true, ["rpc"]);

    private DiscordEngine Engine() => new(_discord, _tokens, _store, () => _options,
        (_, token) => Task.Delay(5, token), _ => { });

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400; i++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("Условие не выполнилось вовремя.");
    }

    private sealed class Run(DiscordEngine engine, CancellationTokenSource stop, Task loop) : IAsyncDisposable
    {
        public DiscordEngine Engine { get; } = engine;

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private Run Start()
    {
        var engine = Engine();
        var stop = new CancellationTokenSource();
        return new Run(engine, stop, Task.Run(() => engine.RunAsync(stop.Token)));
    }

    [Fact]
    public async Task Frames_use_the_discord_layout()
    {
        using var stream = new MemoryStream();
        await DiscordFrame.WriteAsync(stream, DiscordOpcode.Frame, new JsonObject { ["a"] = 1 }, CancellationToken.None);

        Assert.Equal([1, 0, 0, 0, 7, 0, 0, 0, (byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'1', (byte)'}'], stream.ToArray());

        stream.Position = 0;
        var (opcode, payload) = await DiscordFrame.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(DiscordOpcode.Frame, opcode);
        Assert.Equal(1, payload!["a"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_corrupt_length_is_rejected()
    {
        using var stream = new MemoryStream([1, 0, 0, 0, 255, 255, 255, 127]);

        await Assert.ThrowsAsync<InvalidDataException>(() => DiscordFrame.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Without_a_client_id_nothing_touches_discord()
    {
        _options = _options with { ClientId = "" };
        await using var run = Start();

        await Until(() => run.Engine.State.Status.Contains("Client ID"));

        Assert.Equal(0, _discord.Connections);
    }

    [Fact]
    public async Task Connects_and_learns_the_user_without_any_authorization_dialog()
    {
        await using var run = Start();

        await Until(() => run.Engine.State.Connected);

        Assert.Equal("tester", run.Engine.State.User);
        Assert.False(run.Engine.State.Authorized);
        Assert.False(_discord.Saw("AUTHORIZE"));
    }

    [Fact]
    public async Task Waits_for_discord_and_connects_when_it_appears()
    {
        _discord.Running = false;
        await using var run = Start();
        await Until(() => run.Engine.State.Status == "Discord не запущен");

        _discord.Running = true;

        await Until(() => run.Engine.State.Connected);
    }

    [Fact]
    public async Task Reconnects_after_discord_goes_away()
    {
        await using var run = Start();
        await Until(() => run.Engine.State.Connected);

        _discord.Drop();

        await Until(() => _discord.Connections == 2 && run.Engine.State.Connected);
    }

    [Fact]
    public async Task A_rejected_handshake_shows_the_reason()
    {
        _discord.RejectWith = "Invalid Client ID";
        await using var run = Start();

        await Until(() => run.Engine.State.Status == "Invalid Client ID");

        Assert.False(run.Engine.State.Connected);
    }

    [Fact]
    public async Task Answers_discord_pings()
    {
        await using var run = Start();
        await Until(() => run.Engine.State.Connected);

        await _discord.PingAsync();
        await run.Engine.SetActivityAsync(new JsonObject { ["state"] = "x" }, 1);

        Assert.True(_discord.Saw("SET_ACTIVITY"));
    }

    [Fact]
    public async Task Presence_works_without_authorization()
    {
        await using var run = Start();
        await Until(() => run.Engine.State.Connected);

        await run.Engine.SetActivityAsync(new JsonObject { ["details"] = "StellarEngine" }, 7);

        var command = _discord.Commands.Single(item => item["cmd"]!.GetValue<string>() == "SET_ACTIVITY");
        Assert.Equal(7, command["args"]!["pid"]!.GetValue<int>());
    }

    [Fact]
    public async Task Voice_needs_authorization_and_says_so()
    {
        await using var run = Start();
        await Until(() => run.Engine.State.Connected);

        var ex = await Assert.ThrowsAsync<DiscordRpcException>(() => run.Engine.SetMuteAsync(true));

        Assert.Contains("авторизация", ex.Message);
    }

    [Fact]
    public async Task Authorizing_exchanges_the_code_and_saves_the_tokens()
    {
        await using var run = Start();
        await Until(() => run.Engine.State.Connected);

        await run.Engine.AuthorizeAsync();

        Assert.Equal("the-code", _tokens.ExchangedCode);
        Assert.Equal("good", _store.Tokens!.Access);
        Assert.True(run.Engine.State.Authorized);
        Assert.True(_discord.Saw("SUBSCRIBE", "VOICE_SETTINGS_UPDATE"));
    }

    [Fact]
    public async Task Authorizing_needs_a_secret()
    {
        _options = _options with { ClientSecret = "" };
        await using var run = Start();
        await Until(() => run.Engine.State.Connected);

        await Assert.ThrowsAsync<DiscordRpcException>(() => run.Engine.AuthorizeAsync());

        Assert.False(_discord.Saw("AUTHORIZE"));
    }

    [Fact]
    public async Task A_denied_dialog_is_an_error_and_saves_nothing()
    {
        _discord.DenyAuthorize = true;
        await using var run = Start();
        await Until(() => run.Engine.State.Connected);

        await Assert.ThrowsAsync<DiscordRpcException>(() => run.Engine.AuthorizeAsync());

        Assert.Null(_store.Tokens);
    }

    [Fact]
    public async Task A_saved_token_restores_access_without_a_dialog()
    {
        _store.Tokens = new DiscordTokens("good", "refresh");
        await using var run = Start();

        await Until(() => run.Engine.State.Authorized);

        Assert.False(_discord.Saw("AUTHORIZE"));
    }

    [Fact]
    public async Task An_expired_token_is_refreshed()
    {
        _store.Tokens = new DiscordTokens("expired", "refresh");
        await using var run = Start();

        await Until(() => run.Engine.State.Authorized);

        Assert.Equal("refresh2", _store.Tokens!.Refresh);
    }

    [Fact]
    public async Task A_dead_refresh_token_asks_for_authorization_again()
    {
        _store.Tokens = new DiscordTokens("expired", "refresh");
        _tokens.RefreshFails = true;
        await using var run = Start();

        await Until(() => run.Engine.State.Status.Contains("авторизуйся"));

        Assert.Null(_store.Tokens);
        Assert.True(run.Engine.State.Connected);
    }

    private async Task<Run> AuthorizedAsync()
    {
        _store.Tokens = new DiscordTokens("good", null);
        var run = Start();
        await Until(() => run.Engine.State.Authorized);
        return run;
    }

    [Fact]
    public async Task Mute_sets_toggles_and_follows_changes_made_in_discord()
    {
        await using var run = await AuthorizedAsync();

        await run.Engine.SetMuteAsync(true);
        Assert.True(run.Engine.State.Muted);

        await run.Engine.SetMuteAsync(null);
        Assert.False(run.Engine.State.Muted);

        await _discord.PushAsync("VOICE_SETTINGS_UPDATE", new JsonObject { ["mute"] = true, ["deaf"] = true });
        await Until(() => run.Engine.State is { Muted: true, Deafened: true });
    }

    [Fact]
    public async Task Knows_the_channel_with_its_members_and_who_is_speaking()
    {
        _discord.SelectedChannel = Channel;
        await using var run = await AuthorizedAsync();

        await Until(() => run.Engine.State.Channel is not null);
        var channel = run.Engine.State.Channel!;
        Assert.Equal("General", channel.Name);
        Assert.Equal("QuantumDev", channel.GuildName);
        Assert.Equal(new DiscordMember("9", "friend", true, false), channel.Members.Single());
        Assert.True(_discord.Saw("SUBSCRIBE", "SPEAKING_START"));

        await _discord.PushAsync("SPEAKING_START", new JsonObject { ["user_id"] = "9" });
        await Until(() => run.Engine.State.Channel!.Members[0].Speaking);

        await _discord.PushAsync("SPEAKING_STOP", new JsonObject { ["user_id"] = "9" });
        await Until(() => !run.Engine.State.Channel!.Members[0].Speaking);
    }

    [Fact]
    public async Task Follows_joining_and_leaving_a_channel()
    {
        await using var run = await AuthorizedAsync();
        Assert.Null(run.Engine.State.Channel);

        _discord.SelectedChannel = Channel;
        await _discord.PushAsync("VOICE_CHANNEL_SELECT", new JsonObject { ["channel_id"] = Channel });
        await Until(() => run.Engine.State.Channel?.Id == Channel);

        _discord.SelectedChannel = null;
        await _discord.PushAsync("VOICE_CHANNEL_SELECT", new JsonObject { ["channel_id"] = null });
        await Until(() => run.Engine.State.Channel is null);
        await Until(() => _discord.Saw("UNSUBSCRIBE", "SPEAKING_START"));
    }

    [Fact]
    public async Task Reports_the_voice_ping()
    {
        await using var run = await AuthorizedAsync();

        await _discord.PushAsync("VOICE_CONNECTION_STATUS", new JsonObject { ["state"] = "VOICE_CONNECTED", ["last_ping"] = 48, ["average_ping"] = 51.5 });

        await Until(() => run.Engine.State.PingMs == 48);
        Assert.Equal(51.5, run.Engine.State.AveragePingMs);
        Assert.Equal("VOICE_CONNECTED", run.Engine.State.VoiceState);
    }

    [Fact]
    public async Task Hands_over_every_event_as_it_is()
    {
        await using var run = await AuthorizedAsync();
        var seen = new TaskCompletionSource<string>();
        run.Engine.RawEvent += (name, _) => seen.TrySetResult(name);

        await _discord.PushAsync("NOTIFICATION_CREATE", new JsonObject());

        Assert.Equal("NOTIFICATION_CREATE", await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Any_rpc_command_can_be_sent()
    {
        await using var run = await AuthorizedAsync();

        await run.Engine.CommandAsync("GET_GUILDS");

        Assert.True(_discord.Saw("GET_GUILDS"));
    }

    [Fact]
    public async Task Commands_fail_cleanly_without_a_connection()
    {
        _discord.Running = false;
        await using var run = Start();

        await Assert.ThrowsAsync<DiscordRpcException>(() => run.Engine.CommandAsync("GET_GUILDS"));
    }
}
