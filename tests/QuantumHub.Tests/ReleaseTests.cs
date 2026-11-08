using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using QuantumHub.Cli;
using QuantumHub.Client;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Links;
using QuantumHub.Contracts.Plugin;
using QuantumHub.Contracts.Proto;
using QuantumHub.Core;
using QuantumHub.Core.Plugins;
using QuantumHub.Gui.Services;
using QuantumHub.ProtoMemProtocol;
using QuantumHub.Setup;
using Xunit;
using static QuantumHub.Tests.TestHub;

namespace QuantumHub.Tests;

/// <summary>Плагин для тестов: отмечает перезапуск зависимости и умеет не запускаться.</summary>
public sealed class RestartProbePlugin : IHubPlugin, IDependencyAware
{
    /// <summary>Появляется, когда зависимость перезапустилась.</summary>
    public const string NoticeFile = "notice.txt";
    /// <summary>Пока этот файл есть, плагин отказывается запускаться.</summary>
    public const string RefuseFile = "refuse.txt";

    private IPluginContext _context = null!;

    /// <inheritdoc/>
    public Task StartAsync(IPluginContext context, CancellationToken token)
    {
        _context = context;
        if (File.Exists(Path.Combine(context.Paths.DataDir, RefuseFile))) throw new InvalidOperationException("refused");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken token) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task OnDependencyRestartedAsync(string providerId, CancellationToken token) =>
        File.WriteAllTextAsync(Path.Combine(_context.Paths.DataDir, NoticeFile), providerId, token);
}

/// <summary>То, что появилось к версии 1.0.0.</summary>
public class ReleaseTests
{
    private const string Dependent = "needs.uuid";
    private const string Probe = "restart.probe";

    private static async Task<PluginInfo> InfoAsync(IHubBus bus, string id)
    {
        var list = await bus.CallAsync<PluginList>("hub", "hub.plugins.list", new PluginRef());
        return list.Plugins.Single(plugin => plugin.Manifest.Id == id);
    }

    private static ValueTask<HubPayload> Restart(IHubBus bus, string id) =>
        bus.CallAsync("hub", "hub.plugins.restart", HubPayload.Proto(new PluginRef { Id = id }));

    /// <summary>Кладёт плагин из сборки тестов.</summary>
    private static void AddProbe(TestHub hub, string id, string extra = "")
    {
        var dir = Directory.CreateDirectory(Path.Combine(hub.PluginsDir, id)).FullName;
        File.Copy(typeof(RestartProbePlugin).Assembly.Location, Path.Combine(dir, "QuantumHub.Tests.dll"));
        File.WriteAllText(Path.Combine(dir, "plugin.jsonc"), $$"""
            { "id": "{{id}}", "version": "1.0.0",
              "service": { "assembly": "QuantumHub.Tests.dll", "type": "{{typeof(RestartProbePlugin).FullName}}" }{{extra}} }
            """);
    }

    private static async Task<TestHub> HubWithDependentAsync()
    {
        var hub = await StartAsync(0, Uuid);
        await hub.ShutdownAsync();
        AddProbe(hub, Dependent, $$""", "dependencies": [ { "id": "{{Uuid}}" } ]""");
        await hub.BootAsync();
        return hub;
    }

    [Fact]
    public async Task Restarting_a_dependency_leaves_its_dependents_running()
    {
        await using var hub = await HubWithDependentAsync();
        await using var client = await hub.ConnectAsync();

        var states = new List<PluginState>();
        await client.SubscribeAsync("hub.plugin.state", e =>
        {
            var info = e.Payload.AsProto<PluginInfo>();
            if (info.Manifest.Id == Dependent) lock (states) states.Add(info.State);
        });

        await Restart(client, Uuid);

        var dependent = await InfoAsync(client, Dependent);
        Assert.Equal(PluginState.Running, dependent.State);
        Assert.Empty(dependent.Error);
        lock (states) Assert.Empty(states);
    }

    [Fact]
    public async Task A_dependent_hears_about_the_restart_and_can_restore_what_it_asked_for()
    {
        await using var hub = await HubWithDependentAsync();
        await using var client = await hub.ConnectAsync();

        await Restart(client, Uuid);

        Assert.Equal(Uuid, File.ReadAllText(Path.Combine(hub.DataDir, "data", Dependent, RestartProbePlugin.NoticeFile)));
    }

