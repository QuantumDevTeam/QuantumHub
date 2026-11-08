using QuantumHub.Contracts.Ui;
using System.Diagnostics;

namespace QuantumHub.Contracts.Plugin;

/// <summary>
/// Внешняя часть плагина: отдельное приложение, которое плагин запускает сам. Хаб — фоновый процесс без
/// окон, поэтому всё, что плагин хочет показать на экране, живёт в такой части и подключается к хабу
/// как обычный клиент.
/// </summary>
/// <param name="executable">Путь к exe.</param>
/// <param name="context">Контекст плагина.</param>
public sealed class CompanionProcess(string executable, IPluginContext context) : IDisposable
{
    private readonly Lock _gate = new();
    private Process? _process;
    private bool _reportedMissing;

    /// <summary>Работает ли приложение прямо сейчас.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate) return _process is { HasExited: false };
        }
    }

    /// <summary>Запускает приложение, если оно ещё не работает. Повторные вызовы ничего не делают.</summary>
    public void Ensure()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) return;
            _process?.Dispose();
            _process = null;

            if (!File.Exists(executable))
            {
                if (!_reportedMissing) context.Logger.Warning(Loc.T($"Не найдено приложение плагина: {executable}", $"Plugin app not found: {executable}"));
                _reportedMissing = true;
                return;
            }

            var start = new ProcessStartInfo(executable, $"--instance \"{context.HubInstance}\" --language {Ui.Loc.Language}")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable),
            };

            // В релизе .NET не установлен в систему, и внешняя часть должна работать на том же, что и хаб.
            if (RuntimeRoot(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()) is { } runtime)
            {
                start.Environment["DOTNET_ROOT"] = runtime;
                start.Environment["DOTNET_ROOT_X64"] = runtime;
            }

            _process = Process.Start(start);
        }
    }

    /// <summary>Находит корень .NET по папке рантайма: &lt;root&gt;\shared\Microsoft.NETCore.App\&lt;версия&gt;.</summary>
    /// <param name="runtimeDirectory">Папка рантайма.</param>
    /// <returns>Папка или <see langword="null"/>, если раскладка другая.</returns>
    public static string? RuntimeRoot(string runtimeDirectory)
    {
        if (runtimeDirectory.Length == 0) return null;
        var root = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
        return Directory.Exists(Path.Combine(root, "host", "fxr")) ? root : null;
    }

    /// <summary>Закрывает приложение, если оно работает.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) _process.Kill(true);
            _process?.Dispose();
            _process = null;
        }
    }

    /// <summary>Закрывает приложение.</summary>
    public void Dispose() => Stop();
}
