using System.Diagnostics;

namespace QuantumHub.Plugin.PicoAntiSleep;

/// <summary>Настоящий запуск adb.exe.</summary>
/// <param name="findAdb">Находит adb.exe. <see langword="null"/>, если его нет.</param>
public sealed class AdbProcessRunner(Func<string?> findAdb) : IAdbRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Сервер запускается без перехвата вывода. Он остаётся жить после команды и наследует её потоки,
    /// поэтому чтение вывода такой команды не закончилось бы никогда.
    /// </summary>
    /// <param name="token">Отмена.</param>
    public async Task StartServerAsync(CancellationToken token)
    {
        var path = findAdb() ?? throw new FileNotFoundException("adb.exe не найден. Укажи путь в настройках.");
        using var process = Process.Start(new ProcessStartInfo(path, "start-server")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Timeout);
        await process.WaitForExitAsync(timeout.Token);
    }

    /// <inheritdoc/>
    public async Task<AdbResult> RunAsync(string arguments, CancellationToken token)
    {
        var path = findAdb() ?? throw new FileNotFoundException("adb.exe не найден. Укажи путь в настройках.");
        using var process = Process.Start(new ProcessStartInfo(path, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        // Оба потока читаются одновременно: иначе заполнившийся буфер второго остановил бы процесс.
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            process.Kill(true);
            return new AdbResult(-1, $"adb {arguments}: нет ответа");
        }

        await Task.WhenAny(Task.WhenAll(output, error), Task.Delay(500, token));
        return new AdbResult(process.ExitCode, (output.IsCompletedSuccessfully ? output.Result : string.Empty) +
                                               (error.IsCompletedSuccessfully ? error.Result : string.Empty));
    }
}
