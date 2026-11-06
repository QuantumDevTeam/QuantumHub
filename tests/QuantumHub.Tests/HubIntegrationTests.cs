using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using QuantumHub.Client;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Proto;
using Xunit;
using static QuantumHub.Tests.TestHub;

namespace QuantumHub.Tests;

/// <summary>Сценарии, ради которых существуют плагины-примеры: от начала до конца через настоящий IPC.</summary>
public class HubIntegrationTests
{
    private static readonly Dependency Backend = new() { Id = Uuid, Version = ">=1.0.0", When = "init" };
    private static readonly Dependency SpecsLazy = new() { Capability = "system.specs", Optional = true, When = "lazy" };

    private static async Task<PluginInfo> InfoAsync(IHubBus bus, string id)
    {
        var list = await bus.CallAsync<PluginList>("hub", "hub.plugins.list", new PluginRef());
        return list.Plugins.Single(plugin => plugin.Manifest.Id == id);
    }

    private static async Task<UiResponse> UiAsync(IHubBus bus, string id) =>
        await bus.CallAsync<UiResponse>("hub", "hub.ui.get", new UiRequest { PluginId = id });

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }

        Assert.Fail("The condition did not become true in time.");
    }

    [Fact]
    public async Task The_value_from_an_external_process_shows_up_in_the_backend_ui()
    {
        await using var hub = await StartAsync(0, Uuid);
        await using var client = await hub.ConnectAsync(dependencies: [Backend]);

        await client.CallAsync(Uuid, "value.submit", HubPayload.Json(new { kind = "uuid", value = "abc-123" }));

        var ui = await UiAsync(client, Uuid);
        Assert.Equal("abc-123", ui.State["value"]);
        Assert.Equal("test.client", ui.State["source"]);
        Assert.Contains(ui.Pages, page => page.Id == "main");
    }

    [Fact]
    public async Task A_missing_required_dependency_rejects_the_connection_with_details()
    {
        await using var hub = await StartAsync(0);

        var ex = await Assert.ThrowsAsync<HubRejectedException>(() => hub.ConnectAsync(dependencies: [Backend]));

        Assert.Equal(StatusCode.FailedPrecondition, ex.Status);
        Assert.Equal(DependencyState.Missing, ex.Dependencies[0].State);
    }

    [Fact]
    public async Task A_too_old_required_dependency_rejects_the_connection()
    {
        await using var hub = await StartAsync(0, Uuid);

        var ex = await Assert.ThrowsAsync<HubRejectedException>(() =>
            hub.ConnectAsync(dependencies: [new Dependency { Id = Uuid, Version = ">=2.0.0" }]));

        Assert.Equal(DependencyState.VersionMismatch, ex.Dependencies[0].State);
    }

    [Fact]
    public async Task A_lazy_optional_dependency_leaves_the_provider_unloaded_until_requested()
    {
        await using var hub = await StartAsync(0, Uuid, Specs);
        await using var client = await hub.ConnectAsync(dependencies: [Backend, SpecsLazy]);

        Assert.Equal(DependencyState.Resolved, client.Dependencies[0].State);
        Assert.Equal(DependencyState.Deferred, client.Dependencies[1].State);
        Assert.Equal(PluginState.Stopped, (await InfoAsync(client, Specs)).State);

        var resolved = await client.ResolveAsync(SpecsLazy);

        Assert.Equal(DependencyState.Resolved, resolved.State);
        Assert.Equal(Specs, resolved.ProviderId);
        Assert.Equal(PluginState.Running, (await InfoAsync(client, Specs)).State);

        var specs = await client.CallAsync("cap:system.specs", "specs.get");
        var document = JsonDocument.Parse(specs.Data);
        Assert.True(document.RootElement.GetProperty("threads").GetInt32() > 0);
    }

    [Fact]
    public async Task A_missing_optional_dependency_still_connects_and_reports_missing()
    {
        await using var hub = await StartAsync(0, Uuid);
        await using var client = await hub.ConnectAsync(dependencies: [Backend, SpecsLazy]);

        var resolved = await client.ResolveAsync(SpecsLazy);

        Assert.Equal(DependencyState.Missing, resolved.State);
    }

    [Fact]
    public async Task A_call_starts_an_on_demand_plugin_by_capability()
    {
        await using var hub = await StartAsync(0, Specs);
        await using var client = await hub.ConnectAsync();

        var specs = await client.CallAsync("cap:system.specs", "specs.get");

        Assert.False(specs.IsEmpty);
    }

    [Fact]
    public async Task Disabling_a_dependency_is_seen_by_clients_and_undone_by_enabling()
    {
        await using var hub = await StartAsync(0, Uuid, Specs);
        await using var client = await hub.ConnectAsync();
        var seen = new List<PluginState>();
        await client.SubscribeAsync("hub.plugin.state", e =>
        {
            var info = e.Payload.AsProto<PluginInfo>();
            if (info.Manifest.Id == Specs) lock (seen) seen.Add(info.State);
        });

        Assert.Equal(DependencyState.Resolved, (await client.ResolveAsync(SpecsLazy)).State);

        await client.CallAsync("hub", "hub.plugins.enable", HubPayload.Proto(new SetEnabledRequest { Id = Specs, Enabled = false }));
        Assert.Equal(DependencyState.Failed, (await client.ResolveAsync(SpecsLazy)).State);
        await EventuallyAsync(() => { lock (seen) return Task.FromResult(seen.Contains(PluginState.Disabled)); });

        await client.CallAsync("hub", "hub.plugins.enable", HubPayload.Proto(new SetEnabledRequest { Id = Specs, Enabled = true }));
        Assert.Equal(DependencyState.Resolved, (await client.ResolveAsync(SpecsLazy)).State);
    }

    [Fact]
    public async Task Stopping_a_plugin_blocks_its_dependents_and_restores_them_afterwards()
    {
        await using var hub = await StartAsync(0, Uuid);
        var dependent = Path.Combine(hub.PluginsDir, "needs-uuid");
        CopyPlugin(Uuid, dependent);
        File.WriteAllText(Path.Combine(dependent, "plugin.jsonc"), $$"""
            { "id": "needs.uuid", "version": "1.0.0",
              "service": { "assembly": "QuantumHub.Plugin.UuidBackend.dll" },
              "dependencies": [ { "id": "{{Uuid}}" } ] }
            """);
        await hub.ShutdownAsync();
        await hub.BootAsync();

        await using var client = await hub.ConnectAsync();
        Assert.Equal(PluginState.Running, (await InfoAsync(client, "needs.uuid")).State);
        Assert.Contains("needs.uuid", (await InfoAsync(client, Uuid)).Dependents);

        await client.CallAsync("hub", "hub.plugins.enable", HubPayload.Proto(new SetEnabledRequest { Id = Uuid, Enabled = false }));
        Assert.Equal(PluginState.Blocked, (await InfoAsync(client, "needs.uuid")).State);

        await client.CallAsync("hub", "hub.plugins.enable", HubPayload.Proto(new SetEnabledRequest { Id = Uuid, Enabled = true }));
        Assert.Equal(PluginState.Running, (await InfoAsync(client, "needs.uuid")).State);
    }

    [Fact]
    public async Task A_dependency_cycle_blocks_the_plugins_and_does_not_hang_the_hub()
    {
        await using var hub = await StartAsync(0);
        foreach (var (id, other) in new[] { ("cycle.a", "cycle.b"), ("cycle.b", "cycle.a") })
        {
            var dir = Directory.CreateDirectory(Path.Combine(hub.PluginsDir, id)).FullName;
            File.WriteAllText(Path.Combine(dir, "plugin.jsonc"), $$"""
                { "id": "{{id}}", "version": "1.0.0", "service": { "assembly": "x.dll" },
                  "dependencies": [ { "id": "{{other}}" } ] }
                """);
        }

        await hub.ShutdownAsync();
        await hub.BootAsync();

        await using var client = await hub.ConnectAsync();
        var info = await InfoAsync(client, "cycle.a");
        Assert.Equal(PluginState.Blocked, info.State);
        Assert.Contains("cycle", info.Error);
    }

    [Fact]
    public async Task A_plugin_that_fails_to_load_is_faulted_and_the_rest_keep_working()
    {
        await using var hub = await StartAsync(0, Uuid);
        var dir = Directory.CreateDirectory(Path.Combine(hub.PluginsDir, "broken")).FullName;
        File.WriteAllText(Path.Combine(dir, "plugin.jsonc"),
            """{ "id": "broken", "version": "1.0.0", "service": { "assembly": "missing.dll" } }""");
        var invalid = Directory.CreateDirectory(Path.Combine(hub.PluginsDir, "invalid")).FullName;
        File.WriteAllText(Path.Combine(invalid, "plugin.jsonc"), "{ this is not json");
        await hub.ShutdownAsync();
        await hub.BootAsync();

        await using var client = await hub.ConnectAsync(dependencies: [Backend]);

        Assert.Equal(PluginState.Faulted, (await InfoAsync(client, "broken")).State);
        Assert.Equal(PluginState.Faulted, (await InfoAsync(client, "invalid")).State);
        Assert.Equal(PluginState.Running, (await InfoAsync(client, Uuid)).State);
    }

    [Fact]
    public async Task A_method_that_throws_is_an_error_for_the_caller_only()
    {
        await using var hub = await StartAsync(0, Uuid);
        await using var client = await hub.ConnectAsync();

        var ex = await Assert.ThrowsAsync<HubCallException>(async () =>
            await client.CallAsync(Uuid, "value.submit", HubPayload.Text("not json")));

        Assert.Equal(StatusCode.Internal, ex.Status);
        Assert.Equal(PluginState.Running, (await InfoAsync(client, Uuid)).State);
    }

    [Fact]
    public async Task Unknown_targets_and_methods_are_reported()
    {
        await using var hub = await StartAsync(0, Uuid);
        await using var client = await hub.ConnectAsync();

        var target = await Assert.ThrowsAsync<HubCallException>(async () => await client.CallAsync("nobody", "x"));
        var method = await Assert.ThrowsAsync<HubCallException>(async () => await client.CallAsync(Uuid, "nope"));

        Assert.Equal(StatusCode.NotFound, target.Status);
        Assert.Equal(StatusCode.NotFound, method.Status);
    }

    [Fact]
    public async Task Stored_values_survive_a_restart_as_a_jsonc_file_in_the_user_folder()
    {
        await using var hub = await StartAsync(0, Uuid);
        await using (var client = await hub.ConnectAsync())
            await client.CallAsync(Uuid, "value.submit", HubPayload.Json(new { kind = "text", value = "persist me" }));

        await hub.ShutdownAsync();
        Assert.Contains("persist me", File.ReadAllText(Path.Combine(hub.DataDir, "config", $"{Uuid}.jsonc")));

        await hub.BootAsync();
        await using var again = await hub.ConnectAsync();
        Assert.Equal("persist me", (await UiAsync(again, Uuid)).State["value"]);
    }

    [Fact]
    public async Task A_setting_changed_from_the_gui_reaches_the_plugin_without_plugin_ui_code()
    {
        await using var hub = await StartAsync(0, Uuid);
        await using var gui = await hub.ConnectAsync("gui");
        await gui.CallAsync(Uuid, "value.submit", HubPayload.Json(new { kind = "text", value = "hello" }));

        var patches = new List<UiStatePatch>();
        await gui.SubscribeAsync("hub.ui.state", e => { lock (patches) patches.Add(e.Payload.AsProto<UiStatePatch>()); });

        var settings = (await UiAsync(gui, Uuid)).Pages.Single(page => page.Id == "settings");
        Assert.Contains(settings.Root.Children, card => card.Children[0].Bindings["value"] == "config:uppercase");

        await gui.CallAsync("hub", "hub.ui.event", HubPayload.Proto(new UiEvent
        {
            PluginId = Uuid, Event = "changed", Bind = "config:uppercase", Value = "true",
        }));

        await EventuallyAsync(() =>
        {
            lock (patches) return Task.FromResult(patches.Any(p => p.State.TryGetValue("value", out var v) && v == "HELLO"));
        });
        Assert.Equal("true", (await UiAsync(gui, Uuid)).State["config:uppercase"]);
    }

    [Fact]
    public async Task Installs_a_qhp_archive_at_run_time_and_uninstalls_it_completely()
    {
        await using var hub = await StartAsync(0);
        await using var client = await hub.ConnectAsync();
        var archive = Path.Combine(hub.Root, "uuid.qhp");
        ZipFile.CreateFromDirectory(BuildOutput(Uuid), archive);

        var installed = await client.CallAsync<PluginInfo>("hub", "hub.plugins.install", new InstallRequest { Path = archive });

        Assert.Equal(PluginState.Running, installed.State);
        await client.CallAsync(Uuid, "value.submit", HubPayload.Json(new { kind = "text", value = "x" }));

        await client.CallAsync("hub", "hub.plugins.uninstall", HubPayload.Proto(new PluginRef { Id = Uuid }));

        var list = await client.CallAsync<PluginList>("hub", "hub.plugins.list", new PluginRef());
        Assert.Empty(list.Plugins);
        Assert.False(Directory.Exists(Path.Combine(hub.PluginsDir, Uuid)), "The plugin's files should be gone: its code was unloaded.");
    }

    [Fact]
    public async Task Installs_a_mono_dll_and_reads_its_embedded_manifest()
    {
        await using var hub = await StartAsync(0);
        await using var client = await hub.ConnectAsync();
        var dll = Path.Combine(BuildOutput(Specs), "QuantumHub.Plugin.SystemSpecs.dll");

        var installed = await client.CallAsync<PluginInfo>("hub", "hub.plugins.install", new InstallRequest { Path = dll });

        Assert.Equal(Specs, installed.Manifest.Id);
        Assert.Equal("QuantumHub.Plugin.SystemSpecs.dll", installed.Manifest.Service.Assembly);
        Assert.False((await client.CallAsync("cap:system.specs", "specs.get")).IsEmpty);
    }

    [Fact]
    public async Task A_mono_dll_dropped_into_the_plugins_folder_is_picked_up()
    {
        await using var hub = await StartAsync(0);
        await hub.ShutdownAsync();
        File.Copy(Path.Combine(BuildOutput(Specs), "QuantumHub.Plugin.SystemSpecs.dll"),
            Path.Combine(hub.PluginsDir, "QuantumHub.Plugin.SystemSpecs.dll"));

        await hub.BootAsync();
        await using var client = await hub.ConnectAsync();

        Assert.Equal(PluginState.Stopped, (await InfoAsync(client, Specs)).State);
    }

    [Fact]
    public async Task Serves_an_icon_embedded_in_the_plugin_assembly()
    {
        await using var hub = await StartAsync(0, Window);
        await using var client = await hub.ConnectAsync();

        var asset = await client.CallAsync<AssetResponse>("hub", "hub.plugins.asset", new AssetRequest { PluginId = Window });

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], asset.Data.Take(4));
    }

    [Fact]
    public async Task Reports_the_native_ui_assembly_of_a_split_plugin()
    {
        await using var hub = await StartAsync(0, Window);
        await using var client = await hub.ConnectAsync();

        var ui = await UiAsync(client, Window);

        Assert.EndsWith("QuantumHub.Plugin.Activity.Ui.dll", ui.NativeAssembly);
        Assert.NotEmpty(ui.State["process"]);
    }

    [Fact]
    public async Task Refuses_an_asset_path_outside_the_plugin_folder()
    {
        await using var hub = await StartAsync(0, Uuid);
        await using var client = await hub.ConnectAsync();

        var ex = await Assert.ThrowsAsync<HubCallException>(async () => await client.CallAsync("hub", "hub.plugins.asset",
            HubPayload.Proto(new AssetRequest { PluginId = Uuid, Name = @"..\..\..\data\endpoint.json" })));

        Assert.Equal(StatusCode.NotFound, ex.Status);
    }

    [Theory]
    [InlineData("shm", "shm")]
    [InlineData("stream", "stream")]
    public async Task Works_over_each_local_transport(string requested, string expected)
    {
        await using var hub = await StartAsync(0, Uuid);
        await using var client = await hub.ConnectAsync(transports: [requested]);

        Assert.Equal(expected, client.Transport);
        await client.CallAsync(Uuid, "value.submit", HubPayload.Json(new { kind = "text", value = "x" }));
    }

    [Fact]
    public async Task Works_over_tcp_with_the_token_and_not_without()
    {
        await using var hub = await StartAsync(0, Uuid);
        var options = (string? token) => new HubClientOptions
        {
            ClientId = "tcp.client", TcpPort = hub.Hub.Server.TcpPort, TcpToken = token,
        };

        await using var client = await HubClient.ConnectAsync(options(hub.Hub.Server.TcpToken));
        await client.CallAsync(Uuid, "value.submit", HubPayload.Json(new { kind = "text", value = "tcp" }));

        var ex = await Assert.ThrowsAsync<HubRejectedException>(() => HubClient.ConnectAsync(options("wrong")));
        Assert.Equal(StatusCode.Unauthenticated, ex.Status);
    }

    [Fact]
    public async Task Tcp_is_off_unless_asked_for()
    {
        await using var hub = await StartAsync(tcpPort: null);

        Assert.Null(hub.Hub.Server.TcpPort);
        Assert.DoesNotContain("tcp", hub.Hub.Server.Transports);
    }

    [Fact]
    public async Task Large_payloads_cross_the_ring_in_both_directions()
    {
        await using var hub = await StartAsync(0);
        await using var client = await hub.ConnectAsync();
        var payload = new byte[6 * 1024 * 1024]; // В шесть раз больше кольца.
        Random.Shared.NextBytes(payload);

        var echoed = await client.CallAsync("hub", "hub.ping", HubPayload.Bytes(payload));

        Assert.True(echoed.Data.Span.SequenceEqual(payload));
    }

    [Fact]
    public async Task Parallel_calls_each_get_their_own_answer()
    {
        await using var hub = await StartAsync(0);
        await using var client = await hub.ConnectAsync();

        await Task.WhenAll(Enumerable.Range(0, 2000).Select(async i =>
        {
            var text = $"message {i}";
            var echoed = await client.CallAsync("hub", "hub.ping", HubPayload.Text(text));
            Assert.Equal(text, echoed.AsText());
        }));
    }

    [Fact]
    public async Task The_hub_can_call_a_client_and_clients_can_reach_each_other()
    {
        await using var hub = await StartAsync(0);
        await using var provider = await hub.ConnectAsync("acme.provider", provides: [new Capability { Name = "acme.thing", Version = "1.0.0" }]);
        provider.Handle("greet", (request, _) => ValueTask.FromResult(HubPayload.Text($"hi {request.Source}")));
        await using var caller = await hub.ConnectAsync("acme.caller");

        var byId = await caller.CallAsync("acme.provider", "greet");
        var byCapability = await caller.CallAsync("cap:acme.thing", "greet");

        Assert.Equal("hi acme.caller", byId.AsText());
        Assert.Equal("hi acme.caller", byCapability.AsText());
    }

    [Fact]
    public async Task A_client_cannot_pretend_to_be_someone_else()
    {
        await using var hub = await StartAsync(0);
        await using var receiver = await hub.ConnectAsync("receiver");
        var seen = new TaskCompletionSource<string>();
        await receiver.SubscribeAsync("spoof.*", e => seen.TrySetResult(e.Source));
        await using var sender = await hub.ConnectAsync("honest.client");

        await sender.PublishAsync("spoof.test");

        Assert.Equal("honest.client", await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_call_to_a_client_that_disconnects_fails_instead_of_hanging()
    {
        await using var hub = await StartAsync(0);
        var provider = await hub.ConnectAsync("slow.provider");
        var entered = new TaskCompletionSource();
        provider.Handle("hang", async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return default;
        });
        await using var caller = await hub.ConnectAsync("caller");

        var call = caller.CallAsync("slow.provider", "hang").AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await provider.DisposeAsync();

        var ex = await Assert.ThrowsAsync<HubCallException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(StatusCode.Unavailable, ex.Status);
    }

    [Fact]
    public async Task A_client_with_auto_reconnect_comes_back_and_keeps_its_subscriptions()
    {
        await using var hub = await StartAsync(0, Uuid);
        await using var client = await HubClient.ConnectAsync(new HubClientOptions
        {
            ClientId = "survivor", Endpoint = hub.Endpoint, AutoReconnect = true,
        });
        var updates = new TaskCompletionSource<string>();
        await client.SubscribeAsync("uuid.updated", e => updates.TrySetResult(e.Payload.AsText()));
        var reconnected = new TaskCompletionSource();
        client.Reconnected += () => reconnected.TrySetResult();

        await hub.ShutdownAsync();
        await hub.BootAsync();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await client.CallAsync(Uuid, "value.submit", HubPayload.Json(new { kind = "text", value = "after" }));
        Assert.Equal("after", await updates.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task The_vr_plugins_load_link_up_expose_their_api_and_unload_without_touching_sound_or_headset()
    {
        await using var hub = await StartAsync(0);
        await hub.ShutdownAsync();

        // Настоящие плагины в настоящем хабе. Безопасность держится на этих настройках: без автопереключения
        // VR Switcher только читает процессы и список устройств, а аддон без команды не запускает adb.
        // devices.set здесь не вызывается.
        Directory.CreateDirectory(Path.Combine(hub.DataDir, "config"));
        File.WriteAllText(Path.Combine(hub.DataDir, "config", $"{VrSwitcher}.jsonc"), """{ "autoSwitch": false }""");
        File.WriteAllText(Path.Combine(hub.DataDir, "config", $"{AntiSleep}.jsonc"), """{ "autoWithVr": false }""");
        foreach (var plugin in new[] { Window, Audio, VrSwitcher, AntiSleep }) CopyPlugin(plugin, Path.Combine(hub.PluginsDir, plugin));
        await hub.BootAsync();

        await using var client = await hub.ConnectAsync();
        foreach (var plugin in new[] { Audio, VrSwitcher, AntiSleep })
        {
            var info = await InfoAsync(client, plugin);
            Assert.True(info.State == PluginState.Running, $"{plugin}: {info.Error}");
        }

        Assert.Contains(VrSwitcher, (await InfoAsync(client, Audio)).Dependents);
        Assert.Contains(AntiSleep, (await InfoAsync(client, VrSwitcher)).Dependents);

        var session = JsonDocument.Parse((await client.CallAsync("cap:vr.session", "session.get")).Data).RootElement;
        Assert.Equal("Pico", session.GetProperty("preset").GetString());
        Assert.False(session.GetProperty("onHeadset").GetBoolean());

        var current = JsonDocument.Parse((await client.CallAsync("cap:audio.devices", "devices.current")).Data).RootElement;
        var list = JsonDocument.Parse((await client.CallAsync("cap:audio.devices", "devices.list")).Data).RootElement;
        Assert.False(string.IsNullOrEmpty(current.GetProperty("playback").GetProperty("name").GetString()));
        Assert.True(list.GetProperty("recording").GetArrayLength() > 0);

        Assert.False(string.IsNullOrEmpty((await UiAsync(client, VrSwitcher)).State["playback"]));

        var ex = await Assert.ThrowsAsync<HubCallException>(async () =>
            await client.CallAsync("cap:audio.devices", "devices.set", HubPayload.Json(new { id = "" })));
        Assert.Equal(StatusCode.InvalidArgument, ex.Status);

        foreach (var plugin in new[] { AntiSleep, VrSwitcher, Audio })
            await client.CallAsync("hub", "hub.plugins.uninstall", HubPayload.Proto(new PluginRef { Id = plugin }));
        Assert.False(Directory.Exists(Path.Combine(hub.PluginsDir, Audio)), "NAudio and COM must not pin the plugin in memory.");
    }

    [Fact]
    public async Task The_whole_plugin_chain_feeds_the_chatbox_without_touching_the_real_game()
    {
        await using var hub = await StartAsync(0);
        await hub.ShutdownAsync();

        // Настоящие плагины. Безопасность: приглушение и автопереключение выключены, а OSC идёт на наши порты, не на
        // порты VRChat. Внешних частей в папках сборки нет, поэтому окна здесь не появляются.
        using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var sendPort = ((IPEndPoint)game.Client.LocalEndPoint!).Port;
        int receivePort;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) receivePort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

        var config = Directory.CreateDirectory(Path.Combine(hub.DataDir, "config")).FullName;
        File.WriteAllText(Path.Combine(config, $"{VrSwitcher}.jsonc"), """{ "autoSwitch": false }""");
        File.WriteAllText(Path.Combine(config, $"{Music}.jsonc"), """{ "ducking": false, "notifications": false, "notifyPause": false }""");
        File.WriteAllText(Path.Combine(config, $"{Osc}.jsonc"), $$"""{ "sendPort": {{sendPort}}, "receivePort": {{receivePort}} }""");
        File.WriteAllText(Path.Combine(config, $"{Chatbox}.jsonc"), """
            { "openPanel": false, "modComponent": true, "status": "testing", "intervalSeconds": 2 }
            """);
        var plugins = new[] { Audio, VrSwitcher, Window, Specs, Notify, Music, Osc, Chatbox };
        foreach (var plugin in plugins) CopyPlugin(plugin, Path.Combine(hub.PluginsDir, plugin));
        await hub.BootAsync();

        await using var client = await hub.ConnectAsync();
        foreach (var plugin in plugins.Where(plugin => plugin != Specs))
        {
            var info = await InfoAsync(client, plugin);
            Assert.True(info.State == PluginState.Running, $"{plugin}: {info.Error}");
        }

        var stats = JsonDocument.Parse((await client.CallAsync("cap:system.stats", "stats.get")).Data).RootElement;
        Assert.True(stats.GetProperty("ramTotalGb").GetDouble() > 0);

        var idle = JsonDocument.Parse((await client.CallAsync("cap:user.activity", "idle.get")).Data).RootElement;
        Assert.True(idle.GetProperty("idleSeconds").GetDouble() >= 0);
        await client.CallAsync("cap:user.activity", "vr.get");

        var sessions = JsonDocument.Parse((await client.CallAsync("cap:audio.devices", "sessions.list")).Data).RootElement;
        Assert.Equal(JsonValueKind.Array, sessions.ValueKind);

        await client.CallAsync("cap:media.player", "player.current");

        await EventuallyAsync(async () =>
        {
            var state = JsonDocument.Parse((await client.CallAsync("cap:vrchat.chatbox", "chatbox.state")).Data).RootElement;
            var preview = state.GetProperty("preview").GetString()!;
            return preview.Contains("testing") && preview.Contains("CPU");
        });

        // OSC сам по себе: пакет от другого плагина или клиента приходит на порт «игры».
        await client.CallAsync("cap:vrchat.osc", "avatar.parameter.set", HubPayload.Json(new { name = "Toggle", value = 1 }));
        await EventuallyAsync(async () =>
        {
            var packet = await game.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return Encoding.UTF8.GetString(packet.Buffer).StartsWith("/avatar/parameters/Toggle", StringComparison.Ordinal);
        });

        // А то, что присылает «игра», становится событием и попадает в состояние аватара.
        var received = new TaskCompletionSource<string>();
        await client.SubscribeAsync("vrchat.osc.received", e => received.TrySetResult(e.Payload.AsText()));
        var fromGame = QuantumHub.Plugin.VrChatOsc.OscMessage.Encode("/avatar/parameters/Mood", 3);
        await game.SendAsync(fromGame, new IPEndPoint(IPAddress.Loopback, receivePort));
        Assert.Contains("Mood", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("Mood", (await client.CallAsync("cap:vrchat.osc", "avatar.get")).AsText());

        await client.CallAsync("cap:vrchat.chatbox", "chatbox.status.set", HubPayload.Text("new status"));
        var updated = JsonDocument.Parse((await client.CallAsync("cap:vrchat.chatbox", "chatbox.state")).Data).RootElement;
        Assert.Equal("new status", updated.GetProperty("status").GetString());
        Assert.Equal("new status", updated.GetProperty("statuses")[0].GetString());
    }

    [Fact]
    public async Task List_settings_are_stored_as_arrays_and_edited_as_lines()
    {
        await using var hub = await StartAsync(0);
        await hub.ShutdownAsync();
        var dir = Directory.CreateDirectory(Path.Combine(hub.PluginsDir, "with.list")).FullName;
        File.WriteAllText(Path.Combine(dir, "plugin.jsonc"), """
            { "id": "with.list", "version": "1.0.0", "activation": "onDemand",
              "settings": [ { "key": "apps", "type": "list", "group": "Приложения", "default": [ "a", "b" ] } ] }
            """);
        await hub.BootAsync();
        await using var client = await hub.ConnectAsync();

        var ui = await UiAsync(client, "with.list");
        Assert.Equal("a\nb", ui.State["config:apps"]);
        var page = ui.Pages.Single(p => p.Id == "settings");
        Assert.Equal("Приложения", page.Root.Children[0].Props["text"]);
        Assert.Equal("true", page.Root.Children[1].Children[0].Props["multiline"]);

        await client.CallAsync("hub", "hub.ui.event", HubPayload.Proto(new UiEvent
        {
            PluginId = "with.list", Event = "changed", Bind = "config:apps", Value = "one\n\n two \n",
        }));

        var stored = await client.CallAsync<ConfigValue>("hub", "hub.config.get", new ConfigGetRequest { PluginId = "with.list", Key = "apps" });
        Assert.Equal("""["one","two"]""", stored.Json);
    }

    [Fact]
    public async Task The_chatbox_waits_for_its_required_plugins()
    {
        await using var hub = await StartAsync(0, Chatbox);
        await using var client = await hub.ConnectAsync();

        Assert.Equal(PluginState.Blocked, (await InfoAsync(client, Chatbox)).State);
    }

    [Fact]
    public async Task The_discord_plugin_stays_away_from_discord_until_it_has_a_client_id()
    {
        // Настоящий плагин с настройками по умолчанию: Client ID пуст, поэтому пайпа Discord он не трогает.
        await using var hub = await StartAsync(0, Discord);
        await using var client = await hub.ConnectAsync();

        var info = await InfoAsync(client, Discord);
        Assert.True(info.State == PluginState.Running, info.Error);

        await EventuallyAsync(async () =>
        {
            var state = JsonDocument.Parse((await client.CallAsync("cap:discord.client", "discord.state")).Data).RootElement;
            return !state.GetProperty("connected").GetBoolean() && state.GetProperty("status").GetString()!.Contains("Client ID");
        });

        var ex = await Assert.ThrowsAsync<HubCallException>(async () =>
            await client.CallAsync("cap:discord.client", "voice.mute", HubPayload.Json(new { value = true })));
        Assert.Equal(StatusCode.FailedPrecondition, ex.Status);

        var settings = (await UiAsync(client, Discord)).Pages.Single(page => page.Id == "settings");
        Assert.Contains(settings.Root.Children, node => node.Children.Any(control => control.Type == "password"));

        await client.CallAsync("hub", "hub.plugins.uninstall", HubPayload.Proto(new PluginRef { Id = Discord }));
        Assert.False(Directory.Exists(Path.Combine(hub.PluginsDir, Discord)));
    }

    [Fact]
    public async Task A_stopping_plugin_can_still_call_others()
    {
        await using var hub = await StartAsync(0);
        await hub.ShutdownAsync();

        var dir = Directory.CreateDirectory(Path.Combine(hub.PluginsDir, "stop.caller")).FullName;
        File.Copy(typeof(StopCallerPlugin).Assembly.Location, Path.Combine(dir, "QuantumHub.Tests.dll"));
        File.WriteAllText(Path.Combine(dir, "plugin.jsonc"), $$"""
            { "id": "stop.caller", "version": "1.0.0",
              "service": { "assembly": "QuantumHub.Tests.dll", "type": "{{typeof(StopCallerPlugin).FullName}}" } }
            """);
        await hub.BootAsync();

        await hub.ShutdownAsync();

        var result = Path.Combine(hub.DataDir, "data", "stop.caller", StopCallerPlugin.ResultFile);
        Assert.Equal("reached", File.ReadAllText(result));
        await hub.BootAsync();
    }

    [Fact]
    public async Task Disconnected_clients_disappear_from_the_session_list()
    {
        await using var hub = await StartAsync(0);
        await using var observer = await hub.ConnectAsync("observer");
        var other = await hub.ConnectAsync("other");

        var sessions = await observer.CallAsync<SessionList>("hub", "hub.sessions.list", new PluginRef());
        Assert.Contains(sessions.Sessions, session => session is { ClientId: "other", Transport: "shm" });

        await other.DisposeAsync();

        await EventuallyAsync(async () =>
        {
            var list = await observer.CallAsync<SessionList>("hub", "hub.sessions.list", new PluginRef());
            return list.Sessions.All(session => session.ClientId != "other");
        });
    }
}
