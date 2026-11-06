using QuantumHub.Contracts.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Proto;
using QuantumHub.Gui.Rendering;
using QuantumHub.Gui.Services;
using QuantumHub.Ui.Contracts;

namespace QuantumHub.Gui.Pages;

/// <summary>Страница одного плагина. Заголовок принадлежит GUI, вкладки — плагину: декларативные или нативные.</summary>
public sealed class PluginPage : HubPage, IPluginUiHost
{
    private readonly string _pluginId;
    /// <summary>Здесь показывается содержимое вкладки.</summary>
    private readonly ContentControl _host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _column = new() { Spacing = 16, Padding = new Thickness(36, 24, 36, 36), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
    /// <summary>Текущее состояние плагина: вкладка, открытая позже, видит свежие значения.</summary>
    private readonly Dictionary<string, string> _state = new();
    /// <summary>Рендерер открытой вкладки.</summary>
    private UiRenderer? _renderer;
    /// <summary>Состояние, с которым страница была построена.</summary>
    private PluginState _shownState;
    private bool _subscribed;

    /// <summary>Создаёт страницу.</summary>
    /// <param name="hub">Связь с хабом.</param>
    /// <param name="pluginId">Id плагина.</param>
    public PluginPage(HubConnection hub, string pluginId) : base(hub)
    {
        _pluginId = pluginId;
        _column.MaxWidth = Fluent.PageMaxWidth;
        Content = Fluent.Scroll(_column);
    }

    string IPluginUiHost.PluginId => _pluginId;
    IHubBus IPluginUiHost.Bus => Hub.Client!;
    Microsoft.UI.Dispatching.DispatcherQueue IPluginUiHost.Dispatcher => Hub.Dispatcher;

    /// <inheritdoc/>
    protected override async Task LoadAsync()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            await SubscribeAsync(Core.UiStateTopic, OnStatePatch);
            await SubscribeAsync("hub.plugin.state", OnPluginState);
        }

        var list = await Hub.TryCallAsync<PluginList>("hub.plugins.list", new PluginRef());
        var plugin = list?.Plugins.FirstOrDefault(entry => entry.Manifest.Id == _pluginId);
        if (plugin is null) return;

        _shownState = plugin.State;
        var ui = await Hub.TryCallAsync<UiResponse>("hub.ui.get", new UiRequest { PluginId = _pluginId }) ?? new UiResponse();

