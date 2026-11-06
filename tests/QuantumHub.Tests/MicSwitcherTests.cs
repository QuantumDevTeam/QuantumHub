using System.Text.Json;
using QuantumHub.Client;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Proto;
using QuantumHub.Plugin.AudioDevices;
using QuantumHub.Plugin.MicSwitcher;
using Xunit;
using static QuantumHub.Tests.TestHub;
using MicLevel = QuantumHub.Plugin.MicSwitcher.MicLevel;

namespace QuantumHub.Tests;

/// <summary>Выбор микрофона. Всё на подделках: настоящие микрофоны здесь не открываются.</summary>
public class MicSwitcherTests
{
    private const string Desk = "desk";
    private const string Headset = "headset";
    private const string Buds = "buds";
    private const string Cable = "cable";

    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeAudio : IMicAudio
    {
        public List<(string Id, string Name, bool Bluetooth)> Mics { get; } =
        [
            (Desk, "Microphone (fifine Chat)", false),
            (Headset, "Microphone (Edge Wireless Black)", false),
            (Buds, "Headset (Buds Hands-Free)", true),
            (Cable, "CABLE Output (VB-Audio Virtual Cable)", false),
        ];

        public Dictionary<string, double> Peaks { get; } = [];
        public HashSet<string> EverListened { get; } = [];
        public string? Default { get; set; } = Desk;
        public List<string> Switches { get; } = [];
        public int Reads { get; private set; }
        public int LastListened { get; private set; }
        public bool Fail { get; set; }

        public Task<IReadOnlyList<MicLevel>> ReadAsync(IReadOnlyList<string> listen)
        {
            Reads++;
            LastListened = listen.Count;
            EverListened.UnionWith(listen);
            IReadOnlyList<MicLevel> levels = Mics.Select(mic =>
            {
                var listening = listen.Contains(mic.Id);
                return new MicLevel(mic.Id, mic.Name, mic.Bluetooth, mic.Id == Default, listening ? Peaks.GetValueOrDefault(mic.Id) : 0, listening);
            }).ToList();
            return Task.FromResult(levels);
        }

        public Task SetDefaultAsync(string deviceId)
        {
            if (Fail) throw new InvalidOperationException("device is gone");
            Switches.Add(deviceId);
            Default = deviceId;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePrompt : IMicPrompt
    {
        public List<string> Shown { get; } = [];
        public int Hidden { get; private set; }
        public bool Fail { get; set; }

        public Task ShowAsync(MicState mic, TimeSpan duration)
        {
            if (Fail) throw new InvalidOperationException("no notifications");
            Shown.Add(mic.Id);
            return Task.CompletedTask;
        }

        public Task HideAsync()
        {
            Hidden++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVr : IVrState
    {
        public bool Active { get; set; }
    }

    private readonly FakeClock _clock = new();
    private readonly FakeAudio _audio = new();
    private readonly FakePrompt _prompt = new();
    private readonly FakeVr _vr = new();
    private readonly MicSwitchEngine _engine;
    private MicSwitchOptions _options = new() { Mode = MicMode.Auto, Blacklist = ["CABLE Output"] };

    public MicSwitcherTests() => _engine = new MicSwitchEngine(_audio, _prompt, _vr, () => _options, _clock, _ => { });

    private async Task Run(double seconds)
    {
        for (var time = TimeSpan.Zero; time < TimeSpan.FromSeconds(seconds); time += Step)
        {
            _clock.Now += Step;
            await _engine.PollAsync();
        }
    }

    /// <summary>Несколько секунд тишины: потоки открыты, счётчики знают, что было тихо.</summary>
    private Task Settle() => Run(1);

    private async Task Speak(string mic, double seconds)
    {
        _audio.Peaks[mic] = 0.5;
        await Run(seconds);
        _audio.Peaks[mic] = 0;
    }

    [Fact]
    public async Task Switches_when_a_steady_signal_appears_on_another_microphone()
    {
        await Settle();
        await Speak(Headset, 2);

        Assert.Equal([Headset], _audio.Switches);
        Assert.Contains("Edge Wireless", _engine.LastAction);
    }

    [Fact]
    public async Task A_click_does_not_switch_anything()
    {
        await Settle();
        await Speak(Headset, 0.25);
        await Run(3);

        Assert.Empty(_audio.Switches);
    }

    [Fact]
    public async Task Pauses_between_words_do_not_reset_the_signal()
    {
        await Settle();
        for (var i = 0; i < 4 && _audio.Switches.Count == 0; i++)
        {
            await Speak(Headset, 0.5);
            await Run(0.25);
        }

        Assert.Equal([Headset], _audio.Switches);
    }

    [Fact]
    public async Task A_constant_hum_is_not_a_voice()
    {
        _audio.Peaks[Headset] = 0.5;
        await Run(10);

        Assert.Empty(_audio.Switches);
    }

    [Fact]
    public async Task A_signal_below_the_threshold_does_not_count()
    {
        await Settle();
        _audio.Peaks[Headset] = 0.05;
        await Run(5);

        Assert.Empty(_audio.Switches);
    }

    [Fact]
    public async Task Stays_put_while_the_current_microphone_hears_the_same_voice()
    {
        await Settle();
        _audio.Peaks[Desk] = 0.5;
        _audio.Peaks[Headset] = 0.5;
        await Run(5);

        Assert.Empty(_audio.Switches);
    }

    [Fact]
    public async Task The_current_microphone_can_be_ignored_if_asked()
    {
        _options = _options with { RequireCurrentSilent = false };
        await Settle();
        _audio.Peaks[Desk] = 0.5;
        _audio.Peaks[Headset] = 0.5;
        await Run(3);

        Assert.Equal([Headset], _audio.Switches);
    }

    [Fact]
    public async Task Does_not_bounce_back_right_after_switching()
    {
        await Settle();
        await Speak(Headset, 2);
        await Speak(Desk, 3);

        Assert.Equal([Headset], _audio.Switches);
    }

    [Fact]
    public async Task Goes_back_when_the_author_returns_after_the_cooldown()
    {
        await Settle();
        await Speak(Headset, 2);
        await Run(_options.Cooldown.TotalSeconds + 1);
        await Speak(Desk, 2);

        Assert.Equal([Headset, Desk], _audio.Switches);
    }

    [Fact]
    public async Task Does_not_argue_with_a_change_made_by_someone_else()
    {
        await Settle();
        _audio.Default = Headset;
        await Run(0.5);
        await Speak(Desk, 3);

        Assert.Empty(_audio.Switches);
    }

    [Fact]
    public async Task Hint_mode_only_offers_until_the_click()
    {
        _options = _options with { Mode = MicMode.Hint };
        await Settle();
        await Speak(Headset, 2);

        Assert.Equal([Headset], _prompt.Shown);
        Assert.Empty(_audio.Switches);
        Assert.Equal(Headset, _engine.Hint?.Id);
        Assert.Contains("Edge Wireless", _engine.Status);

        await _engine.AcceptHintAsync();

        Assert.Equal([Headset], _audio.Switches);
        Assert.Null(_engine.Hint);
        Assert.Equal(1, _prompt.Hidden);
    }

    [Fact]
    public async Task A_refused_hint_does_nothing_and_does_not_come_back_at_once()
    {
        _options = _options with { Mode = MicMode.Hint };
        await Settle();
        _audio.Peaks[Headset] = 0.5;
        await Run(2);
        await _engine.DismissHintAsync();
        await Run(5);

        Assert.Empty(_audio.Switches);
        Assert.Equal([Headset], _prompt.Shown);
    }

    [Fact]
    public async Task A_click_on_an_old_hint_changes_nothing()
    {
        _options = _options with { Mode = MicMode.Hint };
        await Settle();
        await Speak(Headset, 2);
        _clock.Now += _options.HintTime;

        await _engine.AcceptHintAsync();

        Assert.Empty(_audio.Switches);
    }

    [Fact]
    public async Task The_hint_disappears_if_the_author_switched_by_hand()
    {
        _options = _options with { Mode = MicMode.Hint };
        await Settle();
        await Speak(Headset, 2);

        _audio.Default = Headset;
        await Run(0.25);

        Assert.Null(_engine.Hint);
        Assert.Equal(1, _prompt.Hidden);
    }

    [Fact]
    public async Task Without_notifications_the_hint_is_still_visible_on_the_page()
    {
        _options = _options with { Mode = MicMode.Hint };
        _prompt.Fail = true;
        await Settle();
        await Speak(Headset, 2);

        Assert.Equal(Headset, _engine.Hint?.Id);
    }

    [Theory]
    [InlineData("edge wireless")]
    [InlineData("Edge")]
    public async Task A_blacklisted_microphone_is_never_opened(string part)
    {
        _options = _options with { Blacklist = [part] };
        await Settle();
        await Speak(Headset, 3);

        Assert.DoesNotContain(Headset, _audio.EverListened);
        Assert.Empty(_audio.Switches);
    }

    [Fact]
    public async Task The_whitelist_limits_where_to_switch_but_the_current_microphone_is_still_heard()
    {
        _options = _options with { Whitelist = ["Edge Wireless"], Blacklist = [] };
        await Settle();

        Assert.Equal([Desk, Headset], _audio.EverListened.Order().ToArray());

        await Speak(Headset, 2);
        await Run(_options.Cooldown.TotalSeconds + 1);
        await Speak(Desk, 3);

        Assert.Equal([Headset], _audio.Switches);
    }

    [Fact]
    public async Task A_bluetooth_microphone_is_left_alone_by_default()
    {
        await Settle();
        await Speak(Buds, 3);

        Assert.DoesNotContain(Buds, _audio.EverListened);
        Assert.Empty(_audio.Switches);
        Assert.Equal(MicFilter.Bluetooth, _engine.Mics.Single(mic => mic.Id == Buds).Excluded);
    }

    [Fact]
    public async Task A_bluetooth_microphone_is_used_only_when_allowed()
    {
        _options = _options with { WatchBluetooth = true };
        await Settle();
        await Speak(Buds, 2);

        Assert.Equal([Buds], _audio.Switches);
    }

    [Fact]
    public async Task A_bluetooth_default_microphone_is_not_opened_and_counts_as_quiet()
    {
        _audio.Default = Buds;
        await Settle();
        await Speak(Headset, 2);

        Assert.DoesNotContain(Buds, _audio.EverListened);
        Assert.Equal([Headset], _audio.Switches);
    }

    [Fact]
    public async Task Turned_off_it_does_not_even_ask_for_the_devices()
    {
        _options = _options with { Mode = MicMode.Off };
        await Run(3);

        Assert.Equal(0, _audio.Reads);
        Assert.Contains("не заняты", _engine.Status);
    }

    [Fact]
    public async Task Turning_it_off_lets_the_microphones_go()
    {
        await Settle();
        Assert.True(_audio.LastListened > 0);

        _options = _options with { Mode = MicMode.Off };
        var reads = _audio.Reads;
        await Run(1);

        Assert.Equal(reads, _audio.Reads);
        Assert.All(_engine.Mics, mic => Assert.False(mic.Listening));
    }

    [Fact]
    public async Task Vr_comes_first_nothing_is_opened_or_switched()
    {
        _vr.Active = true;
        await Run(1);
        _audio.Peaks[Headset] = 0.5;
        await Run(3);

        Assert.Empty(_audio.EverListened);
        Assert.Empty(_audio.Switches);
        Assert.Contains("VR", _engine.Status);
    }

    [Fact]
    public async Task Starting_vr_withdraws_the_hint()
    {
        _options = _options with { Mode = MicMode.Hint };
        await Settle();
        await Speak(Headset, 2);

        _vr.Active = true;
        await Run(0.25);
        await _engine.AcceptHintAsync();

        Assert.Equal(1, _prompt.Hidden);
        Assert.Empty(_audio.Switches);
    }

    [Fact]
    public async Task After_vr_it_waits_for_the_sound_to_settle()
    {
        _vr.Active = true;
        await Run(1);
        _vr.Active = false;
        await Settle();
        await Speak(Headset, 3);

        Assert.Empty(_audio.Switches);
    }

    [Fact]
    public async Task The_open_page_shows_levels_but_does_not_turn_the_switching_on()
    {
        _options = _options with { Mode = MicMode.Off };
        _engine.KeepListening();
        await Run(0.5);
        _engine.KeepListening();
        _audio.Peaks[Headset] = 0.5;
        await Run(0.5);

        Assert.Equal(0.5, _engine.Mics.Single(mic => mic.Id == Headset).Peak);
        Assert.DoesNotContain(Buds, _audio.EverListened);

        for (var i = 0; i < 12; i++)
        {
            _engine.KeepListening();
            await Run(0.25);
        }

        Assert.Empty(_audio.Switches);
    }

    [Fact]
    public async Task A_page_that_went_away_silently_releases_the_microphones()
    {
        _options = _options with { Mode = MicMode.Off };
        _engine.KeepListening();
        await Run(0.5);
        Assert.True(_audio.Reads > 0);

        await Run(3);
        var reads = _audio.Reads;
        await Run(1);

        Assert.Equal(reads, _audio.Reads);
    }

    [Fact]
    public async Task Closing_the_page_releases_the_microphones_at_once()
    {
        _options = _options with { Mode = MicMode.Off };
        _engine.KeepListening();
        await Run(0.5);

        _engine.StopListening();
        var reads = _audio.Reads;
        await Run(0.5);

        Assert.Equal(reads, _audio.Reads);
    }

    [Fact]
    public async Task The_author_can_switch_by_hand_even_during_vr()
    {
        _vr.Active = true;
        await _engine.SwitchAsync(Headset);

        Assert.Equal([Headset], _audio.Switches);
    }

    [Fact]
    public async Task A_failed_switch_is_reported_and_not_repeated_on_every_check()
    {
        _audio.Fail = true;
        await Settle();
        await Speak(Headset, 3);
        _audio.Fail = false;
        await Run(1);

        Assert.Contains("Не удалось", _engine.LastAction);
        Assert.Empty(_audio.Switches);
    }

    [Fact]
    public async Task A_microphone_that_was_plugged_in_later_is_picked_up()
    {
        _audio.Mics.RemoveAll(mic => mic.Id == Headset);
        await Settle();
        _audio.Mics.Add((Headset, "Microphone (Edge Wireless Black)", false));
        await Settle();
        await Speak(Headset, 2);

        Assert.Equal([Headset], _audio.Switches);
    }

    [Fact]
    public void The_levels_text_explains_every_line()
    {
        var text = MicLevelsText.Format(
        [
            new MicState(Desk, "Desk", false, true, 0.5, true, null),
            new MicState(Buds, "Buds", true, false, 0, false, MicFilter.Bluetooth),
            new MicState(Cable, "Cable", false, false, 0, false, MicFilter.Blacklisted),
        ], 0.1).Split('\n');

        Assert.StartsWith("▶ ██|", text[0]);
        Assert.Contains("50%  Desk", text[0]);
        Assert.Contains("Bluetooth", text[1]);
        Assert.Contains("чёрном", text[2]);
    }

    private sealed class FakeMeter(Func<double> peak) : IMicMeter
    {
        public bool Failed { get; set; }
        public bool Disposed { get; private set; }

        public double ReadPeak() => peak();

        public void Dispose() => Disposed = true;
    }

    private sealed class Pool : IDisposable
    {
        private readonly FakeClock _clock = new();

        public Pool() => Meters = new MicMeterPool(Open, _clock, Warnings.Add);

        public MicMeterPool Meters { get; }
        public Dictionary<string, FakeMeter> Opened { get; } = [];
        public List<string> Warnings { get; } = [];
        public int OpenCalls { get; private set; }
        public bool RefuseToOpen { get; set; }

        public void Pass(TimeSpan time) => _clock.Now += time;

        public void Dispose() => Meters.Dispose();

        private FakeMeter Open(string id)
        {
            OpenCalls++;
            if (RefuseToOpen) throw new InvalidOperationException("in use");
            return Opened[id] = new FakeMeter(() => 0.25);
        }
    }

    [Fact]
    public void The_pool_keeps_a_stream_open_while_it_is_asked_about()
    {
        using var pool = new Pool();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(0.25, pool.Meters.Read([Desk])[Desk]);
            pool.Pass(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(1, pool.OpenCalls);
        Assert.False(pool.Opened[Desk].Disposed);
    }

    [Fact]
    public void The_pool_lets_go_of_a_microphone_nobody_asks_about()
    {
        using var pool = new Pool();
        pool.Meters.Read([Desk]);
        pool.Pass(MicMeterPool.Lease);
        pool.Meters.Expire();

        Assert.True(pool.Opened[Desk].Disposed);
        Assert.Equal(0, pool.Meters.OpenCount);
    }

    [Fact]
    public void An_empty_request_opens_nothing()
    {
        using var pool = new Pool();

        Assert.Empty(pool.Meters.Read([]));
        Assert.Equal(0, pool.OpenCalls);
    }

    [Fact]
    public void A_microphone_that_will_not_open_is_retried_later_and_not_on_every_call()
    {
        using var pool = new Pool { RefuseToOpen = true };
        Assert.Empty(pool.Meters.Read([Desk]));
        Assert.Empty(pool.Meters.Read([Desk]));
        Assert.Equal(1, pool.OpenCalls);
        Assert.Single(pool.Warnings);

        pool.RefuseToOpen = false;
        pool.Pass(MicMeterPool.RetryDelay);

        Assert.Equal(0.25, pool.Meters.Read([Desk])[Desk]);
    }

    [Fact]
    public void A_stream_that_broke_is_closed_and_reopened_later()
    {
        using var pool = new Pool();
        pool.Meters.Read([Desk]);
        var broken = pool.Opened[Desk];
        broken.Failed = true;

        Assert.Empty(pool.Meters.Read([Desk]));
        Assert.True(broken.Disposed);

        pool.Pass(MicMeterPool.RetryDelay);

        Assert.Single(pool.Meters.Read([Desk]));
        Assert.NotSame(broken, pool.Opened[Desk]);
    }

    [Fact]
    public void Disposing_the_pool_closes_everything()
    {
        var pool = new Pool();
        pool.Meters.Read([Desk, Headset]);
        pool.Dispose();

        Assert.All(pool.Opened.Values, meter => Assert.True(meter.Disposed));
    }

    [Theory]
    [InlineData("BTHENUM", null, "Headset", true)]
    [InlineData("BTHHFENUM", null, "Headset", true)]
    [InlineData(null, @"{2}.\\?\bthhfenum#{0000111e}", "Headset", true)]
    [InlineData(null, null, "Headset (WH-1000XM4 Hands-Free AG Audio)", true)]
    [InlineData(null, null, "Bluetooth Headset", true)]
    [InlineData("USB", @"{1}.USB\VID_3206&PID_0871&MI_00\6&2FE04C3F&0&0000", "Microphone (Edge Wireless Black)", false)]
    [InlineData("ROOT", @"{1}.ROOT\MEDIA\0000", "Microphone (PicoStreamingMicrophone)", false)]
    public void Bluetooth_is_told_apart_from_a_radio_dongle(string? enumerator, string? path, string name, bool expected) =>
        Assert.Equal(expected, BluetoothMic.Detect(enumerator, path, name));

    [Fact]
    public async Task The_plugin_works_through_the_hub_and_opens_nothing_while_turned_off()
    {
        await using var hub = await StartAsync(0, Audio, MicSwitcher);
        await using var client = await hub.ConnectAsync();

        var plugins = await client.CallAsync<PluginList>("hub", "hub.plugins.list", new PluginRef());
        var info = plugins.Plugins.Single(plugin => plugin.Manifest.Id == MicSwitcher);
        Assert.True(info.State == PluginState.Running, info.Error);

        var levels = JsonDocument.Parse((await client.CallAsync("cap:audio.micswitch", "mic.levels")).Data).RootElement;
        Assert.Equal(JsonValueKind.Array, levels.ValueKind);
        Assert.All(levels.EnumerateArray(), mic => Assert.False(mic.GetProperty("listening").GetBoolean()));

        var raw = JsonDocument.Parse((await client.CallAsync("cap:audio.devices", "mics.levels")).Data).RootElement;
        Assert.All(raw.EnumerateArray(), mic => Assert.False(mic.GetProperty("listening").GetBoolean()));

        await client.CallAsync("cap:audio.micswitch", "mic.current");
        var page = await client.CallAsync<UiResponse>("hub", "hub.ui.get", new UiRequest { PluginId = MicSwitcher });
        Assert.Contains(page.Pages.SelectMany(item => item.Root.Children), node => node.Type == "live");

        var state = JsonDocument.Parse((await client.CallAsync("cap:audio.micswitch", "mode.set", HubPayload.Json(new { mode = "off" }))).Data).RootElement;
        Assert.Equal("off", state.GetProperty("mode").GetString());

        var ex = await Assert.ThrowsAsync<HubCallException>(async () =>
            await client.CallAsync("cap:audio.micswitch", "mode.set", HubPayload.Json(new { mode = "loud" })));
        Assert.Equal(StatusCode.InvalidArgument, ex.Status);
    }

    [Fact]
    public async Task Notification_buttons_reach_the_overlay_and_the_click_comes_back()
    {
        await using var hub = await StartAsync(0, Notify);
        await using var client = await hub.ConnectAsync();

        var shown = new TaskCompletionSource<JsonElement>();
        var clicked = new TaskCompletionSource<JsonElement>();
        var hidden = new TaskCompletionSource<JsonElement>();
        await client.SubscribeAsync("notify.display", e => shown.TrySetResult(JsonDocument.Parse(e.Payload.Data).RootElement));
        await client.SubscribeAsync("notify.action", e => clicked.TrySetResult(JsonDocument.Parse(e.Payload.Data).RootElement));
        await client.SubscribeAsync("notify.hide", e => hidden.TrySetResult(JsonDocument.Parse(e.Payload.Data).RootElement));

        await client.CallAsync("cap:ui.notifications", "notify.show", HubPayload.Json(new
        {
            key = "mic",
            title = "title",
            durationSeconds = 12,
            actions = new[] { new { id = "switch", label = "Switch" } },
        }));

        var display = await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(12000, display.GetProperty("durationMs").GetInt32());
        Assert.Equal("switch", display.GetProperty("request").GetProperty("actions")[0].GetProperty("id").GetString());

        await client.CallAsync("cap:ui.notifications", "notify.action", HubPayload.Json(new { key = "mic", action = "switch" }));
        var click = await clicked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("mic", click.GetProperty("key").GetString());
        Assert.Equal("switch", click.GetProperty("action").GetString());

        var pending = await client.CallAsync("cap:ui.notifications", "notify.pending");
        Assert.Equal("null", pending.AsText());

        await client.CallAsync("cap:ui.notifications", "notify.hide", HubPayload.Json(new { key = "mic" }));
        Assert.Equal("mic", (await hidden.Task.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("key").GetString());
    }
}
