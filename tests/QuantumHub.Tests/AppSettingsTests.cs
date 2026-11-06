using QuantumHub.Core;
using QuantumHub.Gui.Services;
using Xunit;

namespace QuantumHub.Tests;

/// <summary>Настройки окна и журнал хаба на временных папках.</summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("qh-settings-").FullName;

    private string File1 => Path.Combine(_root, "gui.json");

    [Fact]
    public void Settings_survive_a_restart()
    {
        new GuiSettings { Theme = "dark", LogRows = 700, AlwaysOnTop = true }.Save(File1);

        var loaded = GuiSettings.Load(File1);

        Assert.Equal("dark", loaded.Theme);
        Assert.Equal(700, loaded.LogRows);
        Assert.True(loaded.AlwaysOnTop);
    }

    [Fact]
    public void A_missing_or_broken_file_means_defaults()
    {
        Assert.Equal(new GuiSettings(), GuiSettings.Load(File1));

        File.WriteAllText(File1, "{ not json");
        Assert.Equal(new GuiSettings(), GuiSettings.Load(File1));
    }

    [Fact]
    public void A_file_from_an_older_version_keeps_new_defaults()
    {
        File.WriteAllText(File1, """{ "Theme": "light" }""");

        var loaded = GuiSettings.Load(File1);

        Assert.Equal("light", loaded.Theme);
        Assert.True(loaded.ConfirmRemove);
        Assert.Equal(500, loaded.LogRows);
    }

    [Fact]
    public void Impossible_values_are_corrected()
    {
        File.WriteAllText(File1, """{ "Theme": "pink", "LogRows": 5, "LogFont": 99, "CloseAction": null }""");

        var loaded = GuiSettings.Load(File1);

        Assert.Equal(GuiSettings.System, loaded.Theme);
        Assert.Equal(GuiSettings.MinLogRows, loaded.LogRows);
        Assert.Equal(GuiSettings.MaxLogFont, loaded.LogFont);
        Assert.Equal("tray", loaded.CloseAction);
    }

    [Fact]
    public void The_start_page_can_be_the_last_one()
    {
        Assert.Equal("logs", new GuiSettings { StartPage = "logs", LastPage = "plugin:x" }.InitialPage);
        Assert.Equal("plugin:x", new GuiSettings { StartPage = GuiSettings.LastPageValue, LastPage = "plugin:x" }.InitialPage);
    }

    [Theory]
    [InlineData("open", false, true)]
    [InlineData("closed", true, false)]
    [InlineData(GuiSettings.Remember, false, false)]
    [InlineData(GuiSettings.Remember, true, true)]
    public void The_menu_opens_as_chosen(string mode, bool wasOpen, bool expected) =>
        Assert.Equal(expected, new GuiSettings { PaneMode = mode, PaneWasOpen = wasOpen }.InitialPaneOpen);

    [Theory]
    [InlineData("debug", "debug", true)]
    [InlineData("info", "debug", false)]
    [InlineData("warning", "info", false)]
    [InlineData("warning", "error", true)]
    public void The_log_shows_entries_from_the_chosen_level(string filter, string level, bool shown) =>
        Assert.Equal(shown, new GuiSettings { LogFilter = filter }.ShowsLevel(level));

    [Fact]
    public void Old_log_files_go_and_todays_file_stays()
    {
        var old = Path.Combine(_root, "hub-2020-01-01.log");
        var recent = Path.Combine(_root, "hub-2026-01-01.log");
        var other = Path.Combine(_root, "crash.log");
        foreach (var file in new[] { old, recent, other }) File.WriteAllText(file, "x");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-40));
        File.SetLastWriteTime(recent, DateTime.Now.AddDays(-5));
        File.SetLastWriteTime(other, DateTime.Now.AddDays(-40));

        using var log = new HubLog(_root);
        Assert.Equal(0, log.Prune(0));
        Assert.True(File.Exists(old));

        Assert.Equal(1, log.Prune(30));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));
        Assert.Single(Directory.GetFiles(_root, $"hub-{DateTime.Now:yyyy-MM-dd}.log"));
    }

    [Fact]
    public void Entries_below_the_level_are_not_kept()
    {
        using var log = new HubLog(_root) { MinLevel = "warning" };

        log.Info("quiet");
        log.Warning("loud");

        Assert.Equal("loud", Assert.Single(log.Recent()).Message);
    }

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(_root, true);
}
