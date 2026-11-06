using QuantumHub.Plugin.VrSwitcher;
using Xunit;

namespace QuantumHub.Tests;

/// <summary>Логика переключения звука. Всё на подделках: настоящие устройства здесь не трогаются.</summary>
public class VrAudioEngineTests
{
    private const string PcOut = "pc-out";
    private const string PcMic = "pc-mic";
    private const string PicoOut = "pico-out";
    private const string PicoMic = "pico-mic";

    private sealed class FakeAudio : IAudioDevices
    {
        public List<AudioDevice> Playback { get; } =
        [
            new(PcOut, "Speakers (3- fifine Headset)"),
            new(PicoOut, "Speakers (Pico Streaming Speaker)"),
        ];

        public List<AudioDevice> Recording { get; } =
        [
            new(PcMic, "Microphone (fifine Chat)"),
            new(PicoMic, "Microphone (PicoStreamingMicrophone)"),
        ];

        public string DefaultPlayback { get; set; } = PcOut;
        public string DefaultRecording { get; set; } = PcMic;
        public int Changes { get; private set; }
        public bool Fail { get; set; }

        public Task<IReadOnlyList<AudioDevice>> ListAsync(AudioFlow flow) =>
            Task.FromResult<IReadOnlyList<AudioDevice>>(flow == AudioFlow.Playback ? Playback : Recording);

        public Task<string?> GetDefaultAsync(AudioFlow flow) =>
            Task.FromResult<string?>(flow == AudioFlow.Playback ? DefaultPlayback : DefaultRecording);