        _column.Children.Clear();
        _renderer = null;
        _state.Clear();
        foreach (var (key, value) in ui.State) _state[key] = value;
        _column.Children.Add(await HeaderAsync(plugin));
        if (plugin.State != PluginState.Running) _column.Children.Add(NotRunningBar(plugin));
        _column.Children.Add(Tabs(plugin, ui));
        _column.Children.Add(_host);
    }

    /// <summary>Обновление состояния от плагина.</summary>
    private void OnStatePatch(HubEvent hubEvent)
    {
        var patch = hubEvent.Payload.AsProto<UiStatePatch>();
        if (patch.PluginId != _pluginId) return;

        // Храним и здесь: вкладка, открытая позже, должна показать текущие значения, а не значения на момент загрузки.
        foreach (var (key, value) in patch.State) _state[key] = value;
        _renderer?.ApplyState(patch.State);
    }

    /// <summary>Перестраивает страницу, когда плагин запустился или остановился.</summary>
    private void OnPluginState(HubEvent hubEvent)
    {
        var info = hubEvent.Payload.AsProto<PluginInfo>();
        if (info.Manifest.Id != _pluginId || info.State == _shownState) return;
        if (info.State is PluginState.Starting or PluginState.Stopping) return;
        _ = LoadSafelyAsync();
    }

    /// <summary>Шапка: иконка, имя, состояние, описание.</summary>
    private async Task<FrameworkElement> HeaderAsync(PluginInfo plugin)
    {
        var manifest = plugin.Manifest;
        FrameworkElement picture = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(10),
            Background = Fluent.Resource("AccentFillColorDefaultBrush"),
            Child = new FontIcon { Glyph = "", FontSize = 22, Foreground = Fluent.Resource("TextOnAccentFillColorPrimaryBrush") },
        };
        if (manifest.Icon.Length > 0 && await PluginIcons.LoadAsync(Hub, _pluginId) is BitmapImage image)
            picture = new Image { Source = image, Width = 48, Height = 48 };

        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        heading.Children.Add(Fluent.Text(manifest.Name.Length > 0 ? manifest.Name : manifest.Id, "TitleTextBlockStyle"));
        heading.Children.Add(Fluent.StateBadge(plugin.State));

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(heading);
        text.Children.Add(Fluent.Caption($"{manifest.Id} · {manifest.Version}" + (manifest.Author.Length > 0 ? $" · {manifest.Author}" : string.Empty)));
        if (manifest.Description.Length > 0) text.Children.Add(Fluent.Text(manifest.Description, secondary: true));

        return Fluent.Columns((picture, Fluent.Auto), (text, Fluent.Star));
    }

    /// <summary>Предупреждение, если плагин не работает.</summary>
    private InfoBar NotRunningBar(PluginInfo plugin)
    {
        var start = new Button { Content = Loc.T("Запустить", "Start") };
        start.Click += async (_, _) =>
        {
            if (await Hub.TryRunAsync("hub.plugins.restart", new PluginRef { Id = _pluginId }) is { } error) ShowError(_column, error);
        };

        return new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = plugin.State is PluginState.Faulted or PluginState.Blocked ? InfoBarSeverity.Error : InfoBarSeverity.Informational,
            Title = plugin.Error.Length > 0 ? plugin.Error : Loc.T("Плагин сейчас не запущен", "The plugin is not running"),
            Message = plugin.Manifest.Activation == "onDemand" ? Loc.T("Он запустится сам, когда кому-нибудь понадобится.", "It starts by itself when something needs it.") : string.Empty,
            ActionButton = plugin.Enabled ? start : null,
        };
    }

    /// <summary>Вкладки: страницы плагина и нативные страницы.</summary>
    private SelectorBar Tabs(PluginInfo plugin, UiResponse ui)
    {
        var bar = new SelectorBar();
        var views = new Dictionary<SelectorBarItem, Func<FrameworkElement>>();

        foreach (var page in ui.Pages)
        {
            var item = new SelectorBarItem { Text = page.Title, Icon = new FontIcon { Glyph = Fluent.Glyph(page.Icon) } };
            views[item] = () => RenderPage(page);
            bar.Items.Add(item);
        }

        if (ui.NativeAssembly.Length > 0 && plugin.Enabled) AddNativePages(bar, views, ui);

        bar.SelectionChanged += (_, _) =>
        {
            if (bar.SelectedItem is not { } item) return;
            _renderer = null;
            _host.Content = SafeView(views[item]);
        };
        if (bar.Items.Count > 0) bar.SelectedItem = bar.Items[0];
        return bar;
    }

    /// <summary>Добавляет вкладки из нативной UI-сборки.</summary>
    private void AddNativePages(SelectorBar bar, Dictionary<SelectorBarItem, Func<FrameworkElement>> views, UiResponse ui)
    {
        try
        {
            foreach (var page in NativeUiLoader.Load(ui.NativeAssembly, ui.NativeType))
            {
                var item = new SelectorBarItem { Text = page.Title, Icon = new FontIcon { Glyph = Fluent.Glyph(page.Icon) } };
                views[item] = () => page.CreateView(this);
                bar.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            App.Log($"Native UI of {_pluginId}: {ex}");
            ShowError(_column, Loc.T($"Не удалось загрузить интерфейс плагина: {ex.Message}", $"Could not load the plugin interface: {ex.Message}"));
        }
    }

    /// <summary>Создаёт вкладку, удерживая ошибку плагина внутри неё.</summary>
    private FrameworkElement SafeView(Func<FrameworkElement> create)
    {
        try
        {
            return create();
        }
        catch (Exception ex)
        {
            // Ошибка плагина остаётся на странице плагина.
            App.Log($"Page of {_pluginId}: {ex}");
            return new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Title = Loc.T("Страница плагина упала", "The plugin page crashed"), Message = ex.Message };
        }
    }

    /// <summary>Рисует декларативную страницу.</summary>
    private FrameworkElement RenderPage(UiPage page)
    {
        _renderer = new UiRenderer(Hub, _pluginId, page.Id);
        _renderer.ApplyState(_state);
        return _renderer.Render(page.Root);
    }

    /// <summary>Константы хаба. GUI не ссылается на ядро, поэтому повторяет их здесь.</summary>
    private static class Core
    {
        /// <summary>Топик изменения состояния интерфейса.</summary>
        public const string UiStateTopic = "hub.ui.state";
    }
}
