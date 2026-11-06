using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuantumHub.Contracts.Proto;
using QuantumHub.Gui.Services;

namespace QuantumHub.Gui.Pages;

/// <summary>Журнал хаба с обновлением в реальном времени.</summary>
public sealed class LogsPage : HubPage
{
    /// <summary>Сколько строк показывать.</summary>
    private const int MaxRows = 500;

    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None };

    /// <summary>Создаёт страницу.</summary>
    /// <param name="hub">Связь с хабом.</param>
    public LogsPage(HubConnection hub) : base(hub)
    {
        var grid = new Grid { Padding = new Thickness(36, 24, 36, 24), RowSpacing = 16 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Fluent.Text("Журнал", "TitleTextBlockStyle"));
        Grid.SetRow(_list, 1);
        grid.Children.Add(_list);
        Content = grid;
    }

    /// <inheritdoc/>
    protected override async Task LoadAsync()
    {
        var logs = await Hub.TryCallAsync<LogList>("hub.logs.get", new PluginRef());
        if (logs is null) return;

        foreach (var entry in logs.Entries.TakeLast(MaxRows).Reverse()) _list.Items.Add(Row(entry));
        await SubscribeAsync("hub.log", e =>
        {
            _list.Items.Insert(0, Row(e.Payload.AsProto<LogEntry>()));
            if (_list.Items.Count > MaxRows) _list.Items.RemoveAt(MaxRows);
        });
    }

    /// <summary>Строка журнала.</summary>
    private static Grid Row(LogEntry entry)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(entry.UnixMs).ToLocalTime();
        var mono = new FontFamily("Cascadia Mono, Consolas");
        var message = new TextBlock { Text = entry.Message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = mono, FontSize = 12 };
        if (entry.Level == "error") message.Foreground = Fluent.Resource("SystemFillColorCriticalBrush");
        if (entry.Level == "warning") message.Foreground = Fluent.Resource("SystemFillColorCautionBrush");

        var clock = new TextBlock { Text = time.ToString("HH:mm:ss"), FontFamily = mono, FontSize = 12, Foreground = Fluent.Resource("TextFillColorTertiaryBrush") };
        var source = new TextBlock { Text = entry.Source, FontFamily = mono, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Fluent.Resource("TextFillColorSecondaryBrush") };
        return Fluent.Columns((clock, Fluent.Auto), (source, new GridLength(190)), (message, Fluent.Star));
    }
}
