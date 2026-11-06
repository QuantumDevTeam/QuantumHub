using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;
using QuantumHub.Gui.Services;

namespace QuantumHub.Gui.Pages;

/// <summary>Кто сейчас подключён к хабу.</summary>
public sealed class SessionsPage : HubPage
{
    private readonly StackPanel _body;

    /// <summary>Создаёт страницу.</summary>
    /// <param name="hub">Связь с хабом.</param>
    public SessionsPage(HubConnection hub) : base(hub)
    {
        var (root, body) = Fluent.Page(Loc.T("Подключения", "Connections"), Loc.T("Внешние плагины и другие программы, которые сейчас говорят с хабом.", "External plugins and other programs talking to the hub right now."));
        _body = body;
        Content = root;
    }

    /// <inheritdoc/>
    protected override async Task LoadAsync()
    {
        await RefreshAsync();
        await SubscribeAsync("hub.session.connected", e => { _ = RefreshAsync(); });
        await SubscribeAsync("hub.session.disconnected", e => { _ = RefreshAsync(); });
    }

    /// <summary>Перестраивает список.</summary>
    private async Task RefreshAsync()
    {
        var list = await Hub.TryCallAsync<SessionList>("hub.sessions.list", new PluginRef());
        if (list is null) return;

        _body.Children.Clear();
        foreach (var session in list.Sessions.OrderBy(entry => entry.Role == "gui").ThenBy(entry => entry.DisplayName))
            _body.Children.Add(Row(session));

        var history = await Hub.TryCallAsync<SessionHistory>("hub.sessions.history", new PluginRef());
        if (history is null || history.Entries.Count == 0) return;

        var heading = Fluent.Text(Loc.T("За этот сеанс", "This session"), "BodyStrongTextBlockStyle");
        heading.Margin = new Thickness(0, 16, 0, 0);
        _body.Children.Add(heading);

        var lines = new StackPanel { Spacing = 6 };
        foreach (var entry in history.Entries) lines.Children.Add(HistoryRow(entry));
        _body.Children.Add(Fluent.Card(lines));
    }

    /// <summary>Строка истории: время, имя и подключён ли клиент сейчас.</summary>
    private static Grid HistoryRow(SessionHistoryEntry entry)
    {
        static string Time(long unixMs) => Fluent.Clock(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime());

        var ended = entry.DisconnectedUnixMs > 0 ? Time(entry.DisconnectedUnixMs) : Loc.T("подключён", "connected");
        var name = entry.DisplayName.Length > 0 ? entry.DisplayName : entry.ClientId;
        return Fluent.Columns(
            (Fluent.Caption(Time(entry.ConnectedUnixMs)), new GridLength(84)),
            (Fluent.Text(name), Fluent.Star),
            (Fluent.Caption(ended), Fluent.Auto));
    }

    /// <summary>Карточка подключения.</summary>
    private static Border Row(SessionInfo session)
    {
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(Fluent.Text($"{session.DisplayName} {session.Version}", "BodyStrongTextBlockStyle"));
        text.Children.Add(Fluent.Caption(Loc.T($"{session.ClientId} · процесс {session.ProcessId} · с {Fluent.Clock(DateTimeOffset.FromUnixTimeMilliseconds(session.ConnectedUnixMs).ToLocalTime())}", $"{session.ClientId} · process {session.ProcessId} · since {Fluent.Clock(DateTimeOffset.FromUnixTimeMilliseconds(session.ConnectedUnixMs).ToLocalTime())}")));

        foreach (var dependency in session.Dependencies)
        {
            var name = dependency.Dependency.Id.Length > 0 ? dependency.Dependency.Id : dependency.Dependency.Capability;
            var provider = dependency.ProviderId.Length > 0 ? $" ← {dependency.ProviderId}" : string.Empty;
            text.Children.Add(Fluent.Caption(Loc.T($"зависимость: {name} ({dependency.State}){provider}", $"dependency: {name} ({dependency.State}){provider}")));
        }

        var transport = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 2, 10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Fluent.Resource("AccentFillColorDefaultBrush"),
            Child = new TextBlock { Text = session.Transport, FontSize = 12, Foreground = Fluent.Resource("TextOnAccentFillColorPrimaryBrush") },
        };

        return Fluent.Card(Fluent.Columns((text, Fluent.Star), (transport, Fluent.Auto)));
    }
}
