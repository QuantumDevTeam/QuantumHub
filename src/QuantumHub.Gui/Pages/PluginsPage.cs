using QuantumHub.Contracts.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuantumHub.Contracts.Proto;
using QuantumHub.Gui.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace QuantumHub.Gui.Pages;

/// <summary>Список плагинов: включение, установка, удаление.</summary>
public sealed class PluginsPage : HubPage
{
    private readonly MainWindow _window;
    private readonly StackPanel _body;
    /// <summary>Итог последней установки.</summary>
    private InfoBar? _notice;
    /// <summary>Карточки плагинов.</summary>
    private readonly StackPanel _list = new() { Spacing = 8 };

    /// <summary>Создаёт страницу.</summary>
    /// <param name="hub">Связь с хабом.</param>
    /// <param name="window">Окно, для навигации и диалога выбора файла.</param>
    public PluginsPage(HubConnection hub, MainWindow window) : base(hub)
    {
        _window = window;
        var (root, body) = Fluent.Page(Loc.T("Плагины", "Plugins"), Loc.T("Всё, что умеет хаб, умеют плагины. Чтобы добавить плагин, открой файл .qhp или перетащи его в это окно.", "Everything the hub can do comes from plugins. To add one, open a .qhp file or drop it into this window."));
        _body = body;

        var install = new Button { Content = Loc.T("Установить плагин…", "Install plugin…"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        install.Click += async (_, _) => await InstallAsync();
        _body.Children.Add(install);
        _body.Children.Add(_list);
        Content = root;

        // Прозрачный фон нужен, чтобы файл можно было бросить и на пустое место страницы.
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    /// <summary>Файл несут над страницей.</summary>
    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Loc.T("Установить плагин", "Install plugin");
    }

    /// <summary>Файл бросили на страницу.</summary>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        foreach (var item in await e.DataView.GetStorageItemsAsync())
        {
            if (item is StorageFile file) await InstallFileAsync(file.Path);
        }
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
            _list.Children.Add(Fluent.Text(Loc.T("Пока пусто. Установи первый плагин или положи его в папку plugins.", "Nothing here yet. Install a plugin or put it into the plugins folder."), secondary: true));
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

        var open = new Button { Content = Loc.T("Открыть", "Open") };
        open.Click += (_, _) => _window.OpenPlugin(manifest.Id);

        var more = new Button { Content = Fluent.Icon("") };
        var menu = new MenuFlyout();
        var restart = new MenuFlyoutItem { Text = Loc.T("Перезапустить", "Restart"), Icon = new FontIcon { Glyph = "" } };
        restart.Click += async (_, _) => await RunAsync("hub.plugins.restart", new PluginRef { Id = manifest.Id });
        var remove = new MenuFlyoutItem { Text = Loc.T("Удалить", "Remove"), Icon = new FontIcon { Glyph = "" } };
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
        if (!GuiSettings.Current.ConfirmRemove)
        {
            await RunAsync("hub.plugins.uninstall", new PluginRef { Id = manifest.Id });
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T($"Удалить «{manifest.Name}»?", $"Remove \"{manifest.Name}\"?"),
            Content = Loc.T("Настройки плагина останутся в папке пользователя.", "The plugin settings stay in your user folder."),
            PrimaryButtonText = Loc.T("Удалить", "Remove"),
            CloseButtonText = Loc.T("Отмена", "Cancel"),
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
        if (file is not null) await InstallFileAsync(file.Path);
    }

    /// <summary>Спрашивает и устанавливает. Файл пришёл из проводника, и пользователь мог открыть его случайно.</summary>
    /// <param name="path">Путь к файлу плагина.</param>
    public async Task ConfirmAndInstallAsync(string path)
    {
        if (!GuiSettings.Current.ConfirmInstall)
        {
            await InstallFileAsync(path);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = _window.Content.XamlRoot,
            Title = Loc.T($"Установить {Path.GetFileName(path)}?", $"Install {Path.GetFileName(path)}?"),
            Content = Loc.T("Плагин работает с теми же правами, что и QuantumHub. Ставь только то, чему доверяешь.", "A plugin runs with the same rights as QuantumHub. Install only what you trust."),
            PrimaryButtonText = Loc.T("Установить", "Install"),
            CloseButtonText = Loc.T("Отмена", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await InstallFileAsync(path);
    }

    /// <summary>Устанавливает файл и сообщает результат.</summary>
    /// <param name="path">Путь к файлу плагина.</param>
    private async Task InstallFileAsync(string path)
    {
        var (plugin, error) = await Hub.CallWithErrorAsync<PluginInfo>("hub.plugins.install", new InstallRequest { Path = path });
        if (plugin is null)
        {
            Notify(InfoBarSeverity.Error, Loc.T("Не получилось установить", "Could not install"), error ?? Path.GetFileName(path));
            return;
        }

        var name = plugin.Manifest.Name.Length > 0 ? plugin.Manifest.Name : plugin.Manifest.Id;
        if (plugin.Error.Length > 0) Notify(InfoBarSeverity.Warning, Loc.T($"«{name}» установлен, но не запустился", $"\"{name}\" is installed but did not start"), plugin.Error);
        else Notify(InfoBarSeverity.Success, Loc.T($"«{name}» {plugin.Manifest.Version} установлен", $"\"{name}\" {plugin.Manifest.Version} installed"), string.Empty);
    }

    /// <summary>Показывает итог установки вместо предыдущего.</summary>
    private void Notify(InfoBarSeverity severity, string title, string message)
    {
        if (_notice is not null) _body.Children.Remove(_notice);
        _notice = new InfoBar { IsOpen = true, Severity = severity, Title = title, Message = message };
        _body.Children.Insert(0, _notice);
    }
}
