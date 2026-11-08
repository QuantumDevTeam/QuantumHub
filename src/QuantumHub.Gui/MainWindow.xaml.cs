using QuantumHub.Contracts.Ui;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Links;
using QuantumHub.Contracts.Proto;
using QuantumHub.Gui.Pages;
using QuantumHub.Gui.Services;
using WinRT.Interop;

namespace QuantumHub.Gui;

/// <summary>Главное окно: заголовок с индикатором связи и навигация по страницам.</summary>
public sealed partial class MainWindow : Window
{
    /// <summary>Префикс тега пункта навигации, ведущего на плагин.</summary>
    private const string PluginTagPrefix = "plugin:";

    private readonly HubConnection _hub;
    private readonly string _initialPage;
    private readonly string _instance;
    /// <summary>Фон, который уже применён: пересоздавать его при каждой настройке незачем.</summary>
    private string? _backdrop;
    /// <summary>Откладывает запись размера, пока окно тянут за край.</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    /// <summary>Закрываемся на самом деле, а не сворачиваемся в трей.</summary>
    private bool _reallyClosing;
    /// <summary>Подписки на хаб уже оформлены.</summary>
    private bool _subscribed;
    /// <summary>Очередь диалогов от ссылок.</summary>
    private readonly SemaphoreSlim _dialogs = new(1, 1);
    /// <summary>Завершается после первого подключения и построения страниц.</summary>
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Создаёт окно.</summary>
    /// <param name="hub">Связь с хабом.</param>
    /// <param name="initialPage">Какую страницу показать первой.</param>
    /// <param name="instance">Экземпляр хаба: у каждого свой размер окна.</param>
    public MainWindow(HubConnection hub, string? initialPage, string instance)
    {
        _hub = hub;
        _instance = instance;
        _initialPage = initialPage ?? GuiSettings.Current.InitialPage;
        InitializeComponent();

        Title = "QuantumHub";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Closing += OnClosing;
        LogoImage.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png")));

        Hwnd = WindowNative.GetWindowHandle(this);
        WindowPlacement.Apply(AppWindow, Scale, _instance, GuiSettings.Current.RememberWindow);
        Nav.IsPaneOpen = GuiSettings.Current.InitialPaneOpen;
        Nav.PaneOpened += (_, _) => RememberPane(true);
        Nav.PaneClosed += (_, _) => RememberPane(false);
        GuiSettings.Changed += ApplySettings;
        ApplySettings();
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromSeconds(1);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SavePlacement();
        AppWindow.Changed += (_, change) =>
        {
            if ((change.DidSizeChange || change.DidPositionChange) && AppWindow.IsVisible) _saveTimer.Start();
            if (change.DidPresenterChange && GuiSettings.Current.MinimizeToTray &&
                AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) AppWindow.Hide();
        };
        _hub.StateChanged += OnHubStateChanged;
        _hub.LanguageChanged += OnLanguageChanged;
        ApplyLanguage();
        ShowStatus(false);
        Navigate(_initialPage);
    }

    /// <summary>Хэндл окна для функций WinAPI.</summary>
    public nint Hwnd { get; }

    /// <summary>Масштаб экрана, на котором окно: 1 это 100%.</summary>
    private double Scale => GetDpiForWindow(Hwnd) / 96.0;

    /// <summary>Запоминает размер и место окна.</summary>
    private void SavePlacement()
    {
        if (GuiSettings.Current.RememberWindow) WindowPlacement.Capture(AppWindow, Scale, _instance);
    }

    /// <summary>Запоминает, открыто ли меню.</summary>
    private static void RememberPane(bool open)
    {
        if (GuiSettings.Current.PaneWasOpen != open) GuiSettings.Note(settings => settings with { PaneWasOpen = open });
    }

    /// <summary>Попросили закрыть интерфейс крестиком.</summary>
    public event Action? ExitRequested;

    /// <summary>Применяет настройки окна.</summary>
    private void ApplySettings()
    {
        var settings = GuiSettings.Current;
        AppWindow.TitleBar.PreferredTheme = App.ThemeAtStart switch
        {
            "light" => TitleBarTheme.Light,
            "dark" => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };

        if (_backdrop != settings.Backdrop)
        {
            _backdrop = settings.Backdrop;
            SystemBackdrop = settings.Backdrop switch
            {
                "micaAlt" => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
                "acrylic" => new DesktopAcrylicBackdrop(),
                "none" => null,
                _ => new MicaBackdrop(),
            };
        }

        RootGrid.Background = settings.Backdrop == "none" ? Fluent.Resource("SolidBackgroundFillColorBaseBrush") : null;
        StatusPill.Visibility = settings.ShowStatus ? Visibility.Visible : Visibility.Collapsed;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = settings.AlwaysOnTop;

        var showPlugins = settings.MenuPlugins != "none";
        PluginsHeader.Visibility = showPlugins ? Visibility.Visible : Visibility.Collapsed;
        if (_hub.IsConnected) _ = RefreshPluginsAsync();
    }

