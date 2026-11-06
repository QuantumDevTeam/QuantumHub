using System.Globalization;
using System.Text.RegularExpressions;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;
using QuantumHub.Core.Plugins;
using QuantumHub.Plugin.Chatbox;
using QuantumHub.VrDashboard;
using Xunit;

namespace QuantumHub.Tests;

/// <summary>Шаблон чатбокса, язык и панель SteamVR. Настоящего SteamVR здесь нет: панель проверяется через раскладку и поддельную шину.</summary>
public sealed class PolishTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 21, 42, 0, TimeSpan.Zero);
    private static readonly ChatModules All = new(true, true, true, true, true);
    private static readonly MediaInfo Song = new("Title", "Artist", "playing", 30, 120);

    private static ChatInputs Inputs(string? process = "Code", string? title = "secret.txt - Code", bool vr = false, MediaInfo? media = null) =>
        new("drinking", process, vr, null, media, Now, title);

    [Theory]
    [InlineData("process", "🖥️ Code")]
    [InlineData("title", "🖥️ secret.txt - Code")]
    [InlineData("both", "🖥️ Code: secret.txt - Code")]
    public void The_window_line_shows_what_was_chosen(string source, string expected)
    {
        var text = ChatboxComposer.Compose(Inputs(), All with { Status = false, Time = false }, new ChatFormat(WindowSource: source));
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Without_a_title_the_window_line_falls_back_to_the_process()
    {
        var text = ChatboxComposer.Compose(Inputs(title: ""), All with { Status = false, Time = false }, new ChatFormat(WindowSource: "title"));
        Assert.Equal("🖥️ Code", text);
    }

    [Fact]
    public void A_template_decides_the_lines_and_their_order()
    {
        var format = new ChatFormat(Template: ["{time} | {status}", "now in {process}", "🎵 {song}"]);
        var text = ChatboxComposer.Compose(Inputs(media: Song), All, format);
        Assert.Equal("21:42 | drinking\nnow in Code\n🎵 Title - Artist", text);
    }

    [Fact]
    public void A_line_with_only_empty_placeholders_disappears()
    {
        var format = new ChatFormat(Template: ["{status}", "🎵 {song} {progress}", "plain text"]);
        Assert.Equal("drinking\nplain text", ChatboxComposer.Compose(Inputs(), All, format));
    }

    [Fact]
    public void An_unknown_placeholder_stays_as_typed()
    {
        var format = new ChatFormat(Template: ["{nope} {status}"]);
        Assert.Equal("{nope} drinking", ChatboxComposer.Compose(Inputs(), All, format));
    }

    [Fact]
    public void A_template_never_goes_over_the_chatbox_limit()
    {
        var format = new ChatFormat(Template: [new string('a', 100), new string('b', 100), new string('c', 300)]);
        var text = ChatboxComposer.Compose(Inputs(), All, format);
        Assert.True(text.Length <= ChatboxComposer.MaxLength, $"{text.Length}");
        Assert.DoesNotContain('b', text);
    }

    [Fact]
    public void A_blank_template_keeps_the_usual_lines()
    {
        var format = new ChatFormat(Template: ["", "  "]);
        Assert.Equal(ChatboxComposer.Compose(Inputs(), All), ChatboxComposer.Compose(Inputs(), All, format));
    }

    [Fact]
    public void Only_the_data_a_template_uses_is_requested()
    {
        var needed = ChatboxComposer.Needed(All, new ChatFormat(Template: ["{title} {cpu}"]));
        Assert.Equal(new ChatModules(false, true, true, false, false), needed);
        Assert.Equal(All, ChatboxComposer.Needed(All, new ChatFormat()));
    }

    [Theory]
    [InlineData("auto", "ru-RU", "ru")]
    [InlineData("auto", "en-US", "en")]
    [InlineData("auto", "de-DE", "en")]
    [InlineData("ru", "en-US", "ru")]
    [InlineData("en", "ru-RU", "en")]
    [InlineData("nonsense", "ru-RU", "ru")]
    public void The_language_follows_the_setting_then_the_system(string setting, string culture, string expected) =>
        Assert.Equal(expected, Loc.Resolve(setting, new CultureInfo(culture)));

    [Fact]
    public void A_translation_is_laid_over_a_copy_of_the_manifest()
    {
        var manifest = new PluginManifest { Id = "a", Name = "Имя", Description = "Описание" };
        manifest.Settings.Add(new SettingDescriptor { Key = "k", Title = "Заголовок", Group = "Группа", Options = { "x" } });
        var locale = new ManifestLocale { Description = "Description", Groups = { { "Группа", "Group" } } };
        locale.Settings["k"] = new SettingLocale { Title = "Title", Options = { { "x", "Label" } } };
        manifest.Locales["en"] = locale;

        var english = ManifestLocalizer.Apply(manifest, "en");
        Assert.Equal("Имя", english.Name);
        Assert.Equal("Description", english.Description);
        Assert.Equal("Title", english.Settings[0].Title);
        Assert.Equal("Group", english.Settings[0].Group);
        Assert.Equal("Label", english.Settings[0].OptionLabels["x"]);

        Assert.Equal("Заголовок", manifest.Settings[0].Title);
        Assert.Same(manifest, ManifestLocalizer.Apply(manifest, "ru"));
    }

    [Fact]
    public void Every_bundled_plugin_is_translated()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "plugins"));
        var manifests = Directory.GetFiles(root, ManifestReader.FileName, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(manifests);

        foreach (var path in manifests)
        {
            var manifest = ManifestReader.Parse(File.ReadAllText(path));
            Assert.True(manifest.Locales.TryGetValue("en", out var english), manifest.Id);
            Assert.NotEmpty(english.Description);
            foreach (var setting in manifest.Settings)
            {
                Assert.True(english.Settings.ContainsKey(setting.Key), $"{manifest.Id}: {setting.Key}");
                if (setting.Group.Length > 0 && Regex.IsMatch(setting.Group, "[а-яА-Я]"))
                    Assert.True(english.Groups.ContainsKey(setting.Group), $"{manifest.Id}: {setting.Group}");
            }
        }
    }

    private static readonly PanelState Full = new(true, true, true, true, false, false, true, false, true, true, true, "a", ["a", "b"], "text");

    [Fact]
    public void The_panel_shows_only_what_is_available_and_wanted()
    {
        var everything = PanelLayout.Build(Full, new PanelOptions());
        Assert.Contains(everything.Tiles, tile => tile.Id == PanelLayout.Sleep);
        Assert.NotNull(everything.Preview);

        var noDiscord = PanelLayout.Build(Full with { HasDiscord = false }, new PanelOptions());
        Assert.DoesNotContain(noDiscord.Tiles, tile => tile.Id == PanelLayout.Mute);

        var hidden = PanelLayout.Build(Full, new PanelOptions(Chatbox: false, Sleep: false));
        Assert.Null(hidden.Preview);
        Assert.DoesNotContain(hidden.Tiles, tile => tile.Id is PanelLayout.Sleep or PanelLayout.SendChat);
    }

    [Fact]
    public void Everything_on_the_panel_fits_inside_it()
    {
        var state = Full with { Statuses = ["1", "2", "3", "4", "5", "6"] };
        foreach (var tile in PanelLayout.Build(state, new PanelOptions()).Tiles)
        {
            Assert.True(tile.Bounds.X >= 0 && tile.Bounds.X + tile.Bounds.Width <= PanelLayout.Width, tile.Id);
            Assert.True(tile.Bounds.Y >= 0 && tile.Bounds.Y + tile.Bounds.Height <= PanelLayout.Height, tile.Id);
        }
    }

    [Fact]
    public void A_tile_that_cannot_work_cannot_be_pressed()
    {
        var view = PanelLayout.Build(Full with { DiscordReady = false }, new PanelOptions());
        var mute = view.Tiles.Single(tile => tile.Id == PanelLayout.Mute);
        Assert.Null(PanelLayout.HitTest(view, mute.Bounds.X + 5, mute.Bounds.Y + 5));

        var audio = view.Tiles.Single(tile => tile.Id == PanelLayout.Audio);
        Assert.Equal(PanelLayout.Audio, PanelLayout.HitTest(view, audio.Bounds.X + 5, audio.Bounds.Y + 5)?.Id);
        Assert.Null(PanelLayout.HitTest(view, 0, 0));
    }

    [Theory]
    [InlineData(PanelLayout.Audio, "cap:vr.session", "audio.target.set", "desktop")]
    [InlineData(PanelLayout.Mute, "cap:discord.client", "voice.mute", "true")]
    [InlineData(PanelLayout.Deafen, "cap:discord.client", "voice.deafen", "true")]
    [InlineData(PanelLayout.Sleep, "cap:vr.pico.sleep", "sleep.set", "true")]
    [InlineData(PanelLayout.Sending, "cap:vrchat.chatbox", "chatbox.sending.set", "false")]
    [InlineData("status.1", "cap:vrchat.chatbox", "chatbox.status.set", "b")]
    public async Task A_press_reaches_the_right_plugin(string tile, string target, string method, string payload)
    {
        var bus = new FakeBus();
        await new PanelClient(bus).PressAsync(tile, Full);

        var call = Assert.Single(bus.Calls);
        Assert.Equal(target, call.Target);
        Assert.Equal(method, call.Method);
        Assert.Contains(payload, call.Payload);
    }

    [Fact]
    public async Task A_missing_plugin_only_hides_its_section()
    {
        var bus = new FakeBus();
        bus.Replies["session.get"] = """{ "onHeadset": true }""";

        var state = await new PanelClient(bus).ReadAsync(new PanelOptions());

        Assert.True(state is { HasAudio: true, OnHeadset: true });
        Assert.False(state.HasDiscord || state.HasSleep || state.HasChatbox);
    }

    /// <summary>Шина, которая запоминает вызовы и отвечает заранее заданным.</summary>
    private sealed class FakeBus : IHubBus
    {
        public List<(string Target, string Method, string Payload)> Calls { get; } = [];
        public Dictionary<string, string> Replies { get; } = [];

        public ValueTask<HubPayload> CallAsync(string target, string method, HubPayload payload = default, TimeSpan? timeout = null, CancellationToken token = default)
        {
            Calls.Add((target, method, payload.AsText()));
            if (method.EndsWith(".get", StringComparison.Ordinal) || method.EndsWith(".state", StringComparison.Ordinal))
            {
                if (!Replies.TryGetValue(method, out var reply)) throw new HubCallException(StatusCode.NotFound, method);
                return ValueTask.FromResult(HubPayload.Text(reply));
            }

            return ValueTask.FromResult(HubPayload.Empty);
        }

        public ValueTask PublishAsync(string topic, HubPayload payload = default, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask<IDisposable> SubscribeAsync(string topic, Action<HubEvent> handler, CancellationToken token = default) => throw new NotSupportedException();
        public IDisposable Handle(string method, HubRequestHandler handler) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<DependencyResolution>> ResolveAsync(IEnumerable<Dependency> dependencies, CancellationToken token = default) => throw new NotSupportedException();
    }
}
