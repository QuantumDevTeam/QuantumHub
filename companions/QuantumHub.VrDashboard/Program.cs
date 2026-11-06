using System.Diagnostics;
using System.IO;
using QuantumHub.Client;
using QuantumHub.Contracts.Ui;
using QuantumHub.ProtoMemProtocol;

namespace QuantumHub.VrDashboard;

/// <summary>
/// Панель в меню SteamVR. Внешний плагин: отдельный процесс, который говорит с хабом по пайпу как обычный клиент.
/// Запускает её плагин qh.vr.dashboard, когда SteamVR уже работает. Сама она SteamVR не запускает никогда.
/// </summary>
/// <remarks>Аргумент <c>--preview файл.png</c> рисует панель с примером данных и завершается, шлем для этого не нужен.</remarks>
internal static class Program
{
    /// <summary>Сколько пробовать подключиться к хабу.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    [STAThread]
    private static int Main(string[] args)
    {
        if (Value(args, "--language") is { } language) Loc.Language = language;
        if (Value(args, "--preview") is { } preview) return Preview(preview);

        var instance = Value(args, "--instance") ?? HubEndpoint.DefaultInstance;
        using var single = new Mutex(true, $@"Local\QuantumHub.VrDashboard.{instance}", out var isFirst);
        if (!isFirst) return 1;

        // OpenVR запускает SteamVR сам, если его нет, а без спроса этого делать нельзя.
        if (Process.GetProcessesByName("vrserver").Length == 0)
        {
            Log("SteamVR is not running.");
            return 0;
        }

        try
        {
            var client = HubClient.ConnectAsync(new HubClientOptions
            {
                ClientId = "qh.vr.dashboard.overlay",
                DisplayName = "SteamVR Dashboard",
                Endpoint = new HubEndpoint(instance),
                AutoReconnect = true,
                ConnectTimeout = ConnectTimeout,
            }).GetAwaiter().GetResult();

            return new OverlayHost(new PanelClient(client), Log).Run();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or DllNotFoundException)
        {
            Log(ex.ToString());
            return 1;
        }
    }

    /// <summary>Рисует панель с примером данных.</summary>
    private static int Preview(string path)
    {
        var state = new PanelState(true, true, true, true, false, false, true, true, true, true, true, "drinking tea",
            ["drinking tea", "afk", "coding"], "💬 drinking tea\n🥽 In VR\n21:42\n🎵 'Song' ᵇʸ Artist");
        PanelRenderer.SavePng(PanelLayout.Build(state, new PanelOptions()), state, path);
        return 0;
    }

    /// <summary>Пишет в журнал. Окна у панели нет, поэтому причину ошибки видно только здесь.</summary>
    private static void Log(string message)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuantumHub", "logs", "vr-dashboard.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // С ошибкой журнала уже ничего разумного не сделать.
        }
    }

    /// <summary>Значение аргумента командной строки.</summary>
    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
