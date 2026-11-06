using QuantumHub.Client;
using QuantumHub.Contracts.Proto;
using QuantumHub.Core;
using QuantumHub.ProtoMemProtocol;

namespace QuantumHub.Tests;

/// <summary>Настоящий хаб с настоящими плагинами, в своих временных папках и на своём pipe.</summary>
public sealed class TestHub : IAsyncDisposable
{
    /// <summary>Id плагина UUID Backend.</summary>
    public const string Uuid = "qh.sample.uuid";
    /// <summary>Id плагина System Specs.</summary>
    public const string Specs = "qh.system";
    /// <summary>Id плагина User Activity.</summary>
    public const string Window = "qh.activity";

    /// <summary>Id плагина Audio Devices.</summary>
    public const string Audio = "qh.audio";

    /// <summary>Id плагина Mic Switcher.</summary>
    public const string MicSwitcher = "qh.audio.micswitch";

    /// <summary>Id плагина VR Switcher.</summary>
    public const string VrSwitcher = "qh.vr.switcher";

    /// <summary>Id плагина Pico Sleep Mode.</summary>
    public const string AntiSleep = "qh.vr.pico.antisleep";

    /// <summary>Id плагина Notifications.</summary>
    public const string Notify = "qh.ui.notify";

    /// <summary>Id плагина SteamVR Dashboard.</summary>
    public const string VrDashboard = "qh.vr.dashboard";

    /// <summary>Id плагина Discord.</summary>
    public const string Discord = "qh.discord";

    /// <summary>Id плагина Music.</summary>
    public const string Music = "qh.music";

    /// <summary>Id плагина VRChat OSC.</summary>
    public const string Osc = "qh.vrchat.osc";

    /// <summary>Id плагина VRChat Chatbox.</summary>
    public const string Chatbox = "qh.vrchat.chatbox";

    private static readonly Dictionary<string, string> Projects = new()
    {
        [Uuid] = "QuantumHub.Plugin.UuidBackend",
        [Specs] = "QuantumHub.Plugin.SystemSpecs",
        [Window] = "QuantumHub.Plugin.Activity",
        [Audio] = "QuantumHub.Plugin.AudioDevices",
        [MicSwitcher] = "QuantumHub.Plugin.MicSwitcher",
        [VrSwitcher] = "QuantumHub.Plugin.VrSwitcher",
        [AntiSleep] = "QuantumHub.Plugin.PicoAntiSleep",
        [Notify] = "QuantumHub.Plugin.Notifications",
        [Music] = "QuantumHub.Plugin.Music",
        [Discord] = "QuantumHub.Plugin.Discord",
        [VrDashboard] = "QuantumHub.Plugin.VrDashboard",
        [Osc] = "QuantumHub.Plugin.VrChatOsc",
        [Chatbox] = "QuantumHub.Plugin.Chatbox",
    };

    private TestHub(string root, HubEndpoint endpoint)
    {
        Root = root;
        Endpoint = endpoint;
    }

    public string Root { get; }
    public string DataDir => Path.Combine(Root, "data");
    public string PluginsDir => Path.Combine(Root, "app", "plugins");
    public HubEndpoint Endpoint { get; }
    public Hub Hub { get; private set; } = null!;

    /// <summary>Создаёт папки, копирует плагины из их сборки и запускает хаб.</summary>
    /// <param name="tcpPort">Порт TCP или <see langword="null"/>, чтобы его не включать.</param>
    /// <param name="plugins">Какие плагины установить.</param>
    public static async Task<TestHub> StartAsync(int? tcpPort = 0, params string[] plugins)
    {
        var test = new TestHub(Path.Combine(Path.GetTempPath(), "qh-tests", Guid.NewGuid().ToString("N")),
            new HubEndpoint(Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(test.PluginsDir);
        foreach (var plugin in plugins) CopyPlugin(plugin, Path.Combine(test.PluginsDir, plugin));
        await test.BootAsync(tcpPort);
        return test;
    }

    /// <summary>Запускает хаб в существующих папках.</summary>
    /// <param name="tcpPort">Порт TCP.</param>
    public async Task BootAsync(int? tcpPort = 0)
    {
        Hub = new Hub(new HubOptions
        {
            Language = "ru",
            RootDir = Path.Combine(Root, "app"),
            DataDir = DataDir,
            Endpoint = Endpoint,
            TcpPort = tcpPort,
        });
        await Hub.StartAsync();
    }

    /// <summary>Останавливает хаб, оставляя папки: для проверки того, что переживает перезапуск.</summary>
    public async Task ShutdownAsync() => await Hub.DisposeAsync();

    /// <summary>Папка сборки плагина.</summary>
    /// <param name="pluginId">Id плагина.</param>
    public static string BuildOutput(string pluginId)
    {
        var repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "QuantumHub.sln"))) repo = Path.GetDirectoryName(repo)!;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        // Плагину Music нужен WinRT, поэтому его папка сборки называется иначе.
        var framework = pluginId == Music ? "net10.0-windows10.0.26100.0" : "net10.0-windows";
        return Path.Combine(repo, "plugins", Projects[pluginId], "bin", configuration, framework);
    }

    /// <summary>Копирует собранный плагин.</summary>
    /// <param name="pluginId">Id плагина.</param>
    /// <param name="destination">Куда.</param>
    public static void CopyPlugin(string pluginId, string destination)
    {
        var source = BuildOutput(pluginId);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }

    /// <summary>Подключает клиента к этому хабу.</summary>
    /// <param name="clientId">Id клиента.</param>
    /// <param name="dependencies">Зависимости.</param>
    /// <param name="transports">Транспорты.</param>
    /// <param name="provides">Предоставляемые возможности.</param>
    public Task<HubClient> ConnectAsync(string clientId = "test.client", IReadOnlyList<Dependency>? dependencies = null,
        IReadOnlyList<string>? transports = null, IReadOnlyList<Capability>? provides = null) =>
        HubClient.ConnectAsync(new HubClientOptions
        {
            ClientId = clientId,
            Endpoint = Endpoint,
            Dependencies = dependencies ?? [],
            Provides = provides ?? [],
            Transports = transports ?? ["shm", "stream"],
        });

    public async ValueTask DisposeAsync()
    {
        try { await Hub.DisposeAsync(); } catch (ObjectDisposedException) { }
        for (var i = 0; i < 10; i++)
        {
            try
            {
                Directory.Delete(Root, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(50);
            }
        }
    }
}
