using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuantumHub.Contracts.Proto;
using QuantumHub.Gui.Services;

namespace QuantumHub.Gui.Pages;

/// <summary>Обзор: сводка по хабу.</summary>
public sealed class OverviewPage : HubPage
{
    private readonly StackPanel _body;

    /// <summary>Создаёт страницу.</summary>
    /// <param name="hub">Связь с хабом.</param>
    public OverviewPage(HubConnection hub) : base(hub)
    {
        var (root, body) = Fluent.Page("Обзор", "Хаб работает в фоне. Это окно можно закрыть, плагины останутся на месте.");
        _body = body;
        Content = root;
    }

    /// <inheritdoc/>
    protected override async Task LoadAsync()
    {
        _body.Children.Clear();
        var status = await Hub.TryCallAsync<HubStatus>("hub.status", new PluginRef());
        if (status is null)
        {
            ShowDisconnected();
            return;
        }

        var plugins = await Hub.TryCallAsync<PluginList>("hub.plugins.list", new PluginRef()) ?? new PluginList();
        var sessions = await Hub.TryCallAsync<SessionList>("hub.sessions.list", new PluginRef()) ?? new SessionList();
        var uptime = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(status.StartedUnixMs);
        var running = plugins.Plugins.Count(plugin => plugin.State == PluginState.Running);
        var problems = plugins.Plugins.Count(plugin => plugin.State is PluginState.Faulted or PluginState.Blocked);

        var tiles = new Grid { ColumnSpacing = 8 };
        var data = new[]
        {
            ("", running.ToString(), $"из {plugins.Plugins.Count} плагинов работают"),
            ("", sessions.Sessions.Count.ToString(), "подключений"),
            ("", FormatUptime(uptime), "без перезапуска"),
            ("", problems.ToString(), "проблем"),
        };

        for (var i = 0; i < data.Length; i++)
        {
            tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = Fluent.Star });
            var (glyph, value, label) = data[i];
            var tile = new StackPanel { Spacing = 6 };
            var icon = Fluent.Icon(glyph, 20);
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            tile.Children.Add(icon);
            tile.Children.Add(Fluent.Text(value, "TitleTextBlockStyle"));
            tile.Children.Add(Fluent.Caption(label));
            tile.HorizontalAlignment = HorizontalAlignment.Left;

            var card = Fluent.Card(tile);
            Grid.SetColumn(card, i);
            tiles.Children.Add(card);
        }

        _body.Children.Add(tiles);
        _body.Children.Add(Row("Версия хаба", status.Version));
        _body.Children.Add(Row("Транспорты", string.Join(", ", status.Transports)));
        _body.Children.Add(Row("Программа", status.RootDir));
        _body.Children.Add(Row("Данные и настройки", status.DataDir));
    }

    /// <summary>Строка сведений.</summary>
    private static Border Row(string key, string value) => Fluent.Card(Fluent.Columns(
        (Fluent.Text(key, secondary: true), new GridLength(200)),
        (new TextBlock { Text = value, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap }, Fluent.Star)), 12);

    /// <summary>Показывает предупреждение с кнопкой запуска хаба.</summary>
    private void ShowDisconnected()
    {
        var button = new Button { Content = "Запустить хаб", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        button.Click += (_, _) => Hub.LaunchService();
        _body.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Title = "Хаб не отвечает",
            Message = "Плагины недоступны, пока он не запущен.",
            ActionButton = button,
        });
    }

    /// <summary>Время работы для человека.</summary>
    private static string FormatUptime(TimeSpan time) =>
        time.TotalDays >= 1 ? $"{(int)time.TotalDays} д {time.Hours} ч"
        : time.TotalHours >= 1 ? $"{(int)time.TotalHours} ч {time.Minutes} мин"
        : $"{(int)time.TotalMinutes} мин";
}
