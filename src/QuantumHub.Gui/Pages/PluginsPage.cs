using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuantumHub.Contracts.Proto;
using QuantumHub.Gui.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace QuantumHub.Gui.Pages;

/// <summary>Список плагинов: включение, установка, удаление.</summary>
public sealed class PluginsPage : HubPage
{
    private readonly MainWindow _window;
    private readonly StackPanel _body;
    /// <summary>Карточки плагинов.</summary>
    private readonly StackPanel _list = new() { Spacing = 8 };

    /// <summary>Создаёт страницу.</summary>
    /// <param name="hub">Связь с хабом.</param>
    /// <param name="window">Окно, для навигации и диалога выбора файла.</param>
    public PluginsPage(HubConnection hub, MainWindow window) : base(hub)
    {
        _window = window;
        var (root, body) = Fluent.Page("Плагины", "Всё, что умеет хаб, умеют плагины. Без них он не делает ничего.");
        _body = body;

        var install = new Button { Content = "Установить плагин…", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        install.Click += async (_, _) => await InstallAsync();
        _body.Children.Add(install);
        _body.Children.Add(_list);
        Content = root;
    }

    /// <inheritdoc/>
    protected override async Task LoadAsync()
    {
        await RefreshAsync();
        await SubscribeAsync("hub.plugin.state", e => { _ = RefreshAsync(); });
        await SubscribeAsync("hub.plugin.removed", e => { _ = RefreshAsync(); });
    }

    /// <summary>Перестраивает список.</summary>
    private async Task RefreshAsync()
    {
        var list = await Hub.TryCallAsync<PluginList>("hub.plugins.list", new PluginRef());
        if (list is null) return;

        _list.Children.Clear();
        foreach (var plugin in list.Plugins) _list.Children.Add(Row(plugin));
        if (list.Plugins.Count == 0)
            _list.Children.Add(Fluent.Text("Пока пусто. Установи первый плагин или положи его в папку plugins.", secondary: true));
    }

    /// <summary>Карточка плагина.</summary>
    private Border Row(PluginInfo plugin)
    {
        var manifest = plugin.Manifest;
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(Fluent.Text(manifest.Name.Length > 0 ? manifest.Name : manifest.Id, "BodyStrongTextBlockStyle"));
        heading.Children.Add(Fluent.Caption(manifest.Version));
        heading.Children.Add(Fluent.StateBadge(plugin.State));

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(heading);
        if (manifest.Description.Length > 0) text.Children.Add(Fluent.Caption(manifest.Description));
        if (plugin.Error.Length > 0)
        {
            var error = Fluent.Caption(plugin.Error);
            error.Foreground = Fluent.Resource("SystemFillColorCriticalBrush");
            text.Children.Add(error);
        }

        var open = new Button { Content = "Открыть" };
        open.Click += (_, _) => _window.OpenPlugin(manifest.Id);

        var more = new Button { Content = Fluent.Icon("") };
        var menu = new MenuFlyout();
        var restart = new MenuFlyoutItem { Text = "Перезапустить", Icon = new FontIcon { Glyph = "" } };
        restart.Click += async (_, _) => await RunAsync("hub.plugins.restart", new PluginRef { Id = manifest.Id });
        var remove = new MenuFlyoutItem { Text = "Удалить", Icon = new FontIcon { Glyph = "" } };
        remove.Click += async (_, _) => await UninstallAsync(manifest);
        menu.Items.Add(restart);
        menu.Items.Add(remove);
        more.Flyout = menu;

        var toggle = new ToggleSwitch { IsOn = plugin.Enabled, OnContent = string.Empty, OffContent = string.Empty, MinWidth = 0 };
        toggle.Toggled += async (_, _) =>
            await RunAsync("hub.plugins.enable", new SetEnabledRequest { Id = manifest.Id, Enabled = toggle.IsOn });

        return Fluent.Card(Fluent.Columns((text, Fluent.Star), (open, Fluent.Auto), (more, Fluent.Auto), (toggle, Fluent.Auto)));
    }

    /// <summary>Выполняет команду, показывая ошибку на странице.</summary>
    private async Task RunAsync(string method, Google.Protobuf.IMessage request)
    {
        if (await Hub.TryRunAsync(method, request) is { } error) ShowError(_body, error);
    }

    /// <summary>Удаляет плагин после подтверждения.</summary>
    private async Task UninstallAsync(PluginManifest manifest)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Удалить «{manifest.Name}»?",
            Content = "Настройки плагина останутся в папке пользователя.",
            PrimaryButtonText = "Удалить",
            CloseButtonText = "Отмена",
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await RunAsync("hub.plugins.uninstall", new PluginRef { Id = manifest.Id });
    }

    /// <summary>Выбор файла и установка.</summary>
    private async Task InstallAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _window.Hwnd);
        foreach (var extension in new[] { ".qhp", ".zip", ".dll" }) picker.FileTypeFilter.Add(extension);

        var file = await picker.PickSingleFileAsync();
        if (file is not null) await RunAsync("hub.plugins.install", new InstallRequest { Path = file.Path });
    }
}
