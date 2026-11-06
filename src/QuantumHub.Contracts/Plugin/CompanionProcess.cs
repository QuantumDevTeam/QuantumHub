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
                if (!_reportedMissing) context.Logger.Warning($"Не найдено приложение плагина: {executable}");
                _reportedMissing = true;
                return;
            }

            _process = Process.Start(new ProcessStartInfo(executable, $"--instance \"{context.HubInstance}\"")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable),
            });
        }
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