        public Task SetDefaultAsync(string deviceId)
        {
            if (Fail) throw new InvalidOperationException("device is gone");
            Changes++;
            if (Playback.Exists(device => device.Id == deviceId)) DefaultPlayback = deviceId;
            else DefaultRecording = deviceId;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProbe : IVrProbe
    {
        public bool Running { get; set; }

        public bool IsRunning() => Running;
    }

    private sealed class FakeMemory : IVrAudioMemory
    {
        public string? Playback { get; set; }
        public string? Recording { get; set; }
    }

    private readonly FakeAudio _audio = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeMemory _memory = new();
    private VrAudioOptions _options = new(VrAudioPreset.Pico, true);

    private VrAudioEngine Engine() => new(_audio, _probe, _memory, () => _options, _ => { });

    private static async Task PollTimes(VrAudioEngine engine, int count)
    {
        for (var i = 0; i < count; i++) await engine.PollAsync();
    }

    [Fact]
    public async Task Switches_to_the_headset_when_steamvr_starts_and_back_when_it_exits()
    {
        var engine = Engine();
        await engine.PollAsync();
        Assert.Equal(PcOut, _audio.DefaultPlayback);

        _probe.Running = true;
        await engine.PollAsync();
        Assert.Equal(PicoOut, _audio.DefaultPlayback);
        Assert.Equal(PicoMic, _audio.DefaultRecording);

        _probe.Running = false;
        await PollTimes(engine, 3);
        Assert.Equal(PcOut, _audio.DefaultPlayback);
        Assert.Equal(PcMic, _audio.DefaultRecording);
        Assert.Null(_memory.Playback);
    }

    [Fact]
    public async Task A_short_steamvr_restart_does_not_touch_the_sound()
    {
        var engine = Engine();
        _probe.Running = true;
        await engine.PollAsync();
        var changes = _audio.Changes;

        _probe.Running = false;
        await PollTimes(engine, 2);
        _probe.Running = true;
        await engine.PollAsync();

        Assert.Equal(changes, _audio.Changes);
        Assert.True(engine.SessionActive);
    }

    [Fact]
    public async Task Reports_the_session_once_per_change()
    {
        var engine = Engine();
        var seen = new List<bool>();
        engine.SessionChanged += seen.Add;

        _probe.Running = true;
        await PollTimes(engine, 3);
        _probe.Running = false;
        await PollTimes(engine, 5);

        Assert.Equal([true, false], seen);
    }

    [Fact]
    public async Task Waits_for_headset_devices_that_appear_after_steamvr()
    {
        var pico = _audio.Playback[1];
        _audio.Playback.Remove(pico);
        var engine = Engine();
        _probe.Running = true;

        await PollTimes(engine, 3);
        Assert.Equal(PcOut, _audio.DefaultPlayback);
        Assert.Contains("наушники", engine.Status);

        _audio.Playback.Add(pico);
        await engine.PollAsync();
        Assert.Equal(PicoOut, _audio.DefaultPlayback);
    }

    [Fact]
    public async Task Does_not_fight_the_user_after_switching()
    {
        var engine = Engine();
        _probe.Running = true;
        await engine.PollAsync();

        _audio.DefaultPlayback = PcOut;
        await PollTimes(engine, 5);

        Assert.Equal(PcOut, _audio.DefaultPlayback);
    }

    [Fact]
    public async Task Retries_after_a_failed_switch()
    {
        var engine = Engine();
        _audio.Fail = true;
        _probe.Running = true;
        await engine.PollAsync();
        Assert.Contains("device is gone", engine.Status);

        _audio.Fail = false;
        await engine.PollAsync();
        Assert.Equal(PicoOut, _audio.DefaultPlayback);
    }

    [Fact]
    public async Task Leaves_the_sound_alone_when_auto_switch_is_off()
    {
        _options = _options with { AutoSwitch = false };
        var engine = Engine();
        _probe.Running = true;
        await PollTimes(engine, 3);
        _probe.Running = false;
        await PollTimes(engine, 3);

        Assert.Equal(0, _audio.Changes);
    }

    [Fact]
    public async Task Restores_after_a_crash_in_the_middle_of_vr()
    {
        _audio.DefaultPlayback = PicoOut;
        _audio.DefaultRecording = PicoMic;
        _memory.Playback = PcOut;
        _memory.Recording = PcMic;

        await Engine().PollAsync();

        Assert.Equal(PcOut, _audio.DefaultPlayback);
        Assert.Equal(PcMic, _audio.DefaultRecording);
    }

    [Fact]
    public async Task A_restart_during_vr_keeps_the_real_pc_devices_in_memory()
    {
        _audio.DefaultPlayback = PicoOut;
        _audio.DefaultRecording = PicoMic;
        _memory.Playback = PcOut;
        _memory.Recording = PcMic;
        _probe.Running = true;

        await Engine().PollAsync();

        Assert.Equal(PcOut, _memory.Playback);
        Assert.Equal(PcMic, _memory.Recording);
    }

    [Fact]
    public async Task Never_remembers_the_headset_as_the_normal_device()
    {
        _audio.DefaultPlayback = PicoOut;
        _audio.DefaultRecording = PicoMic;
        var engine = Engine();
        _probe.Running = true;
        await engine.PollAsync();

        Assert.Null(_memory.Playback);

        _probe.Running = false;
        await PollTimes(engine, 3);

        Assert.Equal(PicoOut, _audio.DefaultPlayback);
        Assert.Contains("Некуда", engine.Status);
    }

    [Fact]
    public async Task Configured_pc_devices_win_over_remembered_ones()
    {
        _options = _options with { PcPlayback = "fifine", PcRecording = "fifine" };
        _audio.Playback.Add(new AudioDevice("other", "Other speakers"));
        _audio.DefaultPlayback = "other";
        var engine = Engine();
        _probe.Running = true;
        await engine.PollAsync();
        _probe.Running = false;
        await PollTimes(engine, 3);

        Assert.Equal(PcOut, _audio.DefaultPlayback);
    }

    [Fact]
    public async Task A_manual_switch_outside_vr_stays_until_undone()
    {
        var engine = Engine();
        await engine.PollAsync();

        await engine.ApplyNowAsync();
        await PollTimes(engine, 5);
        Assert.Equal(PicoOut, _audio.DefaultPlayback);

        await engine.RestoreNowAsync();
        Assert.Equal(PcOut, _audio.DefaultPlayback);
    }

    [Fact]
    public async Task Changing_the_preset_during_vr_moves_to_the_new_headset()
    {
        _audio.Playback.Add(new AudioDevice("meta-out", "Headphones (Oculus Virtual Audio Device)"));
        _audio.Recording.Add(new AudioDevice("meta-mic", "Microphone (Oculus Virtual Audio Device)"));
        var engine = Engine();
        _probe.Running = true;
        await engine.PollAsync();

        _options = _options with { Preset = VrAudioPreset.Meta };
        engine.OnPresetChanged();
        await engine.PollAsync();

        Assert.Equal("meta-out", _audio.DefaultPlayback);
        Assert.Equal(PcOut, _memory.Playback);
    }

    [Fact]
    public async Task Stopping_the_plugin_gives_the_sound_back()
    {
        var engine = Engine();
        _probe.Running = true;
        await engine.PollAsync();

        await engine.ShutdownAsync();

        Assert.Equal(PcOut, _audio.DefaultPlayback);
    }

    [Fact]
    public async Task Stopping_without_a_switch_changes_nothing()
    {
        var engine = Engine();
        await engine.PollAsync();

        await engine.ShutdownAsync();

        Assert.Equal(0, _audio.Changes);
    }

    [Theory]
    [InlineData("Pico", "Speakers (Pico Streaming Speaker)")]
    [InlineData("Pico", "Microphone (PicoStreamingMicrophone)")]
    [InlineData("Meta", "Headphones (Oculus Virtual Audio Device)")]
    public void Presets_find_their_devices(string preset, string deviceName)
    {
        var resolved = VrAudioPreset.Resolve(preset, null, null);
        var device = new AudioDevice("id", deviceName);

        Assert.NotNull(VrAudioPreset.Find([device], resolved.Playback));
    }

    [Fact]
    public void A_custom_preset_uses_the_given_names_and_finds_nothing_when_they_are_empty()
    {
        var custom = VrAudioPreset.Resolve("Custom", "fifine", "");
        var device = new AudioDevice("id", "Speakers (3- fifine Headset)");

        Assert.NotNull(VrAudioPreset.Find([device], custom.Playback));
        Assert.Null(VrAudioPreset.Find([device], custom.Recording));
    }
}
