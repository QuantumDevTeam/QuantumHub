using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QuantumHub.Contracts.Bus;
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
    /// <summary>Закрываемся на самом деле, а не сворачиваемся в трей.</summary>
    private bool _reallyClosing;
    /// <summary>Подписки на хаб уже оформлены.</summary>
    private bool _subscribed;
    /// <summary>Завершается после первого подключения и построения страниц.</summary>
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Создаёт окно.</summary>
    /// <param name="hub">Связь с хабом.</param>
    /// <param name="initialPage">Какую страницу показать первой.</param>
    public MainWindow(HubConnection hub, string? initialPage)
    {
        _hub = hub;
        _initialPage = initialPage ?? "overview";
        InitializeComponent();

        Title = "QuantumHub";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 780));
        AppWindow.Closing += OnClosing;
        LogoImage.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png")));

        Hwnd = WindowNative.GetWindowHandle(this);
        _hub.StateChanged += OnHubStateChanged;
        ShowStatus(false);
        Navigate(_initialPage);
    }

    /// <summary>Хэндл окна для функций WinAPI.</summary>
    public nint Hwnd { get; }

    /// <summary>Крестик сворачивает окно в трей. Выход находится в меню трея.</summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_reallyClosing) return;
        args.Cancel = true;
        AppWindow.Hide();
    }

    /// <summary>Прячет окно при старте с --minimized.</summary>
    public void PrepareHidden() => AppWindow.Hide();

    /// <summary>Показывает окно и выводит его на передний план.</summary>
    public void ShowAndFocus()
    {
        AppWindow.Show();
        Activate();
        SetForegroundWindow(Hwnd);
    }

    /// <summary>Закрывает окно по-настоящему.</summary>
    public void CloseForReal()
    {
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
        Navigate(CurrentTag ?? _initialPage);
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

    /// <summary>Обновляет индикатор связи в заголовке.</summary>
    private void ShowStatus(bool connected)
    {
        var color = connected ? Windows.UI.Color.FromArgb(255, 34, 197, 94) : Windows.UI.Color.FromArgb(255, 234, 179, 8);
        StatusDot.Fill = new SolidColorBrush(color);
        StatusPill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(32, color.R, color.G, color.B));
        StatusText.Text = connected ? $"подключено · {_hub.Client?.Transport}" : "нет связи с хабом";
    }

    /// <summary>Держит установленные плагины в панели навигации, как разделы сайта.</summary>
    private async Task RefreshPluginsAsync()
    {
        var list = await _hub.TryCallAsync<PluginList>("hub.plugins.list", new PluginRef());
        if (list is null) return;

        var existing = Nav.MenuItems.OfType<NavigationViewItem>()
            .Where(item => item.Tag is string tag && tag.StartsWith(PluginTagPrefix, StringComparison.Ordinal))
            .ToDictionary(item => (string)item.Tag);
        var wanted = list.Plugins.ToDictionary(plugin => PluginTagPrefix + plugin.Manifest.Id);

        foreach (var (tag, item) in existing)
        {
            if (!wanted.ContainsKey(tag)) Nav.MenuItems.Remove(item);
        }

        foreach (var (tag, plugin) in wanted)
        {
            if (existing.TryGetValue(tag, out var item))
            {
                item.InfoBadge = Badge(plugin);
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
    private static InfoBadge? Badge(PluginInfo plugin) => plugin.State is PluginState.Faulted or PluginState.Blocked
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
        PageHost.Content = tag switch
        {
            "plugins" => new PluginsPage(_hub, this),
            "sessions" => new SessionsPage(_hub),
            "logs" => new LogsPage(_hub),
            "settings" => new SettingsPage(_hub),
            _ when tag.StartsWith(PluginTagPrefix, StringComparison.Ordinal) => new PluginPage(_hub, tag[PluginTagPrefix.Length..]),
            _ => new OverviewPage(_hub),
        };
    }

    /// <summary>Открывает страницу плагина.</summary>
    /// <param name="id">Id плагина.</param>
    public void OpenPlugin(string id) => Navigate(PluginTagPrefix + id);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);
}
