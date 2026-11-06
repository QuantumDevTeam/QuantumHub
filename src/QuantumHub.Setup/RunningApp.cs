using System.ComponentModel;
using System.Diagnostics;
using QuantumHub.Client;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Proto;
using QuantumHub.ProtoMemProtocol;

namespace QuantumHub.Setup;

/// <summary>Закрывает программу, чтобы можно было заменить её файлы. Касается только процессов из папки программы.</summary>
internal static class RunningApp
{
    /// <summary>Сколько ждать, пока хаб завершится сам и остановит плагины.</summary>
    private static readonly TimeSpan GracefulStop = TimeSpan.FromSeconds(15);

    /// <summary>Закрывает всё, что запущено из папки.</summary>
    /// <param name="directory">Папка программы.</param>
    public static async Task StopAsync(string directory)
    {
        // GUI первым: иначе он увидит пропажу хаба и поднимет его снова.
        Kill(Find(directory).Where(process => process.ProcessName == "QuantumHub.Gui"));

        if (Find(directory).Any(process => process.ProcessName == "QuantumHub.Service"))
        {
            await AskHubToStopAsync(directory);
            var deadline = DateTime.UtcNow + GracefulStop;
            while (DateTime.UtcNow < deadline && Find(directory).Any(process => process.ProcessName == "QuantumHub.Service"))
                await Task.Delay(200);
        }

        Kill(Find(directory));
    }

    /// <summary>Просит хаб завершиться: так плагины успевают вернуть всё на место и сохранить настройки.</summary>
    private static async Task AskHubToStopAsync(string directory)
    {
        try
        {
            await using var client = await HubClient.ConnectAsync(new HubClientOptions
            {
                ClientId = "quantumhub.setup",
                DisplayName = "QuantumHub Setup",
                ConnectTimeout = TimeSpan.FromSeconds(2),
                CallTimeout = TimeSpan.FromSeconds(5),
            });

            // Хаб по умолчанию мог быть запущен и из другой папки, тот не трогаем.
            var status = await client.CallAsync<HubStatus>(Envelopes.HubTarget, "hub.status", new PluginRef());
            if (IsInside(status.RootDir, directory)) await client.CallAsync(Envelopes.HubTarget, "hub.shutdown");
        }
        catch (Exception ex) when (ex is HubCallException or TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            // Значит, завершим силой.
        }
    }

    /// <summary>Завершает процессы и ждёт их выхода.</summary>
    private static void Kill(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                process.Kill(true);
                process.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // Либо уже вышел, либо запущен с правами выше наших. Второе проявится ошибкой удаления файлов.
            }
        }
    }

    /// <summary>Процессы, запущенные из папки.</summary>
    private static List<Process> Find(string directory)
    {
        var found = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            if (process.Id == Environment.ProcessId) continue;
            try
            {
                if (process.MainModule?.FileName is { } path && IsInside(path, directory)) found.Add(process);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Системный или чужой процесс, к которому нас не пускают.
            }
        }

        return found;
    }

    /// <summary>Лежит ли путь внутри папки.</summary>
    private static bool IsInside(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
