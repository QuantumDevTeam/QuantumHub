using System.Diagnostics;
using QuantumHub.Core;
using QuantumHub.ProtoMemProtocol;

namespace QuantumHub.Service;

/// <summary>
/// Режим <c>--watch</c>: маленький процесс, который держит хаб запущенным. Плагины работают внутри хаба, и необработанное
/// исключение в потоке плагина завершает весь процесс. Windows Service тут не помог бы, причина в описании <see cref="Program"/>.
/// </summary>
internal static class Watchdog
{
    /// <summary>Запускает хаб и поднимает его после падений.</summary>
    /// <param name="args">Аргументы, с которыми запущен сторож.</param>
    /// <param name="endpoint">Экземпляр хаба.</param>
    /// <param name="onGaveUp">Сообщает, что хаб падает слишком часто.</param>
    /// <returns>Код завершения процесса.</returns>
    public static int Run(string[] args, HubEndpoint endpoint, Action<string> onGaveUp)
    {
        using var single = new Mutex(true, $@"Local\QuantumHub.Watch.{endpoint.Instance}", out var isFirst);
        if (!isFirst) return Program.AlreadyRunning;

        try
        {
            var policy = new CrashPolicy();
            while (true)
            {
                var code = RunHub(args);

                // Обычный выход и «уже запущен» не падения: первый попросили, а второй значит, что хаб и так работает.
                if (code is 0 or Program.AlreadyRunning) return 0;

                if (policy.NoteCrash(DateTime.UtcNow) is not { } delay)
                {
                    onGaveUp($"The hub crashed {CrashPolicy.MaxCrashes + 1} times in {CrashPolicy.Window.TotalMinutes} minutes. It stays down until the next start.");
                    return 1;
                }

                Thread.Sleep(delay);
            }
        }
        finally
        {
            single.ReleaseMutex();
        }
    }

    /// <summary>Запускает хаб и ждёт его выхода.</summary>
    /// <returns>Код завершения хаба.</returns>
    private static int RunHub(string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var argument in args.Where(argument => argument != Program.WatchArgument)) start.ArgumentList.Add(argument);

        using var hub = Process.Start(start)!;
        hub.WaitForExit();
        return hub.ExitCode;
    }
}