    [Fact]
    public async Task Calls_made_during_a_restart_wait_for_it()
    {
        await using var hub = await StartAsync(0, Uuid);
        await using var client = await hub.ConnectAsync();
        await using var caller = await hub.ConnectAsync("caller");

        using var stop = new CancellationTokenSource();
        var calls = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested) await caller.CallAsync(Uuid, "value.latest");
        });

        for (var i = 0; i < 5; i++) await Restart(client, Uuid);
        await stop.CancelAsync();
        await calls;
    }

    [Fact]
    public async Task A_dependency_that_does_not_come_back_blocks_its_dependents_and_they_return_with_it()
    {
        await using var hub = await StartAsync(0);
        await hub.ShutdownAsync();
        AddProbe(hub, Probe);
        AddProbe(hub, Dependent, $$""", "dependencies": [ { "id": "{{Probe}}" } ]""");
        await hub.BootAsync();
        await using var client = await hub.ConnectAsync();

        var refuse = Path.Combine(hub.DataDir, "data", Probe, RestartProbePlugin.RefuseFile);
        File.WriteAllText(refuse, string.Empty);
        await Assert.ThrowsAsync<HubCallException>(async () => await Restart(client, Probe));
        Assert.Equal(PluginState.Blocked, (await InfoAsync(client, Dependent)).State);

        File.Delete(refuse);
        await Restart(client, Probe);
        var dependent = await InfoAsync(client, Dependent);
        Assert.Equal(PluginState.Running, dependent.State);
        Assert.Empty(dependent.Error);
    }

    [Fact]
    public async Task A_plugin_returning_after_its_dependency_was_off_shows_no_old_error()
    {
        await using var hub = await HubWithDependentAsync();
        await using var client = await hub.ConnectAsync();

        await client.CallAsync("hub", "hub.plugins.enable", HubPayload.Proto(new SetEnabledRequest { Id = Uuid, Enabled = false }));
        Assert.NotEmpty((await InfoAsync(client, Dependent)).Error);

        await client.CallAsync("hub", "hub.plugins.enable", HubPayload.Proto(new SetEnabledRequest { Id = Uuid, Enabled = true }));
        Assert.Empty((await InfoAsync(client, Dependent)).Error);
    }

    [Theory]
    [InlineData("quantumhub://", HubLinkKind.Open)]
    [InlineData("quantumhub://open", HubLinkKind.Open)]
    [InlineData("QUANTUMHUB://settings/", HubLinkKind.Page)]
    [InlineData("quantumhub://plugin/qh.discord", HubLinkKind.Plugin)]
    [InlineData("quantumhub://run/qh.discord/mute?state=on#x", HubLinkKind.Run)]
    public void Good_links_are_understood(string text, HubLinkKind kind)
    {
        Assert.True(HubLink.TryParse(text, out var link));
        Assert.Equal(kind, link.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://example.com")]
    [InlineData("quantumhub://nothing")]
    [InlineData("quantumhub://plugin/../../evil")]
    [InlineData("quantumhub://plugin/a/b")]
    [InlineData("quantumhub://run/qh.discord")]
    [InlineData("quantumhub://run/qh.discord/mute/extra")]
    [InlineData("quantumhub://run/qh%2Fdiscord/mute")]
    public void Anything_else_is_refused(string? text) => Assert.False(HubLink.TryParse(text, out _));

    [Fact]
    public void A_built_link_parses_back_with_its_parameters()
    {
        var text = HubLink.Run("qh.sample.uuid", "submit", new Dictionary<string, string> { ["value"] = "a b&c=d" });

        Assert.True(HubLink.TryParse(text, out var link));
        Assert.Equal("a b&c=d", link.Params["value"]);
    }

    [Fact]
    public async Task A_link_runs_only_what_the_manifest_declares()
    {
        await using var hub = await StartAsync(0, Uuid);
        await using var client = await hub.ConnectAsync();

        var run = new LinkRunRequest { PluginId = Uuid, Action = "submit", Params = { ["value"] = "from a link" } };
        await client.CallAsync("hub", "hub.links.run", HubPayload.Proto(run));
        Assert.Equal("from a link", (await client.CallAsync(Uuid, "value.latest")).AsText());

        var method = new LinkRunRequest { PluginId = Uuid, Action = "value.submit" };
        var error = await Assert.ThrowsAsync<HubCallException>(async () => await client.CallAsync("hub", "hub.links.run", HubPayload.Proto(method)));
        Assert.Equal(StatusCode.NotFound, error.Status);
    }

    [Fact]
    public void The_manifest_refuses_bad_link_actions_and_hub_ranges()
    {
        Assert.Throws<InvalidDataException>(() => ManifestReader.Parse("""{ "id": "a", "version": "1.0.0", "links": [ { "action": "../x" } ] }"""));
        Assert.Throws<InvalidDataException>(() => ManifestReader.Parse("""{ "id": "a", "version": "1.0.0", "links": [ { "action": "x" }, { "action": "x" } ] }"""));
        Assert.Throws<InvalidDataException>(() => ManifestReader.Parse("""{ "id": "a", "version": "1.0.0", "hub": "nonsense" }"""));
    }

    [Fact]
    public async Task A_plugin_for_another_hub_is_shown_but_not_started()
    {
        await using var hub = await StartAsync(0);
        await hub.ShutdownAsync();
        AddProbe(hub, Probe, """, "hub": ">=99.0.0" """);
        await hub.BootAsync();
        await using var client = await hub.ConnectAsync();

        var info = await InfoAsync(client, Probe);
        Assert.Equal(PluginState.Faulted, info.State);
        Assert.Contains(">=99.0.0", info.Error);
    }

    [Fact]
    public async Task An_update_for_another_hub_leaves_the_installed_version_running()
    {
        await using var hub = await StartAsync(0, Uuid);
        await using var client = await hub.ConnectAsync();

        var update = Path.Combine(hub.Root, "update");
        CopyPlugin(Uuid, update);
        var manifest = Path.Combine(update, "plugin.jsonc");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(">=1.0.0 <2.0.0", ">=99.0.0"));

        await Assert.ThrowsAsync<HubCallException>(async () =>
            await client.CallAsync("hub", "hub.plugins.install", HubPayload.Proto(new InstallRequest { Path = update })));
        Assert.Equal(PluginState.Running, (await InfoAsync(client, Uuid)).State);
    }

    [Theory]
    [InlineData("hub")]
    [InlineData("cap:user.activity")]
    [InlineData("session:1")]
    public async Task A_client_cannot_take_a_reserved_address(string id)
    {
        await using var hub = await StartAsync(0);
        await Assert.ThrowsAsync<HubRejectedException>(() => hub.ConnectAsync(id));
    }

    [Fact]
    public async Task A_config_owner_cannot_walk_out_of_the_config_folder()
    {
        await using var hub = await StartAsync(0);
        await using var client = await hub.ConnectAsync();

        await Assert.ThrowsAsync<HubCallException>(async () => await client.CallAsync("hub", "hub.config.set",
            HubPayload.Proto(new ConfigSetRequest { PluginId = @"..\..\evil", Key = "a", Json = "1" })));
    }

    [Fact]
    public void The_log_starts_a_new_file_each_day_and_drops_old_ones()
    {
        var dir = Directory.CreateTempSubdirectory("qh-log").FullName;
        try
        {
            var old = Path.Combine(dir, "hub-2000-01-01.log");
            File.WriteAllText(old, "old");
            File.SetLastWriteTime(old, new DateTime(2000, 1, 1));

            var log = new HubLog(dir);
            log.Prune(0);
            log.Info("today");
            Assert.True(File.Exists(old));

            var tomorrow = DateTime.Now.AddDays(1);
            log.Prune(30);
            File.WriteAllText(old, "old");
            File.SetLastWriteTime(old, new DateTime(2000, 1, 1));
            log.Clock = () => tomorrow;
            log.Info("tomorrow");
            log.Dispose();

            Assert.Contains("tomorrow", File.ReadAllText(Path.Combine(dir, $"hub-{tomorrow:yyyy-MM-dd}.log")));
            Assert.False(File.Exists(old));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task The_report_has_logs_and_settings_but_no_secrets()
    {
        await using var hub = await StartAsync(0);
        await hub.ShutdownAsync();
        AddProbe(hub, Probe, """, "settings": [ { "key": "visible", "type": "string", "default": "shown-value" }, { "key": "password", "type": "secret" } ] """);
        await hub.BootAsync();
        hub.Hub.Config.Set(Probe, "password", "secret-value");
        hub.Hub.Config.Set(Probe, "undeclaredToken", "secret-value");
        await using var client = await hub.ConnectAsync();

        var report = await client.CallAsync<DiagnosticsResponse>("hub", "hub.diagnostics.export", new DiagnosticsRequest());

        using var zip = ZipFile.OpenRead(report.Path);
        Assert.Contains(zip.Entries, entry => entry.FullName.StartsWith("logs/hub-", StringComparison.Ordinal));
        Assert.Contains(zip.Entries, entry => entry.FullName == "plugins.txt");
        var text = string.Concat(zip.Entries.Select(entry =>
        {
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }));
        Assert.Contains("shown-value", text);
        Assert.DoesNotContain("secret-value", text);
    }

    [Fact]
    public void Crashes_are_retried_slower_and_slower_and_then_given_up()
    {
        var policy = new CrashPolicy();
        var now = DateTime.UtcNow;

        var delays = Enumerable.Range(0, CrashPolicy.MaxCrashes).Select(_ => policy.NoteCrash(now)).ToList();
        Assert.Equal(TimeSpan.FromSeconds(1), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(16), delays[^1]);
        Assert.Null(policy.NoteCrash(now));
        Assert.Equal(TimeSpan.FromSeconds(1), policy.NoteCrash(now + CrashPolicy.Window + TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("", @"C:\q\cli", @"C:\q\cli")]
    [InlineData(@"C:\a;", @"C:\q\cli", @"C:\a;C:\q\cli")]
    [InlineData(@"C:\a;c:\Q\CLI\", @"C:\q\cli", @"C:\a;c:\Q\CLI\")]
    public void The_program_folder_is_added_to_path_once(string path, string folder, string expected) =>
        Assert.Equal(expected, PathList.Add(path, folder));

    [Theory]
    [InlineData(@"%USERPROFILE%\bin;C:\q\cli;C:\b", @"%USERPROFILE%\bin;C:\b")]
    [InlineData(@"C:\a", @"C:\a")]
    [InlineData(@"C:\q\cli", "")]
    public void Only_the_program_folder_leaves_path(string path, string expected) =>
        Assert.Equal(expected, PathList.Remove(path, @"C:\q\cli"));

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("0.9.9", "1.0.0", false)]
    [InlineData("1.1.0-beta", "1.0.0", false)]
    public void Only_a_newer_release_counts(string candidate, string current, bool expected) =>
        Assert.Equal(expected, UpdateChecker.IsNewer(candidate, current));

    [Fact]
    public void Update_answers_from_elsewhere_or_previews_are_ignored()
    {
        const string good = """{ "tag_name": "v1.2.0", "html_url": "https://github.com/QuantumDevTeam/QuantumHub/releases/tag/v1.2.0" }""";
        Assert.Equal("1.2.0", UpdateChecker.Parse(good, "1.0.0")?.Version);
        Assert.Null(UpdateChecker.Parse("""{ "tag_name": "v1.2.0", "html_url": "https://evil.example/x" }""", "1.0.0"));
        Assert.Null(UpdateChecker.Parse("""{ "tag_name": "v1.2.0", "prerelease": true, "html_url": "https://github.com/QuantumDevTeam/QuantumHub/releases/tag/v1.2.0" }""", "1.0.0"));
    }

    [Fact]
    public void Old_settings_files_get_the_new_defaults()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, """{ "Theme": "dark" }""");
            var settings = GuiSettings.Load(file);
            Assert.True(settings.CheckUpdates);
            Assert.True(settings.LinkActions);
            Assert.Empty(settings.TrustedLinks);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static async Task<(int Code, string Output, string Error)> CliAsync(TestHub? hub, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var all = hub is null ? args : [.. args, "--instance", hub.Endpoint.Instance];
        var code = await new CliApp(output, error).RunAsync(all);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task The_cli_says_when_the_hub_is_not_running()
    {
        var (code, _, error) = await CliAsync(null, "status", "--instance", Guid.NewGuid().ToString("N"));

        Assert.Equal(CliApp.NoHub, code);
        Assert.Contains("quantumhub start", error);
    }

    [Fact]
    public async Task The_cli_refuses_unknown_commands()
    {
        Assert.Equal(CliApp.Usage, (await CliAsync(null, "dance")).Code);
        Assert.Equal(CliApp.Usage, (await CliAsync(null, "link", "https://example.com")).Code);
    }

    [Fact]
    public async Task The_cli_controls_the_hub()
    {
        await using var hub = await StartAsync(0, Uuid);

        Assert.Contains(Hub.Version, (await CliAsync(hub, "status")).Output);
        Assert.Contains("\"version\"", (await CliAsync(hub, "status", "--json")).Output);
        Assert.Contains(Uuid, (await CliAsync(hub, "plugins")).Output);

        Assert.Equal(CliApp.Ok, (await CliAsync(hub, "config", "set", Uuid, "historySize", "7")).Code);
        Assert.Equal("7", (await CliAsync(hub, "config", "get", Uuid, "historySize")).Output.Trim());
        Assert.Equal(CliApp.Failed, (await CliAsync(hub, "config", "get", Uuid, "missing")).Code);

        Assert.Equal(CliApp.Ok, (await CliAsync(hub, "call", Uuid, "value.submit", """{ "kind": "uuid", "value": "cli" }""")).Code);
        Assert.Equal("cli", (await CliAsync(hub, "call", Uuid, "value.latest")).Output.Trim());
        Assert.Equal(CliApp.Ok, (await CliAsync(hub, "link", HubLink.Run(Uuid, "submit", new Dictionary<string, string> { ["value"] = "linked" }))).Code);
        Assert.Equal("linked", (await CliAsync(hub, "call", Uuid, "value.latest")).Output.Trim());

        Assert.Equal(CliApp.Ok, (await CliAsync(hub, "plugins", "disable", Uuid)).Code);
        Assert.Contains("Disabled", (await CliAsync(hub, "plugins")).Output);
        Assert.Equal(CliApp.Ok, (await CliAsync(hub, "plugins", "enable", Uuid)).Code);
        Assert.Equal(CliApp.Failed, (await CliAsync(hub, "plugins", "restart", "no.such.plugin")).Code);

        Assert.Contains("[hub]", (await CliAsync(hub, "logs", "-n", "5")).Output);
        Assert.EndsWith(".zip", (await CliAsync(hub, "report")).Output.Trim());
    }

    [Theory]
    [InlineData("7", "7")]
    [InlineData("true", "true")]
    [InlineData("just text", "\"just text\"")]
    public void Cli_values_need_no_quotes(string text, string json) => Assert.Equal(json, CliApp.AsJsonValue(text));

    /// <summary>Настоящий exe хаба без плагинов: в своих папках и на своём pipe.</summary>
    private sealed class RealHub : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("qh-real").FullName;

        public RealHub(bool watch)
        {
            var repo = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(repo, "QuantumHub.sln"))) repo = Path.GetDirectoryName(repo)!;
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;

            var start = new ProcessStartInfo(Path.Combine(repo, "src", "QuantumHub.Service", "bin", configuration, "net10.0-windows", "QuantumHub.Service.exe")) { UseShellExecute = false };
            foreach (var argument in new[] { "--instance", Endpoint.Instance, "--root", Path.Combine(_root, "app"), "--data", Path.Combine(_root, "data") }) start.ArgumentList.Add(argument);
            if (watch) start.ArgumentList.Add("--watch");
            Process = Process.Start(start)!;
        }

        public HubEndpoint Endpoint { get; } = new(Guid.NewGuid().ToString("N"));
        public Process Process { get; }

        public async Task<HubClient> ConnectAsync()
        {
            for (var i = 0; ; i++)
            {
                try
                {
                    return await HubClient.ConnectAsync(new HubClientOptions { ClientId = "test.real", Endpoint = Endpoint, ConnectTimeout = TimeSpan.FromMilliseconds(500) });
                }
                catch (Exception ex) when (i < 60 && ex is TimeoutException or IOException)
                {
                    await Task.Delay(250);
                }
            }
        }

        public void Dispose()
        {
            if (!Process.HasExited) Process.Kill(true);
            Process.Dispose();
            for (var i = 0; i < 20; i++)
            {
                try
                {
                    Directory.Delete(_root, true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    [Fact]
    public async Task The_watchdog_brings_a_killed_hub_back_and_lets_a_stopped_one_go()
    {
        using var real = new RealHub(true);
        int first;
        await using (var client = await real.ConnectAsync())
            first = (int)(await client.CallAsync<HubStatus>("hub", "hub.status", new PluginRef())).ProcessId;

        Process.GetProcessById(first).Kill();

        await Task.Delay(500);
        await using var again = await real.ConnectAsync();
        var second = await again.CallAsync<HubStatus>("hub", "hub.status", new PluginRef());
        Assert.NotEqual(first, (int)second.ProcessId);

        await again.CallAsync("hub", "hub.shutdown");
        await real.Process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
        Assert.Equal(0, real.Process.ExitCode);
    }

    [Fact]
    public async Task The_hub_shuts_down_properly_when_windows_ends_the_session()
    {
        using var real = new RealHub(false);
        await using var client = await real.ConnectAsync();

        var window = FindWindowW($"QuantumHub.Service.{real.Endpoint.Instance}", null);
        Assert.NotEqual(0, window);
        Assert.Equal(1, SendMessageW(window, 0x0011, 0, 0));
        SendMessageW(window, 0x0016, 1, 0);

        await real.Process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
        Assert.Equal(0, real.Process.ExitCode);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string className, string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
}
