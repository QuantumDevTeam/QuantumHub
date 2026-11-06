using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuantumHub.Gui.Services;

namespace QuantumHub.Gui.Pages;

/// <summary>Настройки самого приложения.</summary>
public sealed class SettingsPage : HubPage
{
    /// <summary>Создаёт страницу.</summary>
    /// <param name="hub">Связь с хабом.</param>
    public SettingsPage(HubConnection hub) : base(hub)
    {
        var (root, body) = Fluent.Page("Настройки");
        Content = root;

        var service = HubConnection.FindService();
        var gui = Environment.ProcessPath;

        body.Children.Add(Toggle("Запускать хаб при входе в Windows", "Плагины будут работать сразу, без открытия этого окна.",
            Autostart.HubValue, service is null ? null : $"\"{service}\""));
        body.Children.Add(Toggle("Показывать значок в трее при входе", "Интерфейс стартует свёрнутым.",
            Autostart.GuiValue, gui is null ? null : $"\"{gui}\" --minimized"));

        var stop = new Button { Content = "Завершить" };
        stop.Click += async (_, _) => await Hub.ShutdownHubAsync();
        body.Children.Add(Setting("Завершить работу хаба", "Все плагины остановятся. Запустить снова можно на странице «Обзор».", stop));

        var about = new StackPanel { Spacing = 2 };
        about.Children.Add(Fluent.Text("О программе"));
        about.Children.Add(Fluent.Caption("QuantumHub. Copyright (c) 2026 QuantumDevTeam."));
        about.Children.Add(Fluent.Caption("Лицензия PolyForm Noncommercial 1.0.0: только для некоммерческого использования."));
        about.Children.Add(new HyperlinkButton
        {
            Content = "Текст лицензии",
            NavigateUri = new Uri("https://polyformproject.org/licenses/noncommercial/1.0.0/"),
            Padding = new Thickness(0, 4, 0, 0),
        });
        body.Children.Add(Fluent.Card(about));
    }

    /// <inheritdoc/>
    protected override Task LoadAsync() => Task.CompletedTask;

    /// <summary>Переключатель автозапуска.</summary>
    private static Border Toggle(string title, string description, string valueName, string? command)
    {
        var toggle = new ToggleSwitch { IsOn = Autostart.IsEnabled(valueName), IsEnabled = command is not null, OnContent = "Вкл", OffContent = "Выкл" };
        toggle.Toggled += (_, _) => Autostart.Set(valueName, toggle.IsOn ? command : null);
        return Setting(title, description, toggle);
    }

    /// <summary>Карточка настройки.</summary>
    private static Border Setting(string title, string description, FrameworkElement control)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Fluent.Text(title));
        text.Children.Add(Fluent.Caption(description));
        control.VerticalAlignment = VerticalAlignment.Center;
        return Fluent.Card(Fluent.Columns((text, Fluent.Star), (control, Fluent.Auto)));
    }
}
