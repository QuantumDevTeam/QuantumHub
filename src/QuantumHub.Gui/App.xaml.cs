using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using QuantumHub.Gui.Services;
using QuantumHub.ProtoMemProtocol;

namespace QuantumHub.Gui;

/// <summary>Приложение GUI: единственность экземпляра, трей, главное окно и связь с хабом.</summary>
/// <remarks>
/// Аргументы: <c>--minimized</c> стартует только в трее, <c>--instance</c> выбирает экземпляр хаба,
/// <c>--page</c> открывает страницу, <c>--service-args</c> передаёт аргументы хабу, если GUI запускает его сам,
/// <c>--install</c> устанавливает плагин из файла: так открываются файлы .qhp из проводника.
/// </remarks>
public partial class App : Application
{
    /// <summary>Показывает, что GUI этого экземпляра уже запущен.</summary>
    private Mutex? _single;
    /// <summary>Второй запуск сигналит им первому, чтобы тот показал окно.</summary>
    private EventWaitHandle? _showSignal;
    private MainWindow? _window;
    private HubConnection? _hub;
    private TrayIcon? _tray;

    /// <summary>Подключает глобальную обработку ошибок.</summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // Упавшая страница плагина не должна ронять всю админку.
            e.Handled = true;
            Log($"Unhandled: {e.Exception}");
        };
    }

    /// <summary>Пишет в журнал GUI, %APPDATA%\QuantumHub\logs\gui.log.</summary>
    /// <param name="message">Текст.</param>
    public static void Log(string message)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuantumHub", "logs", "gui.log");
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

    /// <summary>Точка входа WinUI.</summary>
    /// <param name="args">Аргументы запуска.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs();
        var instance = Option(argv, "--instance") ?? HubEndpoint.DefaultInstance;
        if (Option(argv, "--install") is { } install) PendingInstalls.Add(instance, Path.GetFullPath(install));

        _single = new Mutex(true, $@"Local\QuantumHub.Gui.{instance}", out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\QuantumHub.Gui.Show.{instance}");
        if (!isFirst)
        {
            // Уже запущен, скорее всего в трее: поднимаем его окно.
            _showSignal.Set();
            Exit();
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _hub = new HubConnection(new HubEndpoint(instance), dispatcher, Option(argv, "--service-args"));
        _window = new MainWindow(_hub, Option(argv, "--page"));
        _tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"), "QuantumHub",
            ShowWindow, Quit, QuitAndStopHub);

        ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => dispatcher.TryEnqueue(() =>
            {
                ShowWindow();
                RunPendingInstalls(instance);
            }), null, Timeout.Infinite, false);

        if (argv.Contains("--minimized")) _window.PrepareHidden();
        else ShowWindow();

        _ = _hub.StartAsync();
        RunPendingInstalls(instance);
    }

    /// <summary>Устанавливает плагины, которые открыли в проводнике.</summary>
    private async void RunPendingInstalls(string instance)
    {
        foreach (var path in PendingInstalls.Take(instance))
        {
            if (_window is not null) await _window.InstallFromShellAsync(path);
        }
    }

    /// <summary>Показывает окно.</summary>
    private void ShowWindow() => _window?.ShowAndFocus();

    /// <summary>Закрывает интерфейс. Хаб продолжает работать.</summary>
    private void Quit()
    {
        _tray?.Dispose();
        _window?.CloseForReal();
        Exit();
    }

    /// <summary>Завершает хаб и закрывает интерфейс.</summary>
    private async void QuitAndStopHub()
    {
        if (_hub is not null) await _hub.ShutdownHubAsync();
        Quit();
    }

    /// <summary>Значение аргумента командной строки.</summary>
    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
