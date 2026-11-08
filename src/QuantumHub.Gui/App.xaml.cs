using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using QuantumHub.Contracts.Links;
using QuantumHub.Gui.Services;
using QuantumHub.ProtoMemProtocol;

namespace QuantumHub.Gui;

/// <summary>Приложение GUI: единственность экземпляра, трей, главное окно и связь с хабом.</summary>
/// <remarks>
/// Аргументы: <c>--minimized</c> стартует только в трее, <c>--instance</c> выбирает экземпляр хаба,
/// <c>--page</c> открывает страницу, <c>--service-args</c> передаёт аргументы хабу, если GUI запускает его сам,
/// <c>--install</c> устанавливает плагин из файла: так открываются файлы .qhp из проводника,
/// <c>--link</c> открывает ссылку quantumhub://.
/// </remarks>
public partial class App : Application
{
    /// <summary>Пауза перед первой проверкой обновлений: при запуске есть дела важнее.</summary>
    private static readonly TimeSpan UpdateFirstDelay = TimeSpan.FromSeconds(10);
    /// <summary>Как часто проверять обновления.</summary>
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromDays(1);

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
        // Тему можно задать только здесь: кисти, которые страницы берут из кода, зависят от темы приложения.
        GuiSettings.Init(Option(Environment.GetCommandLineArgs(), "--instance") ?? HubEndpoint.DefaultInstance);
        ThemeAtStart = GuiSettings.Current.Theme;
        if (ThemeAtStart == "light") RequestedTheme = ApplicationTheme.Light;
        if (ThemeAtStart == "dark") RequestedTheme = ApplicationTheme.Dark;

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
        var linkText = Option(argv, "--link");
        if (linkText is not null) PendingInstalls.Add(instance, linkText);

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
        _window = new MainWindow(_hub, Option(argv, "--page"), instance);
        _tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"), "QuantumHub",
            ShowWindow, Quit, QuitAndStopHub);
        _window.ExitRequested += () => dispatcher.TryEnqueue(Quit);

        ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => dispatcher.TryEnqueue(() => RunPending(instance, true)), null, Timeout.Infinite, false);

        // Разрешённое действие выполняется молча, например с кнопки Stream Deck: окну незачем выскакивать.
        if (argv.Contains("--minimized") || IsRunLink(linkText)) _window.PrepareHidden();
        else ShowWindow();

        _ = _hub.StartAsync();
        RunPending(instance, false);
        _ = CheckUpdatesAsync(dispatcher);
    }

    /// <summary>Ссылка на действие плагина.</summary>
    private static bool IsRunLink(string? text) => HubLink.TryParse(text, out var link) && link.Kind == HubLinkKind.Run;

    /// <summary>Выполняет то, что пришло из проводника и по ссылкам.</summary>
    /// <param name="instance">Экземпляр.</param>
    /// <param name="show">Показать окно, если очередь не состоит из одних действий: второй запуск просит именно этого.</param>
    private async void RunPending(string instance, bool show)
    {
        var items = PendingInstalls.Take(instance);
        if (show && (items.Length == 0 || !items.All(IsRunLink))) ShowWindow();

        foreach (var item in items)
        {
            if (_window is null) return;
            if (HubLink.HasScheme(item)) await _window.OpenLinkAsync(item);
            else await _window.InstallFromShellAsync(item);
        }
    }

    /// <summary>Проверяет обновления при запуске и потом раз в день.</summary>
    private static async Task CheckUpdatesAsync(DispatcherQueue dispatcher)
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        await Task.Delay(UpdateFirstDelay);
        using var timer = new PeriodicTimer(UpdateInterval);
        do
        {
            if (GuiSettings.Current.CheckUpdates && await UpdateChecker.CheckAsync(version) is not null)
                dispatcher.TryEnqueue(() => UpdateFound?.Invoke());
        }
        while (await timer.WaitForNextTickAsync());
    }

    /// <summary>Нашлась новая версия. Срабатывает на потоке интерфейса.</summary>
    public static event Action? UpdateFound;

    /// <summary>Тема, с которой запущено окно.</summary>
    public static string ThemeAtStart { get; private set; } = GuiSettings.System;

    /// <summary>Перезапускает только интерфейс: хаб и плагины продолжают работать.</summary>
    /// <param name="page">Какую страницу открыть.</param>
    public void RestartInterface(string page)
    {
        var argv = Environment.GetCommandLineArgs();
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var name in new[] { "--instance", "--service-args" })
        {
            if (Option(argv, name) is not { } value) continue;
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(value);
        }

        start.ArgumentList.Add("--page");
        start.ArgumentList.Add(page);

        _tray?.Dispose();
        _window?.CloseForReal();

        // Мьютекс отпускаем заранее, иначе новый запуск решит, что окно уже открыто, и просто закроется.
        _single?.ReleaseMutex();
        _single?.Dispose();
        _single = null;
        Process.Start(start)?.Dispose();
        Exit();
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
