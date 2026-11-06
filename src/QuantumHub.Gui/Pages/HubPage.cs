using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuantumHub.Contracts.Bus;
using QuantumHub.Gui.Services;

namespace QuantumHub.Gui.Pages;

/// <summary>Страница, которая загружается с хаба при показе и отпускает подписки при уходе.</summary>
public abstract class HubPage : UserControl
{
    /// <summary>Подписки страницы.</summary>
    private readonly List<IDisposable> _subscriptions = [];

    /// <summary>Подключает загрузку и уборку к показу и скрытию.</summary>
    /// <param name="hub">Связь с хабом.</param>
    protected HubPage(HubConnection hub)
    {
        Hub = hub;
        Loaded += async (_, _) => await LoadSafelyAsync();
        Unloaded += (_, _) =>
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        };
    }

    /// <summary>Связь с хабом.</summary>
    protected HubConnection Hub { get; }

    /// <summary>Заполняет страницу.</summary>
    protected abstract Task LoadAsync();

    /// <summary>Вызывает <see cref="LoadAsync"/>, не давая ошибке уронить окно.</summary>
    protected async Task LoadSafelyAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            App.Log($"{GetType().Name}: {ex}");
        }
    }

    /// <summary>Подписка на время жизни страницы.</summary>
    /// <param name="topic">Топик.</param>
    /// <param name="handler">Обработчик. Выполняется на потоке интерфейса.</param>
    protected async Task SubscribeAsync(string topic, Action<HubEvent> handler)
    {
        if (Hub.Client is not { IsConnected: true } client) return;
        _subscriptions.Add(await client.SubscribeAsync(topic, e => Hub.Post(() => handler(e))));
    }

    /// <summary>Показывает ошибку вверху панели.</summary>
    /// <param name="panel">Панель.</param>
    /// <param name="message">Текст.</param>
    protected static void ShowError(Panel panel, string message) =>
        panel.Children.Insert(0, new InfoBar { IsOpen = true, Severity = InfoBarSeverity.Error, Title = "Не получилось", Message = message });
}
