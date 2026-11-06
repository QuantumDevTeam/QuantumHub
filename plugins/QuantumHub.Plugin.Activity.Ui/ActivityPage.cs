using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuantumHub.Contracts.Bus;
using QuantumHub.Ui.Contracts;

namespace QuantumHub.Plugin.Activity.Ui;

/// <summary>Окно в фокусе. Копия записи из сервисной части: у половин плагина общий только формат JSON, а не сборка.</summary>
/// <param name="Process">Имя процесса.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="ProcessId">Id процесса.</param>
/// <param name="UnixMs">Когда окно стало активным.</param>
public sealed record ForegroundWindow(string Process, string Title, int ProcessId, long UnixMs);

/// <summary>
/// Написанный вручную WinUI с тем, чего декларативный интерфейс описать не может: живая лента,
/// которая растёт на каждое событие от сервисной части плагина.
/// </summary>
public sealed class ActivityPage : INativePluginPage
{
    /// <summary>Сколько строк показывать.</summary>
    private const int MaxRows = 50;

    /// <inheritdoc/>
    public string Title => "Лента";
    /// <inheritdoc/>
    public string Icon => "E81C";

    /// <inheritdoc/>
    public FrameworkElement CreateView(IPluginUiHost host)
    {
        var rows = new StackPanel { Spacing = 4 };
        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(new TextBlock
        {
            Text = "Этот экран — настоящий WinUI из отдельной DLL плагина. Переключись на другое окно, и тут появится строка.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Resource("TextFillColorSecondaryBrush"),
        });
        root.Children.Add(rows);

        IDisposable? subscription = null;
        root.Loaded += async (_, _) =>
        {
            subscription = await host.Bus.SubscribeAsync("activity.window.changed", e =>
            {
                var window = e.Payload.AsJson<ForegroundWindow>();
                if (window is null) return;
                host.Dispatcher.TryEnqueue(() =>
                {
                    rows.Children.Insert(0, Row(window));
                    if (rows.Children.Count > MaxRows) rows.Children.RemoveAt(MaxRows);
                });
            });

            var current = await host.Bus.CallAsync(host.PluginId, "window.current");
            if (current.AsJson<ForegroundWindow>() is { } first) rows.Children.Add(Row(first));
        };
        root.Unloaded += (_, _) => subscription?.Dispose();
        return root;
    }

    /// <summary>Строка ленты.</summary>
    private static Border Row(ForegroundWindow window)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(window.UnixMs).ToLocalTime();
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var clock = new TextBlock { Text = time.ToString("HH:mm:ss"), Foreground = Resource("TextFillColorTertiaryBrush") };
        var process = new TextBlock { Text = window.Process, TextTrimming = TextTrimming.CharacterEllipsis };
        var title = new TextBlock
        {
            Text = window.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Resource("TextFillColorSecondaryBrush"),
        };
        Grid.SetColumn(process, 1);
        Grid.SetColumn(title, 2);
        grid.Children.Add(clock);
        grid.Children.Add(process);
        grid.Children.Add(title);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(6),
            Background = Resource("CardBackgroundFillColorDefaultBrush"),
        };
    }

    /// <summary>Кисть из ресурсов темы.</summary>
    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}
