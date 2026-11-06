using QuantumHub.Contracts.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuantumHub.Contracts.Proto;
using QuantumHub.Gui.Services;

namespace QuantumHub.Gui.Pages;

/// <summary>Журнал хаба с обновлением в реальном времени.</summary>
public sealed class LogsPage : HubPage
{
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None };

    /// <summary>Создаёт страницу.</summary>
    /// <param name="hub">Связь с хабом.</param>
    public LogsPage(HubConnection hub) : base(hub)
    {
        var grid = new Grid { Padding = new Thickness(36, 24, 36, 24), RowSpacing = 16 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Fluent.Text(Loc.T("Журнал", "Log"), "TitleTextBlockStyle"));
        Grid.SetRow(_list, 1);
        grid.Children.Add(_list);
        Content = grid;
    }

    /// <inheritdoc/>
    protected override async Task LoadAsync()
    {
        var logs = await Hub.TryCallAsync<LogList>("hub.logs.get", new PluginRef());
        if (logs is null) return;

        var settings = GuiSettings.Current;
        foreach (var entry in logs.Entries.Where(entry => settings.ShowsLevel(entry.Level)).TakeLast(settings.LogRows).Reverse()) _list.Items.Add(Row(entry));
        await SubscribeAsync("hub.log", e =>
        {
            var entry = e.Payload.AsProto<LogEntry>();
            if (!settings.ShowsLevel(entry.Level)) return;
            _list.Items.Insert(0, Row(entry));
            if (_list.Items.Count > settings.LogRows) _list.Items.RemoveAt(settings.LogRows);
        });
    }

    /// <summary>Строка журнала.</summary>
    private static Grid Row(LogEntry entry)
    {
        var settings = GuiSettings.Current;
        var time = DateTimeOffset.FromUnixTimeMilliseconds(entry.UnixMs).ToLocalTime();
        var mono = new FontFamily("Cascadia Mono, Consolas");
        var message = new TextBlock
        {
            Text = entry.Message,
            TextWrapping = settings.LogWrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = settings.LogWrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = true,
            FontFamily = mono,
            FontSize = settings.LogFont,
        };
        if (entry.Level == "error") message.Foreground = Fluent.Resource("SystemFillColorCriticalBrush");
        if (entry.Level == "warning") message.Foreground = Fluent.Resource("SystemFillColorCautionBrush");

        var clock = new TextBlock { Text = Fluent.Clock(time), FontFamily = mono, FontSize = settings.LogFont, Foreground = Fluent.Resource("TextFillColorTertiaryBrush") };
        if (!settings.LogSource) return Fluent.Columns((clock, Fluent.Auto), (message, Fluent.Star));

        var source = new TextBlock { Text = entry.Source, FontFamily = mono, FontSize = settings.LogFont, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Fluent.Resource("TextFillColorTertiaryBrush") };
        return Fluent.Columns((clock, Fluent.Auto), (source, new GridLength(190)), (message, Fluent.Star));
    }
}
