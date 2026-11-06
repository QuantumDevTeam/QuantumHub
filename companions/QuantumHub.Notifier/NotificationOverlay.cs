using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QuantumHub.Notifier;

/// <summary>Держит открытые окошки и складывает их столбиком в правом верхнем углу.</summary>
public sealed class NotificationOverlay
{
    private const double Spacing = 8;

    private readonly Dictionary<string, NotificationWindow> _keyed = new();
    private readonly List<NotificationWindow> _open = [];

    /// <summary>Нажата кнопка: ключ окошка и id кнопки.</summary>
    public event Action<string, string>? ActionClicked;

    /// <summary>Убирает окошко, если оно ещё открыто.</summary>
    /// <param name="key">Ключ окошка.</param>
    public void Hide(string key)
    {
        if (_keyed.TryGetValue(key, out var window)) window.Dismiss();
    }

    /// <summary>Показывает уведомление или обновляет окошко с тем же ключом.</summary>
    /// <param name="display">Что показать.</param>
    public void Show(NotificationDisplay display)
    {
        var key = display.Request.Key;
        if (string.IsNullOrEmpty(key) || !_keyed.TryGetValue(key, out var window))
        {
            window = new NotificationWindow();
            var created = window;
            window.ActionClicked += action => ActionClicked?.Invoke(key ?? string.Empty, action);
            window.Closed += (_, _) =>
            {
                _open.Remove(created);
                if (!string.IsNullOrEmpty(key)) _keyed.Remove(key);
                Arrange(display);
            };
            _open.Add(window);
            if (!string.IsNullOrEmpty(key)) _keyed[key] = window;
        }

        window.Update(display);
        Arrange(display);
    }

    private void Arrange(NotificationDisplay display)
    {
        var area = SystemParameters.WorkArea;
        var top = area.Top + display.MarginTop;
        foreach (var window in _open)
        {
            window.UpdateLayout();
            window.Left = area.Right - display.MarginRight - window.ActualWidth;
            window.Top = top;
            top += window.ActualHeight + Spacing;
        }
    }
}

/// <summary>Одно окошко: не забирает фокус и плавно исчезает само. Щелчки пропускает, если в нём нет кнопок.</summary>
public sealed class NotificationWindow : Window
{
    private const int ExStyle = -20;
    private const int Transparent = 0x20;
    private const int ToolWindow = 0x80;
    private const int NoActivate = 0x8000000;
    private const int Corner = 12;

    private static readonly Duration FadeIn = TimeSpan.FromMilliseconds(250);
    private static readonly Duration FadeOut = TimeSpan.FromMilliseconds(300);

    private readonly Image _image = new() { Width = 56, Height = 56, Stretch = Stretch.UniformToFill };
    private readonly Border _picture;
    private readonly Border _badge;
    private readonly TextBlock _title = new()
    {
        Foreground = Brushes.White,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 280,
    };
    private readonly TextBlock _subtitle = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xCC)),
        FontSize = 12.5,
        Margin = new Thickness(0, 3, 0, 0),
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 280,
    };
    private readonly StackPanel _actions = new()
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 10, 0, 0),
        Visibility = Visibility.Collapsed,
    };
    private readonly DispatcherTimer _hide = new();

    /// <summary>Нажата кнопка. Передаётся её id.</summary>
    public event Action<string>? ActionClicked;

    /// <summary>Создаёт окошко. Показано оно будет при первом обновлении.</summary>
    public NotificationWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Opacity = 0;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
            CornerRadius = new CornerRadius(8),
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 22,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var imageHost = new Grid();
        imageHost.Children.Add(new Border { CornerRadius = new CornerRadius(8), Child = _image, ClipToBounds = true });
        imageHost.Children.Add(_badge);
        _picture = new Border { Width = 56, Height = 56, Margin = new Thickness(0, 0, 12, 0), Child = imageHost };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title);
        text.Children.Add(_subtitle);
        text.Children.Add(_actions);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_picture);
        row.Children.Add(text);

        Content = new Border
        {
            Margin = new Thickness(16),
            Padding = new Thickness(12),
            MinWidth = 240,
            CornerRadius = new CornerRadius(Corner),
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 0, Opacity = 0.5 },
            Child = row,
        };

        _hide.Tick += (_, _) => Hide(true);
        SourceInitialized += (_, _) => ApplyStyle();
    }

    /// <summary>Щелчки сквозь окошко мешают только кнопкам, поэтому только с ними окошко их ловит. Фокус не забирает всё равно.</summary>
    private void ApplyStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;

        var style = GetWindowLong(handle, ExStyle) | ToolWindow | NoActivate;
        SetWindowLong(handle, ExStyle, _actions.Children.Count > 0 ? style & ~Transparent : style | Transparent);
    }

    private void ShowActions(IReadOnlyList<NotificationAction>? actions)
    {
        _actions.Children.Clear();
        foreach (var action in actions ?? []) _actions.Children.Add(ActionButton(action));
        _actions.Visibility = _actions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyStyle();
    }

    /// <summary>Обычный Button забрал бы фокус и выглядел бы чужим, а здесь нужен только щелчок.</summary>
    private Border ActionButton(NotificationAction action)
    {
        var normal = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x55));
        var hover = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77));
        var button = new Border
        {
            Background = normal,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = action.Label, Foreground = Brushes.White, FontSize = 12.5 },
        };
        button.MouseEnter += (_, _) => button.Background = hover;
        button.MouseLeave += (_, _) => button.Background = normal;
        button.MouseLeftButtonUp += (_, _) =>
        {
            ActionClicked?.Invoke(action.Id);
            Dismiss();
        };
        return button;
    }

    /// <summary>Обновляет содержимое и заново заводит таймер скрытия.</summary>
    /// <param name="display">Что показать.</param>
    public void Update(NotificationDisplay display)
    {
        var request = display.Request;
        _title.Text = request.Title;
        _subtitle.Text = request.Subtitle ?? string.Empty;
        _subtitle.Visibility = string.IsNullOrEmpty(request.Subtitle) ? Visibility.Collapsed : Visibility.Visible;

        var image = Decode(request.ImageBase64);
        _image.Source = image;
        _picture.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        _badge.Visibility = request.Badge == "pause" ? Visibility.Visible : Visibility.Collapsed;
        ShowActions(request.Actions);

        if (!IsVisible) Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation(display.Opacity, FadeIn) { EasingFunction = new CubicEase() });

        _hide.Stop();
        _hide.Interval = TimeSpan.FromMilliseconds(display.DurationMs);
        _hide.Start();
    }

    /// <summary>Плавно закрывает окошко раньше срока.</summary>
    public void Dismiss() => Hide(true);

    private void Hide(bool animate)
    {
        _hide.Stop();
        if (!animate)
        {
            Close();
            return;
        }

        var fade = new DoubleAnimation(0, FadeOut);
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private static BitmapImage? Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(base64));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 112;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException)
        {
            return null; // Плохая картинка не повод терять уведомление.
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int value);
}