    /// <summary>Возвращает окну размер и место по умолчанию.</summary>
    public void ResetPlacement()
    {
        WindowPlacement.Forget(_instance);
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        WindowPlacement.Apply(AppWindow, Scale, _instance, false);
    }

    /// <summary>Строит страницу заново: например, после сброса настроек.</summary>
    /// <param name="tag">Тег страницы.</param>
    public void Reopen(string tag) => Show(tag);

    /// <summary>Крестик сворачивает окно в трей. Выход находится в меню трея.</summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_reallyClosing) return;
        args.Cancel = true;
        SavePlacement();
        if (GuiSettings.Current.CloseAction == "exit") ExitRequested?.Invoke();
        else AppWindow.Hide();
    }

    /// <summary>Прячет окно при старте с --minimized.</summary>
    public void PrepareHidden() => AppWindow.Hide();

    /// <summary>Показывает окно и выводит его на передний план.</summary>
    public void ShowAndFocus()
    {
        // Окно, свёрнутое в трей, осталось свёрнутым: без этого оно появилось бы только на панели задач.
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        AppWindow.Show();
        Activate();
        SetForegroundWindow(Hwnd);
    }

    /// <summary>Закрывает окно по-настоящему.</summary>
    public void CloseForReal()
    {
        if (AppWindow.IsVisible) SavePlacement();
        _reallyClosing = true;
        Close();
    }

    /// <summary>Реакция на появление и пропажу связи с хабом.</summary>
    private async void OnHubStateChanged(bool connected)
    {
        ShowStatus(connected);
        if (!connected)
        {
            if (CurrentTag == "overview") Show("overview");
            return;
        }

        // Только один раз: после переподключения клиент сам восстанавливает подписки.
        if (!_subscribed)
        {
            _subscribed = true;
            await _hub.Client!.SubscribeAsync("hub.plugin.state", e => _hub.Post(() => { _ = RefreshPluginsAsync(); }));
            await _hub.Client.SubscribeAsync("hub.plugin.removed", e => _hub.Post(() => { _ = RefreshPluginsAsync(); }));
        }

        await RefreshPluginsAsync();

        // Страница, построенная до подключения, пуста: её надо создать заново, даже если выбор не меняется.
        var tag = CurrentTag ?? _initialPage;
        CurrentTag = null;
        Navigate(tag);
        _ready.TrySetResult();
    }

    /// <summary>Устанавливает плагин, открытый из проводника, и показывает результат на странице плагинов.</summary>
    /// <param name="path">Путь к файлу плагина.</param>
    public async Task InstallFromShellAsync(string path)
    {
        ShowAndFocus();
        await _ready.Task;
        Navigate("plugins");
        if (PageHost.Content is PluginsPage page) await page.ConfirmAndInstallAsync(path);
    }

    /// <summary>Открывает ссылку quantumhub://. Страницы открываются сразу, а действие плагина сначала показывается пользователю.</summary>
    /// <param name="text">Текст ссылки.</param>
    public async Task OpenLinkAsync(string text)
    {
        if (!HubLink.TryParse(text, out var link))
        {
            App.Log(Loc.T($"Непонятная ссылка: {text}", $"Unknown link: {text}"));
            ShowAndFocus();
            await InformAsync(Loc.T("Ссылка не открыта", "The link was not opened"), Loc.T("QuantumHub не знает такой ссылки.", "QuantumHub does not know this link."));
            return;
        }

        if (link.Kind != HubLinkKind.Run) ShowAndFocus();
        await _ready.Task;

        switch (link.Kind)
        {
            case HubLinkKind.Page:
                Navigate(link.Page!);
                break;
            case HubLinkKind.Plugin:
                OpenPlugin(link.PluginId!);
                break;
            case HubLinkKind.Run:
                await RunLinkAsync(link);
                break;
        }
    }

    /// <summary>Запускает действие плагина. Ссылку мог подсунуть любой сайт, поэтому первый раз решает пользователь.</summary>
    private async Task RunLinkAsync(HubLink link)
    {
        var list = await _hub.TryCallAsync<PluginList>("hub.plugins.list", new PluginRef());
        var plugin = list?.Plugins.FirstOrDefault(item => item.Manifest.Id == link.PluginId);
        var action = plugin?.Manifest.Links.FirstOrDefault(item => item.Action == link.Action);
        if (!GuiSettings.Current.LinkActions || action is null)
        {
            ShowAndFocus();
            await InformAsync(Loc.T("Ссылка не открыта", "The link was not opened"), !GuiSettings.Current.LinkActions
                ? Loc.T("Действия по ссылкам выключены в настройках.", "Link actions are turned off in the settings.")
                : Loc.T($"У плагина {link.PluginId} нет действия {link.Action}.", $"The plugin {link.PluginId} has no action {link.Action}."));
            return;
        }

        var key = $"{link.PluginId}/{link.Action}";
        if (!GuiSettings.Current.TrustedLinks.Contains(key))
        {
            ShowAndFocus();
            if (!await AskLinkAsync(plugin!, action, link, key)) return;
        }

        var request = new LinkRunRequest { PluginId = link.PluginId!, Action = link.Action! };
        foreach (var (name, value) in link.Params) request.Params[name] = value;
        if (await _hub.TryRunAsync("hub.links.run", request) is { } error)
        {
            ShowAndFocus();
            await InformAsync(Loc.T("Действие не выполнено", "The action failed"), error);
        }
    }

    /// <summary>Спрашивает, выполнять ли действие.</summary>
    private async Task<bool> AskLinkAsync(PluginInfo plugin, LinkAction action, HubLink link, string key)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Fluent.Text(Loc.T($"Ссылка просит плагин {plugin.Manifest.Name} выполнить действие:", $"A link asks the plugin {plugin.Manifest.Name} to run an action:")));
        body.Children.Add(Fluent.Text(action.Title.Length > 0 ? action.Title : action.Action, "BodyStrongTextBlockStyle"));
        if (action.Description.Length > 0) body.Children.Add(Fluent.Caption(action.Description));
        foreach (var (name, value) in link.Params) body.Children.Add(Fluent.Caption($"{name} = {value}"));
        body.Children.Add(Fluent.Caption(Loc.T("Разрешай, только если сам нажал на эту ссылку.", "Allow this only if you clicked the link yourself.")));
        var remember = new CheckBox { Content = Loc.T("Больше не спрашивать про это действие", "Do not ask about this action again") };
        body.Children.Add(remember);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Loc.T("Выполнить действие?", "Run the action?"),
            Content = body,
            PrimaryButtonText = Loc.T("Выполнить", "Run"),
            CloseButtonText = Loc.T("Отмена", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return false;
        if (remember.IsChecked == true)
            GuiSettings.Update(settings => settings with { TrustedLinks = [.. settings.TrustedLinks.Append(key).Distinct()] });
        return true;
    }

    /// <summary>Показывает сообщение.</summary>
    private async Task InformAsync(string title, string message) => await ShowDialogAsync(new ContentDialog
    {
        XamlRoot = Content.XamlRoot,
        Title = title,
        Content = message,
        CloseButtonText = "OK",
    });

    /// <summary>Показывает диалог. Двух диалогов сразу WinUI не умеет, поэтому они ждут очереди.</summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        await _dialogs.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        catch (COMException ex)
        {
            // Открыт другой диалог, например со страницы плагинов.
            App.Log(ex.Message);
            return ContentDialogResult.None;
        }
        finally
        {
            _dialogs.Release();
        }
    }

    /// <summary>Подписи меню на текущем языке.</summary>
    private void ApplyLanguage()
    {
        foreach (var item in Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>())
        {
            item.Content = item.Tag switch
            {
                "overview" => Loc.T("Обзор", "Overview"),
                "plugins" => Loc.T("Плагины", "Plugins"),
                "sessions" => Loc.T("Подключения", "Connections"),
                "logs" => Loc.T("Журнал", "Log"),
                "settings" => Loc.T("Настройки", "Settings"),
                _ => item.Content,
            };
        }

        PluginsHeader.Content = Loc.T("Установленные", "Installed");
    }

    /// <summary>Сменился язык: перестраивает всё, что показано.</summary>
    private async void OnLanguageChanged()
    {
        ApplyLanguage();
        ShowStatus(_hub.IsConnected);

        await RefreshPluginsAsync();
        if (CurrentTag is { } current) Show(current);
    }

    /// <summary>Обновляет индикатор связи в заголовке.</summary>
    private void ShowStatus(bool connected)
    {
        var color = connected ? Windows.UI.Color.FromArgb(255, 34, 197, 94) : Windows.UI.Color.FromArgb(255, 234, 179, 8);
        StatusDot.Fill = new SolidColorBrush(color);
        StatusPill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(32, color.R, color.G, color.B));
        StatusText.Text = connected ? Loc.T($"подключено · {_hub.Client?.Transport}", $"connected · {_hub.Client?.Transport}") : Loc.T("нет связи с хабом", "no connection to the hub");
    }

    /// <summary>Держит установленные плагины в панели навигации, как разделы сайта.</summary>
    private async Task RefreshPluginsAsync()
    {
        var list = await _hub.TryCallAsync<PluginList>("hub.plugins.list", new PluginRef());
        if (list is null) return;

        var existing = Nav.MenuItems.OfType<NavigationViewItem>()
            .Where(item => item.Tag is string tag && tag.StartsWith(PluginTagPrefix, StringComparison.Ordinal))
            .ToDictionary(item => (string)item.Tag);
        // Выключенный плагин убираем из меню, его страница остаётся доступна из списка плагинов.
        var mode = GuiSettings.Current.MenuPlugins;
        var wanted = list.Plugins.Where(plugin => mode == "all" || (mode == "enabled" && plugin.Enabled))
            .ToDictionary(plugin => PluginTagPrefix + plugin.Manifest.Id);

        foreach (var (tag, item) in existing)
        {
            if (!wanted.ContainsKey(tag)) Nav.MenuItems.Remove(item);
        }

        foreach (var (tag, plugin) in wanted)
        {
            if (existing.TryGetValue(tag, out var item))
            {
                item.InfoBadge = Badge(plugin);
                item.Content = plugin.Manifest.Name.Length > 0 ? plugin.Manifest.Name : plugin.Manifest.Id;
                continue;
            }

            item = new NavigationViewItem
            {
                Content = plugin.Manifest.Name.Length > 0 ? plugin.Manifest.Name : plugin.Manifest.Id,
                Tag = tag,
                Icon = new FontIcon { Glyph = "" },
                InfoBadge = Badge(plugin),
            };
            Nav.MenuItems.Add(item);
            _ = LoadIconAsync(item, plugin);
        }
    }

    /// <summary>Красная отметка на пункте сломанного плагина.</summary>
    private static InfoBadge? Badge(PluginInfo plugin) => GuiSettings.Current.ProblemBadges && plugin.State is PluginState.Faulted or PluginState.Blocked
        ? new InfoBadge { Style = (Style)Application.Current.Resources["CriticalIconInfoBadgeStyle"] }
        : null;

    /// <summary>Подгружает иконку плагина с хаба.</summary>
    private async Task LoadIconAsync(NavigationViewItem item, PluginInfo plugin)
    {
        if (plugin.Manifest.Icon.Length == 0) return;
        if (await PluginIcons.LoadAsync(_hub, plugin.Manifest.Id) is { } image)
            item.Icon = new ImageIcon { Source = image };
    }

    /// <summary>Тег открытой страницы.</summary>
    private string? CurrentTag { get; set; }

    /// <summary>Пользователь выбрал пункт навигации.</summary>
    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && tag != CurrentTag) Show(tag);
    }

    /// <summary>Открывает страницу по тегу и выделяет её пункт.</summary>
    /// <param name="tag">Тег страницы.</param>
    public void Navigate(string tag)
    {
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag);
        if (item is not null && !ReferenceEquals(Nav.SelectedItem, item))
        {
            Nav.SelectedItem = item;
            return;
        }

        Show(tag);
    }

    /// <summary>Создаёт страницу и показывает её.</summary>
    private void Show(string tag)
    {
        CurrentTag = tag;
        if (tag != "settings" && GuiSettings.Current.LastPage != tag) GuiSettings.Note(settings => settings with { LastPage = tag });
        PageHost.Content = tag switch
        {
            "plugins" => new PluginsPage(_hub, this),
            "sessions" => new SessionsPage(_hub),
            "logs" => new LogsPage(_hub),
            "settings" => new SettingsPage(_hub, this),
            _ when tag.StartsWith(PluginTagPrefix, StringComparison.Ordinal) => new PluginPage(_hub, tag[PluginTagPrefix.Length..]),
            _ => new OverviewPage(_hub),
        };
    }

    /// <summary>Открывает страницу плагина.</summary>
    /// <param name="id">Id плагина.</param>
    public void OpenPlugin(string id) => Navigate(PluginTagPrefix + id);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
