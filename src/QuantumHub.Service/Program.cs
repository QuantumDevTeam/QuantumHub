using System.Runtime.InteropServices;
using QuantumHub.Core;
using QuantumHub.ProtoMemProtocol;

namespace QuantumHub.Service;

/// <summary>
/// Фоновый хаб. Это процесс в сессии пользователя, а не Windows Service, и сделано так намеренно: в session 0
/// нет рабочего стола, и активные окна, звуковые устройства и подобное были бы недоступны как раз тем
/// плагинам, ради которых всё затевалось.
/// </summary>
/// <remarks>
/// Аргументы: <c>--console</c> показывает журнал, <c>--root</c> задаёт папку программы, <c>--data</c> папку данных,
/// <c>--instance</c> имя экземпляра, <c>--tcp порт</c> включает TCP.
/// </remarks>
internal static class Program
{
    /// <summary>Константа WinAPI: подключиться к консоли родительского процесса.</summary>
    private const int AttachParentProcess = -1;

    /// <summary>Точка входа. Отвечает за единственность экземпляра.</summary>
    /// <param name="args">Аргументы командной строки.</param>
    /// <returns>0 при обычном завершении, 1 если хаб уже запущен.</returns>
    private static int Main(string[] args)
    {
        var console = args.Contains("--console");
        if (console && !AttachConsole(AttachParentProcess)) AllocConsole();

        var instance = Value(args, "--instance") ?? HubEndpoint.DefaultInstance;
        var endpoint = new HubEndpoint(instance);

        // Мьютекс должен освободить тот же поток, что его взял, поэтому он остаётся здесь, в Main.
        using var single = new Mutex(true, endpoint.MutexName, out var isFirst);
        if (!isFirst)
        {
            if (console) Console.WriteLine("QuantumHub is already running.");
            return 1;
        }

        try
        {
            RunAsync(args, endpoint, console).GetAwaiter().GetResult();
            return 0;
        }
        finally
        {
            single.ReleaseMutex();
        }
    }

    /// <summary>Запускает хаб и ждёт команды на завершение.</summary>
    private static async Task RunAsync(string[] args, HubEndpoint endpoint, bool console)
    {
        var tcp = Value(args, "--tcp");
        await using var hub = new Hub(new HubOptions
        {
            RootDir = Value(args, "--root") ?? AppContext.BaseDirectory,
            DataDir = Value(args, "--data"),
            Endpoint = endpoint,
            TcpPort = tcp is null ? null : int.Parse(tcp),
            LogToConsole = console,
        });

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.TrySetResult();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();

        await hub.StartAsync();
        await Task.WhenAny(stop.Task, hub.ShutdownRequested);
    }

    /// <summary>Значение аргумента командной строки.</summary>
    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();
}
