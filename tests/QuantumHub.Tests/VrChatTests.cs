using System.Text;
using QuantumHub.Plugin.Chatbox;
using QuantumHub.Plugin.VrChatOsc;
using Xunit;

namespace QuantumHub.Tests;

/// <summary>Логика чатбокса. Отправка подменена, в настоящий VRChat здесь ничего не уходит.</summary>
public class VrChatTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 21, 40, 0, TimeSpan.Zero);
    private static readonly ChatModules All = new(true, true, true, true, true);
    private static readonly MediaInfo Song = new("care less", "LANY", "playing", 81, 198);
    private static readonly StatsInfo Stats = new(12, 9.1, 16, 40, 5.2, 8);

    [Fact]
    public void Encodes_the_chatbox_message_as_valid_osc()
    {
        var packet = OscMessage.Encode("/chatbox/input", "hi", true, false);

        var expected = new List<byte>();
        expected.AddRange(Encoding.ASCII.GetBytes("/chatbox/input\0\0"));
        expected.AddRange(Encoding.ASCII.GetBytes(",sTF\0\0\0\0"));
        expected.AddRange(Encoding.ASCII.GetBytes("hi\0\0"));
        Assert.Equal(expected, packet);
    }

    [Fact]
    public void A_string_filling_four_bytes_still_gets_its_terminator()
    {
        var packet = OscMessage.Encode("/abc", "abcd");

        // Адрес 8 байт, типы 4, строка из четырёх байт с завершающим нулём 8.
        Assert.Equal(20, packet.Length);
    }

    [Fact]
    public void Numbers_are_big_endian()
    {
        var packet = OscMessage.Encode("/n", 1);

        Assert.Equal([0, 0, 0, 1], packet[^4..]);
    }

    [Fact]
    public void Non_ascii_text_is_measured_in_bytes()
    {
        Assert.Equal(0, OscMessage.Encode("/t", "привет").Length % 4);
    }

    [Fact]
    public void Decodes_what_it_encodes()
    {
        var decoded = OscMessage.Decode(OscMessage.Encode("/avatar/parameters/привет", "текст", 7, 0.5f, true, false));

        Assert.NotNull(decoded);
        Assert.Equal("/avatar/parameters/привет", decoded.Value.Address);
        Assert.Equal(["текст", 7, 0.5f, true, false], decoded.Value.Arguments);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x2F, 0x61 })]
    [InlineData(new byte[] { 0x78, 0, 0, 0, 0x2C, 0, 0, 0 })]
    public void Garbage_is_not_a_message(byte[] packet) => Assert.Null(OscMessage.Decode(packet));

    [Fact]
    public void A_truncated_argument_is_not_a_message()
    {
        var packet = OscMessage.Encode("/n", 1);

        Assert.Null(OscMessage.Decode(packet[..^2]));
    }

    [Fact]
    public void Json_numbers_keep_their_kind_and_float_can_be_forced()
    {
        var values = System.Text.Json.JsonDocument.Parse("""[1, 0.5, "a", true, { "float": 1 }]""").RootElement.EnumerateArray().ToArray();

        Assert.Equal([1, 0.5f, "a", true, 1f], values.Select(OscJson.ToArgument));
    }

    [Fact]
    public void Formatting_settings_are_applied()
    {
        var format = new ChatFormat("в шлеме", "hh:mm tt", false, Cpu: true, Gpu: false, Ram: false, Vram: false);
        var text = ChatboxComposer.Compose(new ChatInputs(null, null, true, Stats, Song, Now), All, format);

        Assert.Contains("в шлеме", text);
        Assert.Contains("09:40 PM", text);
        Assert.Contains("CPU 12%", text);
        Assert.DoesNotContain("GPU", text);
        Assert.DoesNotContain("●", text);
    }

    [Fact]
    public void A_bad_time_format_does_not_break_the_chatbox()
    {
        var text = ChatboxComposer.Compose(new ChatInputs(null, null, false, null, null, Now), All, new ChatFormat(TimeFormat: "%"));

        Assert.Equal("21:40", text);
    }

    [Fact]
    public void Composes_lines_in_order()
    {
        var text = ChatboxComposer.Compose(new ChatInputs("drinking", "Code", false, null, Song, Now), All with { Component = false });

        Assert.Equal("💬 drinking\n🖥️ Code\n21:40\n1:21 ━━━●───── 3:18\n🎵 'care less' ᵇʸ LANY", text);
    }

    [Fact]
    public void Shows_vr_instead_of_the_window_in_vr()
    {
        var text = ChatboxComposer.Compose(new ChatInputs(null, "Code", true, null, null, Now), All);

        Assert.Contains("In VR", text);
        Assert.DoesNotContain("Code", text);
    }

    [Fact]
    public void A_paused_track_says_so()
    {
        var text = ChatboxComposer.Compose(new ChatInputs(null, null, false, null, Song with { State = "paused" }, Now), All);

        Assert.Contains("⏸", text);
    }

    [Fact]
    public void A_stopped_player_shows_no_music() =>
        Assert.DoesNotContain("care less", ChatboxComposer.Compose(new ChatInputs(null, null, false, null, Song with { State = "stopped" }, Now), All));

    [Fact]
    public void Missing_plugins_just_lose_their_lines()
    {
        var text = ChatboxComposer.Compose(new ChatInputs(null, null, false, null, null, Now), All);

        Assert.Equal("21:40", text);
    }

    [Fact]
    public void Switched_off_modules_stay_out()
    {
        var text = ChatboxComposer.Compose(new ChatInputs("status", "Code", false, Stats, Song, Now), new ChatModules(false, false, false, false, false));

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void Stats_use_a_dot_whatever_the_system_culture()
    {
        var text = ChatboxComposer.Compose(new ChatInputs(null, null, false, Stats, null, Now), All with { Time = false });

        Assert.Equal("CPU 12% GPU 40% RAM 9.1/16G VRAM 5.2/8G", text);
    }

    [Fact]
    public void Stays_within_the_limit_dropping_the_least_important_first()
    {
        var status = new string('s', 90);
        var text = ChatboxComposer.Compose(new ChatInputs(status, "Code", false, Stats, Song, Now), All);

        Assert.True(text.Length <= ChatboxComposer.MaxLength, $"{text.Length}");
        Assert.Contains(status, text);
        Assert.Contains("care less", text);
        Assert.DoesNotContain("CPU", text);
    }

    [Fact]
    public void A_giant_status_is_cut()
    {
        var text = ChatboxComposer.Compose(new ChatInputs(new string('s', 500), null, false, null, null, Now), All);

        Assert.Equal(ChatboxComposer.MaxLength, text.Length);
        Assert.EndsWith("…", text);
    }

    private sealed class FakeData : IChatData
    {
        public DateTimeOffset Time { get; set; } = Now;
        public string? Window { get; set; } = "Code";

        public Task<ChatInputs> ReadAsync(string status, ChatModules modules) =>
            Task.FromResult(new ChatInputs(status, Window, false, null, null, Time));
    }

    private sealed class FakeSink : IChatSink
    {
        public List<(string Text, bool Notify)> Sent { get; } = [];

        public Task SendAsync(string text, bool notify)
        {
            Sent.Add((text, notify));
            return Task.CompletedTask;
        }
    }

    private readonly FakeData _data = new();
    private readonly FakeSink _sink = new();
    private ChatboxOptions _options = new(true, "status", All, TimeSpan.FromSeconds(8));

    private ChatboxEngine Engine(bool running = true) => new(_data, _sink, () => _options) { GameRunning = running };

    [Fact]
    public async Task Sends_nothing_while_the_game_is_closed_but_keeps_the_preview()
    {
        var engine = Engine(running: false);

        await engine.TickAsync();

        Assert.Empty(_sink.Sent);
        Assert.Contains("status", engine.Preview);
    }

    [Fact]
    public async Task Sends_nothing_when_sending_is_off()
    {
        _options = _options with { Sending = false };

        await Engine().TickAsync();

        Assert.Empty(_sink.Sent);
    }

    [Fact]
    public async Task Sends_only_when_something_changed()
    {
        var engine = Engine();
        await engine.TickAsync();
        await engine.TickAsync();
        Assert.Single(_sink.Sent);

        _data.Window = "Rider";
        await engine.TickAsync();
        Assert.Equal(2, _sink.Sent.Count);
    }

    [Fact]
    public async Task Repeats_unchanged_text_before_the_chatbox_fades()
    {
        var engine = Engine();
        _options = _options with { Modules = All with { Time = false } };
        await engine.TickAsync();

        _data.Time = Now.AddSeconds(21);
        await engine.TickAsync();

        Assert.Equal(2, _sink.Sent.Count);
    }

    [Fact]
    public async Task A_message_holds_the_chatbox_then_the_lines_return()
    {
        var engine = Engine();

        Assert.True(await engine.SendMessageAsync("hello", Now));
        await engine.TickAsync();
        Assert.Equal(("hello", true), _sink.Sent.Single());

        _data.Time = Now.AddSeconds(9);
        await engine.TickAsync();
        Assert.Equal(2, _sink.Sent.Count);
    }

    [Fact]
    public async Task Messages_need_a_running_game_and_text()
    {
        Assert.False(await Engine(running: false).SendMessageAsync("hello", Now));
        Assert.False(await Engine().SendMessageAsync("   ", Now));
        Assert.Empty(_sink.Sent);
    }

    [Fact]
    public async Task A_long_message_is_cut_to_the_limit()
    {
        await Engine().SendMessageAsync(new string('x', 300), Now);

        Assert.Equal(ChatboxComposer.MaxLength, _sink.Sent[0].Text.Length);
    }
}
