using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;
using QuantumHub.Gui.Services;
using Windows.ApplicationModel.DataTransfer;

namespace QuantumHub.Gui.Pages;

/// <summary>Настройки самого приложения. Настройки плагинов живут на их страницах.</summary>
public sealed class SettingsPage : HubPage
{
    /// <summary>Значения настройки языка в порядке списка.</summary>
    private static readonly string[] Languages = [Loc.Auto, Loc.Russian, Loc.English];
    /// <summary>Значения уровня журнала хаба в порядке списка.</summary>
    private static readonly string[] LogLevels = ["debug", "info", "warning", "error"];

    private readonly MainWindow _window;
    private readonly StackPanel _body;
    /// <summary>Колонка страницы: её ширина меняется сразу, без перестройки.</summary>
    private readonly FrameworkElement _column;
    /// <summary>Заголовки разделов и их карточки: по ним работает поиск.</summary>
    private readonly List<(TextBlock Heading, List<(Border Card, string Text)> Cards)> _sections = [];

    private readonly ComboBox _hubLogLevel = new() { MinWidth = 160, IsEnabled = false };
    private readonly Slider _hubLogDays = new() { Minimum = 0, Maximum = 90, MinWidth = 200, IsEnabled = false };
    private readonly TextBlock _hubLogDaysValue = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 72, TextAlignment = TextAlignment.Right };
    /// <summary>Предложение перезапустить окно.</summary>
    private InfoBar? _themeBar;
    /// <summary>Значения хаба загружаются: это не действие пользователя.</summary>
    private bool _loadingHub;

    /// <summary>Создаёт страницу.</summary>
    /// <param name="hub">Связь с хабом.</param>
    /// <param name="window">Окно.</param>
    public SettingsPage(HubConnection hub, MainWindow window) : base(hub)
    {
        _window = window;
        var (root, body) = Fluent.Page(Loc.T("Настройки", "Settings"));
        _body = body;
        _column = (FrameworkElement)((Grid)root.Content).Children[0];
        Content = root;

        var search = new TextBox { PlaceholderText = Loc.T("Найти настройку", "Find a setting") };
        search.TextChanged += (_, _) => Filter(search.Text);
        _body.Children.Add(search);

        AddGeneral();
        AddAppearance();
        AddNavigation();
        AddWindow();
        AddConfirmations();
        AddHub();
        AddLog();
        AddMaintenance();
        AddAbout();
        ShowThemeRestart();
    }

    /// <inheritdoc/>
    protected override async Task LoadAsync()
    {
        var level = await Hub.TryCallAsync<ConfigValue>("hub.config.get", new ConfigGetRequest { PluginId = "hub", Key = "logLevel" });
        var days = await Hub.TryCallAsync<ConfigValue>("hub.config.get", new ConfigGetRequest { PluginId = "hub", Key = "logRetentionDays" });
        if (level is null || days is null) return;

        _loadingHub = true;
        var index = Array.IndexOf(LogLevels, level.Json.Trim('"'));
        _hubLogLevel.SelectedIndex = index >= 0 ? index : 0;
        _hubLogDays.Value = double.TryParse(days.Json, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
        ShowDays();
        _loadingHub = false;
        _hubLogLevel.IsEnabled = _hubLogDays.IsEnabled = true;
    }

    private void AddGeneral()
    {
        Section(Loc.T("Основные", "General"));

        var language = new ComboBox { MinWidth = 160 };
        language.Items.Add(Loc.T("Как в системе", "System default"));
        language.Items.Add("Русский");
        language.Items.Add("English");
        language.SelectedIndex = Array.IndexOf(Languages, Hub.LanguageSetting) is var index and >= 0 ? index : 0;
        language.SelectionChanged += async (_, _) =>
        {
            if (language.SelectedIndex >= 0) await Hub.SetLanguageSettingAsync(Languages[language.SelectedIndex]);
        };
        Add(Loc.T("Язык", "Language"), Loc.T("Для окна и плагинов.", "For this window and the plugins."), language);

        var service = HubConnection.FindService();
        var gui = Environment.ProcessPath;
        Autostart(Loc.T("Запускать хаб при входе в Windows", "Start the hub when Windows starts"),
            Loc.T("Плагины будут работать сразу, без открытия этого окна.", "Plugins work right away, without opening this window."),
            Services.Autostart.HubValue, service is null ? null : $"\"{service}\"");
        Autostart(Loc.T("Показывать значок в трее при входе", "Show the tray icon when Windows starts"),
            Loc.T("Интерфейс стартует свёрнутым.", "The interface starts minimized."),
            Services.Autostart.GuiValue, gui is null ? null : $"\"{gui}\" --minimized");
    }

    private void AddAppearance()
    {
        Section(Loc.T("Внешний вид", "Appearance"));
        var s = GuiSettings.Current;

        Choice(Loc.T("Тема", "Theme"), Loc.T("Применяется после перезапуска окна. Хаб и плагины продолжат работать.", "Applies after restarting the window. The hub and plugins keep running."), s.Theme, [
            (GuiSettings.System, Loc.T("Как в системе", "System default")),
            ("light", Loc.T("Светлая", "Light")),
            ("dark", Loc.T("Тёмная", "Dark"))], value => x => x with { Theme = value }, ShowThemeRestart);
        Choice(Loc.T("Фон окна", "Window background"), Loc.T("Mica и Acrylic просвечивают обои.", "Mica and Acrylic let the wallpaper show through."), s.Backdrop, [
            ("mica", "Mica"),
            ("micaAlt", "Mica Alt"),
            ("acrylic", "Acrylic"),
            ("none", Loc.T("Сплошной", "Solid"))], value => x => x with { Backdrop = value });
        Toggle(Loc.T("Анимация карточек", "Card animation"), Loc.T("Плавное появление на страницах.", "Smooth entrance on pages."),
            s.Animations, value => x => x with { Animations = value });
        Choice(Loc.T("Плотность", "Density"), Loc.T("Компактная вмещает больше на экран. Действует на страницах, открытых после смены.", "Compact fits more on the screen. Applies to pages opened after the change."), s.Density, [
            ("normal", Loc.T("Обычная", "Normal")),
            ("compact", Loc.T("Компактная", "Compact"))], value => x => x with { Density = value });
        Choice(Loc.T("Ширина страниц", "Page width"), Loc.T("Для широких мониторов.", "For wide monitors."), s.PageWidth, [
            ("normal", Loc.T("Обычная", "Normal")),
            ("wide", Loc.T("Широкая", "Wide")),
            ("full", Loc.T("Во всё окно", "Full window"))], value => x => x with { PageWidth = value }, () => _column.MaxWidth = Fluent.PageMaxWidth);
        Toggle(Loc.T("Состояние связи в заголовке", "Connection status in the title bar"), null,
            s.ShowStatus, value => x => x with { ShowStatus = value });
        Choice(Loc.T("Формат времени", "Time format"), Loc.T("В журнале и подключениях.", "In the log and connections."), s.Clock, [
            ("24", Loc.T("24 часа", "24 hours")),
            ("12", Loc.T("12 часов", "12 hours"))], value => x => x with { Clock = value });
    }

    private void AddNavigation()
    {
        Section(Loc.T("Меню и страницы", "Menu and pages"));
        var s = GuiSettings.Current;

        Choice(Loc.T("Страница при запуске", "Start page"), null, s.StartPage, [
            ("overview", Loc.T("Обзор", "Overview")),
            ("plugins", Loc.T("Плагины", "Plugins")),
            ("sessions", Loc.T("Подключения", "Connections")),
            ("logs", Loc.T("Журнал", "Log")),
            (GuiSettings.LastPageValue, Loc.T("Последняя открытая", "Last opened"))], value => x => x with { StartPage = value });
        Choice(Loc.T("Меню при запуске", "Menu at start"), null, s.PaneMode, [
            ("open", Loc.T("Развёрнуто", "Expanded")),
            ("closed", Loc.T("Свёрнуто", "Collapsed")),
            (GuiSettings.Remember, Loc.T("Как в прошлый раз", "As last time"))], value => x => x with { PaneMode = value });
        Choice(Loc.T("Плагины в меню", "Plugins in the menu"), null, s.MenuPlugins, [
            ("enabled", Loc.T("Только включённые", "Enabled only")),
            ("all", Loc.T("Все", "All")),
            ("none", Loc.T("Не показывать", "Hide"))], value => x => x with { MenuPlugins = value });
        Toggle(Loc.T("Отмечать плагины с ошибкой", "Mark plugins with errors"), Loc.T("Красная метка в меню.", "A red badge in the menu."),
            s.ProblemBadges, value => x => x with { ProblemBadges = value });
        Toggle(Loc.T("Пути на странице «Обзор»", "Paths on the Overview page"), Loc.T("Папки программы и данных.", "Program and data folders."),
            s.OverviewPaths, value => x => x with { OverviewPaths = value });
    }

    private void AddWindow()
    {
        Section(Loc.T("Окно и трей", "Window and tray"));
        var s = GuiSettings.Current;

        Choice(Loc.T("Крестик окна", "Window close button"), Loc.T("Хаб продолжит работать в любом случае.", "The hub keeps running either way."), s.CloseAction, [
            ("tray", Loc.T("Прятать в трей", "Hide to tray")),
            ("exit", Loc.T("Закрывать интерфейс", "Close the interface"))], value => x => x with { CloseAction = value });
        Toggle(Loc.T("Сворачивать в трей", "Minimize to tray"), Loc.T("Окно не занимает место на панели задач.", "The window leaves the taskbar."),
            s.MinimizeToTray, value => x => x with { MinimizeToTray = value });
        Toggle(Loc.T("Поверх всех окон", "Always on top"), null, s.AlwaysOnTop, value => x => x with { AlwaysOnTop = value });
        Toggle(Loc.T("Запоминать размер и место окна", "Remember window size and position"), null,
            s.RememberWindow, value => x => x with { RememberWindow = value });
        Toggle(Loc.T("Открывать двойным щелчком по значку", "Open with a double click on the tray icon"), Loc.T("Иначе хватит одного щелчка.", "Otherwise a single click is enough."),
            s.TrayDoubleClick, value => x => x with { TrayDoubleClick = value });
    }

    private void AddConfirmations()
    {
        Section(Loc.T("Подтверждения", "Confirmations"));
        var s = GuiSettings.Current;

        Toggle(Loc.T("Перед удалением плагина", "Before removing a plugin"), null, s.ConfirmRemove, value => x => x with { ConfirmRemove = value });
        Toggle(Loc.T("Перед установкой файла из проводника", "Before installing a file from Explorer"),
            Loc.T("Без вопроса случайный двойной щелчок запустит код плагина.", "Without it, an accidental double click runs the plugin code."),
            s.ConfirmInstall, value => x => x with { ConfirmInstall = value });
        Toggle(Loc.T("Перед завершением хаба", "Before stopping the hub"), null, s.ConfirmStopHub, value => x => x with { ConfirmStopHub = value });
    }

    private void AddHub()
    {
        Section(Loc.T("Хаб", "Hub"));
        var s = GuiSettings.Current;

        Toggle(Loc.T("Запускать хаб вместе с окном", "Start the hub with this window"), Loc.T("Иначе он запускается кнопкой на странице «Обзор».", "Otherwise use the button on the Overview page."),
            s.StartHub, value => x => x with { StartHub = value });
        Toggle(Loc.T("Поднимать хаб после сбоя", "Restart the hub after a crash"), Loc.T("Хаб, завершённый вручную, останется выключенным.", "A hub stopped by hand stays off."),
            s.RestartHub, value => x => x with { RestartHub = value });

        foreach (var (label, value) in new[]
                 {
                     (Loc.T("Всё, с отладкой", "Everything, with debug"), LogLevels[0]),
                     (Loc.T("Обычные записи", "Regular entries"), LogLevels[1]),
                     (Loc.T("Предупреждения и ошибки", "Warnings and errors"), LogLevels[2]),
                     (Loc.T("Только ошибки", "Errors only"), LogLevels[3]),
                 })
        {
            _ = value;
            _hubLogLevel.Items.Add(label);
        }

        _hubLogLevel.SelectionChanged += async (_, _) =>
        {
            if (!_loadingHub && _hubLogLevel.SelectedIndex >= 0) await SetHubAsync("logLevel", $"\"{LogLevels[_hubLogLevel.SelectedIndex]}\"");
        };
        Add(Loc.T("Что записывать в журнал", "What to write to the log"), Loc.T("Хранится в хабе, действует сразу.", "Stored in the hub, applies right away."), _hubLogLevel);

        ShowDays();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.IsRepeating = false;
        timer.Tick += async (_, _) => await SetHubAsync("logRetentionDays", ((int)_hubLogDays.Value).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _hubLogDays.ValueChanged += (_, _) =>
        {
            ShowDays();
            if (!_loadingHub) timer.Start();
        };
        var days = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        days.Children.Add(_hubLogDaysValue);
        days.Children.Add(_hubLogDays);
        Add(Loc.T("Хранить файлы журнала", "Keep log files"), Loc.T("Старые файлы удаляются при запуске хаба и при смене значения.", "Old files are deleted when the hub starts and when this changes."), days);

        var restart = new Button { Content = Loc.T("Перезапустить", "Restart") };
        restart.Click += async (_, _) =>
        {
            if (await ConfirmAsync(Loc.T("Перезапустить хаб?", "Restart the hub?"), Loc.T("Все плагины запустятся заново.", "All plugins start again."), restart.Content as string)) await Hub.RestartHubAsync();
        };
        Add(Loc.T("Перезапустить хаб", "Restart the hub"), Loc.T("Все плагины запустятся заново.", "All plugins start again."), restart);

        var stop = new Button { Content = Loc.T("Завершить", "Stop") };
        stop.Click += async (_, _) =>
        {
            if (await ConfirmAsync(Loc.T("Завершить хаб?", "Stop the hub?"), Loc.T("Все плагины остановятся.", "All plugins stop."), stop.Content as string)) await Hub.ShutdownHubAsync();
        };
        Add(Loc.T("Завершить работу хаба", "Stop the hub"), Loc.T("Все плагины остановятся. Запустить снова можно на странице «Обзор».", "All plugins stop. You can start it again on the Overview page."), stop);
    }

    private void AddLog()
    {
        Section(Loc.T("Журнал на экране", "Log on screen"));
        var s = GuiSettings.Current;

        Choice(Loc.T("Какие записи показывать", "Which entries to show"), null, s.LogFilter, [
            ("debug", Loc.T("Все", "All")),
            ("info", Loc.T("Без отладки", "No debug")),
            ("warning", Loc.T("Предупреждения и ошибки", "Warnings and errors")),
            ("error", Loc.T("Только ошибки", "Errors only"))], value => x => x with { LogFilter = value });
        Number(Loc.T("Сколько строк показывать", "How many lines to show"), null, s.LogRows, GuiSettings.MinLogRows, GuiSettings.MaxLogRows, 100, value => x => x with { LogRows = value });
        Number(Loc.T("Размер шрифта", "Font size"), null, s.LogFont, GuiSettings.MinLogFont, GuiSettings.MaxLogFont, 1, value => x => x with { LogFont = value });
        Toggle(Loc.T("Показывать источник", "Show the source"), Loc.T("Какой плагин написал строку.", "Which plugin wrote the line."), s.LogSource, value => x => x with { LogSource = value });
        Toggle(Loc.T("Переносить длинные строки", "Wrap long lines"), null, s.LogWrap, value => x => x with { LogWrap = value });
    }

    private void AddMaintenance()
    {
        Section(Loc.T("Обслуживание", "Maintenance"));

        var folders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        folders.Children.Add(Folder(Loc.T("Данные", "Data"), status => status.DataDir));
        folders.Children.Add(Folder(Loc.T("Журналы", "Logs"), status => Path.Combine(status.DataDir, "logs")));
        folders.Children.Add(Folder(Loc.T("Программа", "Program"), status => status.RootDir));
        Add(Loc.T("Открыть папку", "Open a folder"), null, folders);

        var copy = new Button { Content = Loc.T("Скопировать", "Copy") };
        copy.Click += async (_, _) => await CopyDiagnosticsAsync(copy);
        Add(Loc.T("Сведения для отчёта об ошибке", "Details for a bug report"), Loc.T("Версия, пути и состояние плагинов. Без настроек и токенов.", "Version, paths and plugin states. No settings or tokens."), copy);

        var size = new Button { Content = Loc.T("Сбросить", "Reset") };
        size.Click += (_, _) => _window.ResetPlacement();
        Add(Loc.T("Размер окна", "Window size"), Loc.T("Вернуть размер и место по умолчанию.", "Back to the default size and position."), size);

        var reset = new Button { Content = Loc.T("Сбросить", "Reset") };
        reset.Click += async (_, _) =>
        {
            if (!await AskAsync(Loc.T("Сбросить настройки окна?", "Reset the window settings?"),
                    Loc.T("Настройки плагинов, хаба и автозапуска останутся.", "Plugin, hub and autostart settings stay."), reset.Content as string)) return;
            GuiSettings.Update(_ => new GuiSettings());
            _window.Reopen("settings");
        };
        Add(Loc.T("Настройки окна", "Window settings"), Loc.T("Вернуть всё на этой странице к значениям по умолчанию.", "Return everything on this page to defaults."), reset);
    }

    private void AddAbout()
    {
        Section(Loc.T("О программе", "About"));

        var about = new StackPanel { Spacing = 2 };
        var version = typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        about.Children.Add(Fluent.Text($"QuantumHub {version}"));
        about.Children.Add(Fluent.Caption("Copyright (c) 2026 QuantumDevTeam."));
        about.Children.Add(Fluent.Caption(Loc.T("Лицензия PolyForm Noncommercial 1.0.0: только для некоммерческого использования.", "PolyForm Noncommercial 1.0.0 license: noncommercial use only.")));
        about.Children.Add(new HyperlinkButton
        {
            Content = Loc.T("Текст лицензии", "License text"),
            NavigateUri = new Uri("https://polyformproject.org/licenses/noncommercial/1.0.0"),
            Padding = new Thickness(0, 4, 0, 0),
        });
        AddCard(Fluent.Card(about), "about version license");
    }

    /// <summary>Предлагает перезапустить окно, если выбранная тема ещё не действует.</summary>
    private void ShowThemeRestart()
    {
        if (_themeBar is not null) _body.Children.Remove(_themeBar);
        _themeBar = null;
        if (GuiSettings.Current.Theme == App.ThemeAtStart) return;

        var restart = new Button { Content = Loc.T("Перезапустить окно", "Restart the window") };
        restart.Click += (_, _) => ((App)Application.Current).RestartInterface("settings");
        _themeBar = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = Loc.T("Тема сменится после перезапуска окна", "The theme changes after restarting the window"),
            ActionButton = restart,
        };
        _body.Children.Insert(1, _themeBar);
    }

    /// <summary>Подпись срока хранения журнала.</summary>
    private void ShowDays() => _hubLogDaysValue.Text = _hubLogDays.Value == 0
        ? Loc.T("всегда", "forever")
        : Loc.T($"{(int)_hubLogDays.Value} дн.", $"{(int)_hubLogDays.Value} days");

    /// <summary>Меняет настройку хаба.</summary>
    private async Task SetHubAsync(string key, string json)
    {
        if (await Hub.TryRunAsync("hub.config.set", new ConfigSetRequest { PluginId = "hub", Key = key, Json = json }) is { } error) ShowError(_body, error);
    }

    /// <summary>Спрашивает подтверждение, если оно включено.</summary>
    private Task<bool> ConfirmAsync(string title, string message, string? action) =>
        GuiSettings.Current.ConfirmStopHub ? AskAsync(title, message, action) : Task.FromResult(true);

    /// <summary>Спрашивает подтверждение.</summary>
    private async Task<bool> AskAsync(string title, string message, string? action)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = action ?? "OK",
            CloseButtonText = Loc.T("Отмена", "Cancel"),
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Кнопка открытия папки. Пути знает хаб.</summary>
    private Button Folder(string label, Func<HubStatus, string> path)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) =>
        {
            var status = await Hub.TryCallAsync<HubStatus>("hub.status", new PluginRef());
            if (status is null)
            {
                ShowError(_body, Hub.ServiceProblem ?? Loc.T("Нет связи с хабом.", "No connection to the hub."));
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path(status)}\"") { UseShellExecute = true })?.Dispose();
        };
        return button;
    }

    /// <summary>Копирует сведения о программе. Значения настроек сюда не попадают: в них бывают токены.</summary>
    private async Task CopyDiagnosticsAsync(Button button)
    {
        var text = new StringBuilder();
        text.AppendLine($"QuantumHub GUI {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3)}");
        text.AppendLine($"Windows {Environment.OSVersion.Version}, .NET {Environment.Version}, language {Loc.Language}");

        if (await Hub.TryCallAsync<HubStatus>("hub.status", new PluginRef()) is { } status)
        {
            text.AppendLine($"Hub {status.Version}, transports: {string.Join(", ", status.Transports)}");
            text.AppendLine($"Program: {status.RootDir}");
            text.AppendLine($"Data: {status.DataDir}");
        }
        else
        {
            text.AppendLine($"Hub: not connected. {Hub.ServiceProblem}");
        }

        if (await Hub.TryCallAsync<PluginList>("hub.plugins.list", new PluginRef()) is { } list)
        {
            foreach (var plugin in list.Plugins)
                text.AppendLine($"{plugin.Manifest.Id} {plugin.Manifest.Version}: {plugin.State}{(plugin.Error.Length > 0 ? $" ({plugin.Error})" : string.Empty)}");
        }

        var package = new DataPackage();
        package.SetText(text.ToString());
        Clipboard.SetContent(package);
        button.Content = Loc.T("Скопировано", "Copied");
    }

    /// <summary>Показывает только подходящие настройки.</summary>
    private void Filter(string query)
    {
        var words = query.Trim();
        foreach (var (heading, cards) in _sections)
        {
            var any = false;
            foreach (var (card, text) in cards)
            {
                var match = words.Length == 0 || text.Contains(words, StringComparison.CurrentCultureIgnoreCase);
                card.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                any |= match;
            }

            heading.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Начинает раздел.</summary>
    private void Section(string title)
    {
        var heading = Fluent.Text(title, "BodyStrongTextBlockStyle");
        heading.Margin = new Thickness(0, 16, 0, 0);
        _body.Children.Add(heading);
        _sections.Add((heading, []));
    }

    /// <summary>Добавляет карточку в текущий раздел.</summary>
    private void AddCard(Border card, string text)
    {
        _body.Children.Add(card);
        _sections[^1].Cards.Add((card, $"{_sections[^1].Heading.Text} {text}"));
    }

    /// <summary>Карточка настройки.</summary>
    private void Add(string title, string? description, FrameworkElement control)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Fluent.Text(title));
        if (description is not null) text.Children.Add(Fluent.Caption(description));
        control.VerticalAlignment = VerticalAlignment.Center;
        AddCard(Fluent.Card(Fluent.Columns((text, Fluent.Star), (control, Fluent.Auto))), $"{title} {description}");
    }

    /// <summary>Переключатель автозапуска.</summary>
    private void Autostart(string title, string description, string valueName, string? command)
    {
        var toggle = NewToggle(Services.Autostart.IsEnabled(valueName));
        toggle.IsEnabled = command is not null;
        toggle.Toggled += (_, _) => Services.Autostart.Set(valueName, toggle.IsOn ? command : null);
        Add(title, description, toggle);
    }

    /// <summary>Переключатель настройки окна.</summary>
    private void Toggle(string title, string? description, bool value, Func<bool, Func<GuiSettings, GuiSettings>> change)
    {
        var toggle = NewToggle(value);
        toggle.Toggled += (_, _) => GuiSettings.Update(change(toggle.IsOn));
        Add(title, description, toggle);
    }

    /// <summary>Заготовка переключателя.</summary>
    private static ToggleSwitch NewToggle(bool value) =>
        new() { IsOn = value, OnContent = Loc.T("Вкл", "On"), OffContent = Loc.T("Выкл", "Off") };

    /// <summary>Выбор из списка.</summary>
    private void Choice(string title, string? description, string value, (string Value, string Label)[] options,
        Func<string, Func<GuiSettings, GuiSettings>> change, Action? applied = null)
    {
        var box = new ComboBox { MinWidth = 160 };
        foreach (var (_, label) in options) box.Items.Add(label);
        box.SelectedIndex = Array.FindIndex(options, option => option.Value == value);
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            GuiSettings.Update(change(options[box.SelectedIndex].Value));
            applied?.Invoke();
        };
        Add(title, description, box);
    }

    /// <summary>Поле для числа.</summary>
    private void Number(string title, string? description, int value, int min, int max, int step, Func<int, Func<GuiSettings, GuiSettings>> change)
    {
        var box = new NumberBox
        {
            Value = value,
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            MinWidth = 120,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        box.ValueChanged += (_, args) =>
        {
            if (!double.IsNaN(args.NewValue)) GuiSettings.Update(change((int)args.NewValue));
        };
        Add(title, description, box);
    }
}
