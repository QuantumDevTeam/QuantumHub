using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuantumHub.Contracts.Proto;
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
        var (root, body) = Fluent.Page("Подключения", "Внешние плагины и другие программы, которые сейчас говорят с хабом.");
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
    }

    /// <summary>Карточка подключения.</summary>
    private static Border Row(SessionInfo session)
    {
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(Fluent.Text($"{session.DisplayName} {session.Version}", "BodyStrongTextBlockStyle"));
        text.Children.Add(Fluent.Caption($"{session.ClientId} · процесс {session.ProcessId} · с {DateTimeOffset.FromUnixTimeMilliseconds(session.ConnectedUnixMs).ToLocalTime():HH:mm:ss}"));

        foreach (var dependency in session.Dependencies)
        {
            var name = dependency.Dependency.Id.Length > 0 ? dependency.Dependency.Id : dependency.Dependency.Capability;
            var provider = dependency.ProviderId.Length > 0 ? $" ← {dependency.ProviderId}" : string.Empty;
            text.Children.Add(Fluent.Caption($"зависимость: {name} ({dependency.State}){provider}"));
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
