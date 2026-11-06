using QuantumHub.Contracts.Ui;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using QuantumHub.Contracts.Proto;
using QuantumHub.Gui.Services;
using Windows.UI;

namespace QuantumHub.Gui;

/// <summary>Несколько строительных блоков, из которых собраны все страницы, чтобы они выглядели одним приложением.</summary>
public static class Fluent
{
    /// <summary>Во сколько раз компактная плотность уменьшает отступы.</summary>
    private const double CompactPadding = 0.55;

    /// <summary>Включена ли компактная плотность.</summary>
    private static bool Compact => GuiSettings.Current.Density == "compact";

    /// <summary>Наибольшая ширина страницы.</summary>
    public static double PageMaxWidth => GuiSettings.Current.PageWidth switch
    {
        "wide" => 1600,
        "full" => double.PositiveInfinity,
        _ => 1100,
    };

    /// <summary>Время в выбранном формате.</summary>
    /// <param name="time">Время.</param>
    public static string Clock(DateTimeOffset time) =>
        time.ToString(GuiSettings.Current.Clock == "12" ? "hh:mm:ss tt" : "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Кисть из ресурсов темы. Светлая и тёмная темы работают автоматически.</summary>
    /// <param name="key">Имя ресурса.</param>
    public static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Стиль из ресурсов темы.</summary>
    /// <param name="key">Имя ресурса.</param>
    public static Style TextStyle(string key) => (Style)Application.Current.Resources[key];

    /// <summary>Текстовый блок.</summary>
    /// <param name="text">Текст.</param>
    /// <param name="style">Имя стиля.</param>
    /// <param name="secondary">Приглушённый цвет.</param>
    public static TextBlock Text(string text, string style = "BodyTextBlockStyle", bool secondary = false)
    {
        var block = new TextBlock { Text = text, Style = TextStyle(style), TextWrapping = TextWrapping.Wrap };
        if (secondary) block.Foreground = Resource("TextFillColorSecondaryBrush");
        return block;
    }

    /// <summary>Мелкая приглушённая подпись.</summary>
    /// <param name="text">Текст.</param>
    public static TextBlock Caption(string text) => Text(text, "CaptionTextBlockStyle", true);

    /// <summary>Символ из шрифта Segoe Fluent Icons.</summary>
    /// <param name="glyph">Сам символ.</param>
    /// <param name="size">Размер.</param>
    public static FontIcon Icon(string glyph, double size = 16) => new() { Glyph = glyph, FontSize = size };

    /// <summary>Превращает код вроде "E713" в символ. Так иконки приходят от плагинов.</summary>
    /// <param name="codePoint">Шестнадцатеричный код.</param>
    public static string Glyph(string codePoint) =>
        int.TryParse(codePoint, System.Globalization.NumberStyles.HexNumber, null, out var value)
            ? char.ConvertFromUtf32(value)
            : "";

    /// <summary>Карточка.</summary>
    /// <param name="content">Содержимое.</param>
    /// <param name="padding">Внутренний отступ.</param>
    public static Border Card(UIElement content, double padding = 16) => new()
    {
        Child = content,
        Padding = new Thickness(Compact ? padding * CompactPadding : padding),
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        Background = Resource("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = Resource("CardStrokeColorDefaultBrush"),
    };

    /// <summary>Прокручиваемая страница с заголовком, карточки которой плавно появляются.</summary>
    /// <param name="title">Заголовок.</param>
    /// <param name="subtitle">Пояснение.</param>
    /// <returns>Корень страницы и панель для содержимого.</returns>
    public static (ScrollViewer Root, StackPanel Body) Page(string title, string? subtitle = null)
    {
        var body = new StackPanel { Spacing = Compact ? 4 : 8 };
        if (GuiSettings.Current.Animations) body.ChildrenTransitions = [new EntranceThemeTransition { IsStaggeringEnabled = true }];
        var column = new StackPanel { Spacing = 20, Padding = new Thickness(36, 24, 36, 36), MaxWidth = PageMaxWidth, HorizontalAlignment = HorizontalAlignment.Stretch };

        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(Text(title, "TitleTextBlockStyle"));
        if (subtitle is not null) header.Children.Add(Text(subtitle, secondary: true));
        column.Children.Add(header);
        column.Children.Add(body);

        return (Scroll(column), body);
    }

    /// <summary>
    /// Прокрутка для колонки с ограниченной шириной. Сетка между ними обязательна: ScrollViewer ставит прямого потомка
    /// с MaxWidth со сдвигом, и после сворачивания меню страница уезжает за правый край.
    /// </summary>
    /// <param name="column">Колонка.</param>
    public static ScrollViewer Scroll(FrameworkElement column)
    {
        var host = new Grid();
        host.Children.Add(column);
        return new ScrollViewer { Content = host };
    }

    /// <summary>Цветная метка состояния плагина.</summary>
    /// <param name="state">Состояние.</param>
    public static Border StateBadge(PluginState state)
    {
        var (label, color) = state switch
        {
            PluginState.Running => (Loc.T("работает", "running"), Color.FromArgb(255, 34, 197, 94)),
            PluginState.Starting => (Loc.T("запускается", "starting"), Color.FromArgb(255, 59, 130, 246)),
            PluginState.Stopping => (Loc.T("останавливается", "stopping"), Color.FromArgb(255, 59, 130, 246)),
            PluginState.Disabled => (Loc.T("выключен", "off"), Color.FromArgb(255, 148, 163, 184)),
            PluginState.Faulted => (Loc.T("ошибка", "error"), Color.FromArgb(255, 239, 68, 68)),
            PluginState.Blocked => (Loc.T("ждёт зависимость", "needs a dependency"), Color.FromArgb(255, 234, 179, 8)),
            _ => (Loc.T("ждёт запроса", "on demand"), Color.FromArgb(255, 148, 163, 184)),
        };

        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 2, 10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(36, color.R, color.G, color.B)),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(color),
            },
        };
    }

    /// <summary>Сетка из одной строки.</summary>
    /// <param name="cells">Элементы и ширина их колонок.</param>
    public static Grid Columns(params (UIElement Element, GridLength Width)[] cells)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        for (var i = 0; i < cells.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = cells[i].Width });
            Grid.SetColumn((FrameworkElement)cells[i].Element, i);
            grid.Children.Add(cells[i].Element);
        }

        return grid;
    }

    /// <summary>Колонка занимает всё свободное место.</summary>
    public static readonly GridLength Star = new(1, GridUnitType.Star);
    /// <summary>Колонка по размеру содержимого.</summary>
    public static readonly GridLength Auto = GridLength.Auto;
}
