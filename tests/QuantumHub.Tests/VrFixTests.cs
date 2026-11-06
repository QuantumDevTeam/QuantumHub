using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Proto;
using QuantumHub.Core;
using QuantumHub.Core.Config;
using QuantumHub.Plugin.Chatbox;
using QuantumHub.VrDashboard;
using Xunit;
using static QuantumHub.Tests.TestHub;

namespace QuantumHub.Tests;

/// <summary>Определение VR, порча списков, панель SteamVR на поддельном рантайме и статус Away.</summary>
public sealed class VrFixTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 21, 42, 0, TimeSpan.Zero);
    private static readonly ChatModules All = new(true, false, false, false, false);

    /// <summary>Настоящий хаб и настоящий User Activity: поле списка присылает переводы строк так, как это делает WinUI.</summary>
    [Fact]
    public async Task Vr_is_detected_after_the_list_was_edited_in_the_window()
    {
        await using var hub = await StartAsync(0, Window);
        await using var client = await hub.ConnectAsync();

        using var current = Process.GetCurrentProcess();
        await client.CallAsync("hub", "hub.ui.event", HubPayload.Proto(new UiEvent
        {
            PluginId = Window,
            Event = "changed",
            Bind = "config:vrProcesses",
            Value = $"no-such-process\r{current.ProcessName}\r",
        }));

        var state = (await client.CallAsync("cap:user.activity", "vr.get")).AsText();
        Assert.Contains("true", state);
    }

    /// <summary>Вся цепочка: найден процесс VR, User Activity сообщил, плагин панели попытался её запустить. Самой панели здесь нет.</summary>
    [Fact]
    public async Task The_panel_is_launched_when_vr_appears()
    {
        await using var hub = await StartAsync(0, Window, TestHub.VrDashboard);
        await using var client = await hub.ConnectAsync();
        bool Launched() => hub.Hub.Log.Recent().Any(entry => entry.Message.Contains("QuantumHub.VrDashboard.exe"));
        Assert.False(Launched());

        using var current = Process.GetCurrentProcess();
        await client.CallAsync("hub", "hub.ui.event", HubPayload.Proto(new UiEvent
        {
            PluginId = Window,
            Event = "changed",
            Bind = "config:vrProcesses",
            Value = current.ProcessName,
        }));

        await UntilAsync(Launched, 1000);
    }

    [Fact]
    public void A_list_spoiled_by_an_older_version_is_repaired_on_start()
    {
        var dir = Directory.CreateTempSubdirectory("qh-config-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.jsonc"), """{ "names": [ "one\rtwo", "three" ], "text": "keep\rme" }""");
            using var log = new HubLog(dir);
            using (var store = new ConfigStore(dir, log))
            {
                var manifest = new PluginManifest { Id = "a" };
                manifest.Settings.Add(new SettingDescriptor { Key = "names", Type = "list" });
                manifest.Settings.Add(new SettingDescriptor { Key = "text", Type = "string" });
                store.ApplyDefaults(manifest);

                Assert.Equal("""["one","two","three"]""", store.Get("a", "names")!.ToJsonString());
                Assert.Equal("keep\rme", store.Get("a", "text")!.GetValue<string>());
            }

            Assert.DoesNotContain("one\\rtwo", File.ReadAllText(Path.Combine(dir, "a.jsonc")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class FakeRuntime : IOverlayRuntime
    {
        public Queue<OverlayInput> Inputs { get; } = new();
        public int Pixels { get; private set; }
        public List<string> Files { get; } = [];
        public bool RejectPixels { get; init; }
        public string? KeyboardOpenedWith { get; private set; }
        public string Typed { get; set; } = string.Empty;

        public string? Start(string icon) => null;
        public void Stop() { }
        public bool IsVisible => false;

        public bool TryPoll(out OverlayInput input) => Inputs.TryDequeue(out input);

        public string? ShowPixels(byte[] rgba)
        {
            Pixels++;
            return RejectPixels ? "RequestFailed" : null;
        }

        public string? ShowFile(string path)
        {
            Files.Add(path);
            return null;
        }

        public string? OpenKeyboard(string description, string text)
        {
            KeyboardOpenedWith = text;
            return null;
        }

        public string KeyboardText() => Typed;
    }

    private sealed class FakePainter : IPanelPainter
    {
        /// <summary>Что вернёт рисование: тест меняет это, как будто изменилась картинка.</summary>
        public byte Pixel { get; set; }

        public byte[] Rgba(PanelView view, PanelState state) => [Pixel];
        public void Png(PanelView view, PanelState state, string path) { }
    }

    private sealed class FakeBus : IHubBus
    {
        private readonly Lock _gate = new();
        private readonly List<(string Method, string Payload)> _calls = [];
        /// <summary>Настройка луча: считать ли его снизу вверх, как это делает SteamVR.</summary>
        public bool Flip { get; init; } = true;
        public string Status { get; set; } = "tea";

        public IReadOnlyList<(string Method, string Payload)> Calls
        {
            get
            {
                lock (_gate) return _calls.ToList();
            }
        }

        public ValueTask<HubPayload> CallAsync(string target, string method, HubPayload payload = default, TimeSpan? timeout = null, CancellationToken token = default)
        {
            var reply = method switch
            {
                "hub.config.get" => HubPayload.Proto(new ConfigValue { Found = true, Json = Flip ? "{}" : """{ "flipPointer": false }""" }),
                "session.get" => HubPayload.Text("""{ "onHeadset": false }"""),
                "discord.state" => HubPayload.Text("""{ "connected": true, "authorized": true, "muted": false }"""),
                "sleep.get" => HubPayload.Text("""{ "enabled": false }"""),
                "chatbox.state" => HubPayload.Text($$"""{ "gameRunning": true, "sending": true, "status": "{{Status}}", "statuses": ["tea"], "preview": "x" }"""),
                _ => HubPayload.Empty,
            };

            if (!method.EndsWith(".get", StringComparison.Ordinal) && !method.EndsWith(".state", StringComparison.Ordinal))
                lock (_gate) _calls.Add((method, payload.AsText()));
            return ValueTask.FromResult(reply);
        }

        public ValueTask PublishAsync(string topic, HubPayload payload = default, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask<IDisposable> SubscribeAsync(string topic, Action<HubEvent> handler, CancellationToken token = default) => throw new NotSupportedException();
        public IDisposable Handle(string method, HubRequestHandler handler) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<DependencyResolution>> ResolveAsync(IEnumerable<Dependency> dependencies, CancellationToken token = default) => throw new NotSupportedException();
    }

    private static async Task<(OverlayHost Host, PanelView View)> ReadyAsync(FakeRuntime runtime, FakeBus bus, FakePainter? painter = null)
    {
        var host = new OverlayHost(runtime, painter ?? new FakePainter(), new PanelClient(bus), _ => { });
        await host.RefreshAsync();
        Assert.True(host.Step());

        var client = new PanelClient(bus);
        var options = await client.ReadOptionsAsync();
        return (host, PanelLayout.Build(await client.ReadAsync(options), options));
    }

    private static OverlayInput At(PanelView view, string id, OverlayInputKind kind = OverlayInputKind.Press, bool flip = true)
    {
        var bounds = view.Tiles.Single(tile => tile.Id == id).Bounds;
        var y = bounds.Y + bounds.Height / 2;
        return new OverlayInput(kind, bounds.X + bounds.Width / 2, flip ? PanelLayout.Height - y : y);
    }

    private static async Task UntilAsync(Func<bool> condition, int attempts = 300)
    {
        for (var i = 0; i < attempts && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task A_press_goes_where_the_beam_points_counting_from_the_bottom()
    {
        var runtime = new FakeRuntime();
        var bus = new FakeBus();
        var (host, view) = await ReadyAsync(runtime, bus);

        runtime.Inputs.Enqueue(At(view, PanelLayout.Audio));
        host.Step();

        await UntilAsync(() => bus.Calls.Count > 0);
        Assert.Equal("audio.target.set", bus.Calls[0].Method);
        Assert.Contains("headset", bus.Calls[0].Payload);
    }

    [Fact]
    public async Task The_setting_can_count_the_pointer_from_the_top()
    {
        var runtime = new FakeRuntime();
        var bus = new FakeBus { Flip = false };
        var (host, view) = await ReadyAsync(runtime, bus);

        runtime.Inputs.Enqueue(At(view, PanelLayout.Sleep, flip: false));
        host.Step();

        await UntilAsync(() => bus.Calls.Count > 0);
        Assert.Equal("sleep.set", bus.Calls[0].Method);
    }

    [Fact]
    public async Task A_press_outside_the_buttons_does_nothing()
    {
        var runtime = new FakeRuntime();
        var bus = new FakeBus();
        var (host, _) = await ReadyAsync(runtime, bus);

        runtime.Inputs.Enqueue(new OverlayInput(OverlayInputKind.Press, 1, 1));
        host.Step();

        await Task.Delay(100);
        Assert.Empty(bus.Calls);
    }

    [Fact]
    public async Task The_picture_goes_through_a_file_when_steamvr_refuses_the_pixels()
    {
        var runtime = new FakeRuntime { RejectPixels = true };
        await ReadyAsync(runtime, new FakeBus());

        Assert.Equal(1, runtime.Pixels);
        Assert.Single(runtime.Files);
    }

    [Fact]
    public async Task Moving_the_beam_does_not_touch_the_picture()
    {
        var runtime = new FakeRuntime();
        var (host, view) = await ReadyAsync(runtime, new FakeBus());
        var before = runtime.Pixels;

        runtime.Inputs.Enqueue(At(view, PanelLayout.Audio, OverlayInputKind.Move));
        runtime.Inputs.Enqueue(At(view, PanelLayout.Mute, OverlayInputKind.Move));
        host.Step();

        Assert.Equal(before, runtime.Pixels);
    }

    [Fact]
    public async Task The_same_picture_is_not_sent_twice()
    {
        var runtime = new FakeRuntime();
        var bus = new FakeBus();
        var painter = new FakePainter();
        var (host, _) = await ReadyAsync(runtime, bus, painter);
        Assert.Equal(1, runtime.Pixels);

        // Данные другие, а картинка та же: например, поменялся статус, которого не видно.
        bus.Status = "coffee";
        await host.RefreshAsync();
        host.Step();
        Assert.Equal(1, runtime.Pixels);

        painter.Pixel = 1;
        bus.Status = "water";
        await host.RefreshAsync();
        host.Step();
        Assert.Equal(2, runtime.Pixels);
    }

    [Fact]
    public async Task The_keyboard_sets_the_status()
    {
        var runtime = new FakeRuntime();
        var bus = new FakeBus();
        var (host, view) = await ReadyAsync(runtime, bus);

        runtime.Inputs.Enqueue(At(view, PanelLayout.EditStatus));
        host.Step();
        Assert.Equal("tea", runtime.KeyboardOpenedWith);

        runtime.Typed = "coffee";
        runtime.Inputs.Enqueue(new OverlayInput(OverlayInputKind.KeyboardDone));
        host.Step();

        await UntilAsync(() => bus.Calls.Count > 0);
        Assert.Equal(("chatbox.status.set", "coffee"), bus.Calls[0]);
    }

    [Fact]
    public async Task Closing_the_keyboard_sends_nothing()
    {
        var runtime = new FakeRuntime();
        var bus = new FakeBus();
        var (host, view) = await ReadyAsync(runtime, bus);

        runtime.Inputs.Enqueue(At(view, PanelLayout.SendChat));
        runtime.Inputs.Enqueue(new OverlayInput(OverlayInputKind.KeyboardClosed));
        runtime.Inputs.Enqueue(new OverlayInput(OverlayInputKind.KeyboardDone));
        host.Step();

        await Task.Delay(100);
        Assert.Empty(bus.Calls);
    }

    [Fact]
    public async Task The_loop_ends_when_steamvr_quits()
    {
        var runtime = new FakeRuntime();
        var (host, _) = await ReadyAsync(runtime, new FakeBus());

        runtime.Inputs.Enqueue(new OverlayInput(OverlayInputKind.Quit));

        Assert.False(host.Step());
    }

    [Fact]
    public void Away_replaces_the_status()
    {
        var inputs = new ChatInputs("drinking", null, false, null, null, Now, Away: true);
        Assert.Equal("💤 Away", ChatboxComposer.Compose(inputs, All));
        Assert.Equal("💤 AFK", ChatboxComposer.Compose(inputs, All, new ChatFormat(AwayText: "AFK")));
        Assert.Equal("💬 drinking", ChatboxComposer.Compose(inputs with { Away = false }, All));
    }

    [Fact]
    public void Away_shows_even_without_a_status()
    {
        Assert.Equal("💤 Away", ChatboxComposer.Compose(new ChatInputs(null, null, false, null, null, Now, Away: true), All));
    }

    [Fact]
    public void A_template_gets_away_through_both_placeholders()
    {
        var format = new ChatFormat(Template: ["[{status}]", "{away}"]);
        Assert.Equal("[Away]\nAway", ChatboxComposer.Compose(new ChatInputs("drinking", null, false, null, null, Now, Away: true), All, format));
        Assert.Equal("[drinking]", ChatboxComposer.Compose(new ChatInputs("drinking", null, false, null, null, Now), All, format));
        Assert.True(ChatboxComposer.Needed(All, new ChatFormat(Template: ["{away}"])).Status);
    }
}
