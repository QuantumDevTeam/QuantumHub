using System.Text.Json;

namespace QuantumHub.Gui.Services;

/// <summary>
/// Настройки окна. Хранятся отдельно от конфига хаба: это вкусы одного интерфейса, и они нужны ещё до подключения к хабу.
/// Файл не зависит от WinUI, поэтому проверяется тестами.
/// </summary>
public sealed record GuiSettings
{
    /// <summary>Значение настройки: как в системе.</summary>
    public const string System = "system";
    /// <summary>Значение настройки: открыть последнюю страницу.</summary>
    public const string LastPageValue = "last";
    /// <summary>Значение настройки: запомнить как было.</summary>
    public const string Remember = "remember";

    /// <summary>Наименьшее число строк журнала.</summary>
    public const int MinLogRows = 100;
    /// <summary>Наибольшее число строк журнала: больше хаб в памяти не держит.</summary>
    public const int MaxLogRows = 2000;
    /// <summary>Наименьший размер шрифта журнала.</summary>
    public const int MinLogFont = 10;
    /// <summary>Наибольший размер шрифта журнала.</summary>
    public const int MaxLogFont = 18;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Тема: "system", "light" или "dark".</summary>
    public string Theme { get; init; } = System;
    /// <summary>Фон окна: "mica", "micaAlt", "acrylic" или "none".</summary>
    public string Backdrop { get; init; } = "mica";
    /// <summary>Плавное появление карточек.</summary>
    public bool Animations { get; init; } = true;
    /// <summary>Плотность: "normal" или "compact".</summary>
    public string Density { get; init; } = "normal";
    /// <summary>Ширина страниц: "normal", "wide" или "full".</summary>
    public string PageWidth { get; init; } = "normal";
    /// <summary>Показывать состояние связи в заголовке.</summary>
    public bool ShowStatus { get; init; } = true;
    /// <summary>Часы: "24" или "12".</summary>
    public string Clock { get; init; } = "24";

    /// <summary>Какую страницу открывать: "overview", "plugins", "sessions", "logs" или "last".</summary>
    public string StartPage { get; init; } = "overview";
    /// <summary>Последняя открытая страница.</summary>
    public string LastPage { get; init; } = "overview";
    /// <summary>Меню при запуске: "open", "closed" или "remember".</summary>
    public string PaneMode { get; init; } = "open";
    /// <summary>Было ли меню открыто в прошлый раз.</summary>
    public bool PaneWasOpen { get; init; } = true;
    /// <summary>Какие плагины показывать в меню: "enabled", "all" или "none".</summary>
    public string MenuPlugins { get; init; } = "enabled";
    /// <summary>Отмечать плагины с ошибкой в меню.</summary>
    public bool ProblemBadges { get; init; } = true;
    /// <summary>Показывать пути на странице «Обзор».</summary>
    public bool OverviewPaths { get; init; } = true;

    /// <summary>Что делает крестик: "tray" или "exit".</summary>
    public string CloseAction { get; init; } = "tray";
    /// <summary>Сворачивать в трей вместо панели задач.</summary>
    public bool MinimizeToTray { get; init; }
    /// <summary>Поверх всех окон.</summary>
    public bool AlwaysOnTop { get; init; }
    /// <summary>Запоминать размер и место окна.</summary>
    public bool RememberWindow { get; init; } = true;
    /// <summary>Открывать окно двойным щелчком по значку, а не одним.</summary>
    public bool TrayDoubleClick { get; init; }

    /// <summary>Спрашивать перед удалением плагина.</summary>
    public bool ConfirmRemove { get; init; } = true;
    /// <summary>Спрашивать перед установкой файла из проводника.</summary>
    public bool ConfirmInstall { get; init; } = true;
    /// <summary>Спрашивать перед завершением хаба.</summary>
    public bool ConfirmStopHub { get; init; } = true;

    /// <summary>Запускать хаб вместе с окном.</summary>
    public bool StartHub { get; init; } = true;
    /// <summary>Поднимать упавший хаб.</summary>
    public bool RestartHub { get; init; } = true;

    /// <summary>Проверять новые версии на GitHub.</summary>
    public bool CheckUpdates { get; init; } = true;
    /// <summary>Разрешать ссылкам quantumhub:// запускать действия плагинов.</summary>
    public bool LinkActions { get; init; } = true;
    /// <summary>Действия, про которые уже не надо спрашивать: "плагин/действие".</summary>
    public string[] TrustedLinks { get; init; } = [];

    /// <summary>Сколько строк журнала показывать.</summary>
    public int LogRows { get; init; } = 500;
    /// <summary>Записи какого уровня показывать: "debug", "info", "warning" или "error".</summary>
    public string LogFilter { get; init; } = "debug";
    /// <summary>Показывать источник записи.</summary>
    public bool LogSource { get; init; } = true;
    /// <summary>Переносить длинные строки.</summary>
    public bool LogWrap { get; init; } = true;
    /// <summary>Размер шрифта журнала.</summary>
    public int LogFont { get; init; } = 12;

    /// <summary>Текущие настройки.</summary>
    public static GuiSettings Current { get; private set; } = new();

    /// <summary>Настройки изменились.</summary>
    public static event Action? Changed;

    private static string? _path;

    /// <summary>Загружает настройки экземпляра.</summary>
    /// <param name="instance">Экземпляр хаба: у каждого свои настройки окна.</param>
    public static void Init(string instance)
    {
        _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuantumHub", $"gui.{instance}.json");
        Current = Load(_path);
    }

    /// <summary>Меняет настройки и сохраняет их.</summary>
    /// <param name="change">Что поменять.</param>
    public static void Update(Func<GuiSettings, GuiSettings> change)
    {
        Current = change(Current).Normalized();
        if (_path is not null) Current.Save(_path);
        Changed?.Invoke();
    }

    /// <summary>Сохраняет то, что программа запоминает сама: окну от этого перестраиваться незачем.</summary>
    /// <param name="change">Что поменять.</param>
    public static void Note(Func<GuiSettings, GuiSettings> change)
    {
        Current = change(Current).Normalized();
        if (_path is not null) Current.Save(_path);
    }

    /// <summary>Читает файл. Испорченный или отсутствующий файл значит настройки по умолчанию: окно должно открыться в любом случае.</summary>
    /// <param name="path">Путь к файлу.</param>
    public static GuiSettings Load(string path)
    {
        try
        {
            return (JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(path)) ?? new GuiSettings()).Normalized();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new GuiSettings();
        }
    }

    /// <summary>Записывает файл. Запись через временный файл: при сбое старые настройки останутся целы.</summary>
    /// <param name="path">Путь к файлу.</param>
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Настройка действует до закрытия окна, потерять её не страшно.
        }
    }

    /// <summary>Исправляет значения, которых не может быть: файл могли править руками.</summary>
    public GuiSettings Normalized()
    {
        var defaults = new GuiSettings();
        return this with
        {
            Theme = OneOf(Theme, defaults.Theme, System, "light", "dark"),
            Backdrop = OneOf(Backdrop, defaults.Backdrop, "mica", "micaAlt", "acrylic", "none"),
            Density = OneOf(Density, defaults.Density, "normal", "compact"),
            PageWidth = OneOf(PageWidth, defaults.PageWidth, "normal", "wide", "full"),
            Clock = OneOf(Clock, defaults.Clock, "24", "12"),
            StartPage = OneOf(StartPage, defaults.StartPage, "overview", "plugins", "sessions", "logs", LastPageValue),
            PaneMode = OneOf(PaneMode, defaults.PaneMode, "open", "closed", Remember),
            MenuPlugins = OneOf(MenuPlugins, defaults.MenuPlugins, "enabled", "all", "none"),
            CloseAction = OneOf(CloseAction, defaults.CloseAction, "tray", "exit"),
            LogFilter = OneOf(LogFilter, defaults.LogFilter, "debug", "info", "warning", "error"),
            LogRows = Math.Clamp(LogRows, MinLogRows, MaxLogRows),
            LogFont = Math.Clamp(LogFont, MinLogFont, MaxLogFont),
            LastPage = string.IsNullOrEmpty(LastPage) ? defaults.LastPage : LastPage,
            TrustedLinks = TrustedLinks ?? [],
        };
    }

    /// <summary>Какую страницу открыть при запуске.</summary>
    public string InitialPage => StartPage == LastPageValue ? LastPage : StartPage;

    /// <summary>Открыть ли меню при запуске.</summary>
    public bool InitialPaneOpen => PaneMode switch
    {
        "closed" => false,
        Remember => PaneWasOpen,
        _ => true,
    };

    /// <summary>Показывать ли запись журнала с таким уровнем.</summary>
    /// <param name="level">Уровень записи.</param>
    public bool ShowsLevel(string level) => Rank(level) >= Rank(LogFilter);

    /// <summary>Вес уровня журнала.</summary>
    private static int Rank(string level) => level switch
    {
        "debug" => 0,
        "warning" => 2,
        "error" => 3,
        _ => 1,
    };

    /// <summary>Значение из списка или значение по умолчанию.</summary>
    private static string OneOf(string? value, string fallback, params string[] allowed) =>
        value is not null && allowed.Contains(value) ? value : fallback;
}
