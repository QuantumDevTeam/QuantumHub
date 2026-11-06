using System.Diagnostics;
using Google.Protobuf;
using Microsoft.UI.Dispatching;
using QuantumHub.Client;
using QuantumHub.Contracts.Bus;
using QuantumHub.ProtoMemProtocol;

namespace QuantumHub.Gui.Services;

/// <summary>
/// Связь админки с хабом. GUI — всего лишь клиент: своего состояния у него нет, и подключается он
/// так же, как любой плагин.
/// </summary>
/// <param name="endpoint">Экземпляр хаба.</param>
/// <param name="dispatcher">Очередь потока интерфейса.</param>
/// <param name="serviceArgs">Аргументы для хаба, если GUI запускает его сам.</param>
public sealed class HubConnection(HubEndpoint endpoint, DispatcherQueue dispatcher, string? serviceArgs)
{
    /// <summary>Сколько раз пробовать подключиться при старте.</summary>
    private const int ConnectAttempts = 40;

    /// <summary>Клиент. <see langword="null"/>, пока первое подключение не удалось.</summary>
    public HubClient? Client { get; private set; }

    /// <summary>Есть ли связь.</summary>
    public bool IsConnected => Client?.IsConnected == true;

    /// <summary>Связь появилась или пропала. Срабатывает на потоке интерфейса.</summary>
    public event Action<bool>? StateChanged;

    /// <summary>Очередь потока интерфейса.</summary>
    public DispatcherQueue Dispatcher => dispatcher;

    /// <summary>Выполняет действие на потоке интерфейса.</summary>
    /// <param name="action">Действие.</param>
    public void Post(Action action) => dispatcher.TryEnqueue(() => action());

    /// <summary>Подключается, при необходимости запуская хаб.</summary>
    public async Task StartAsync()
    {
        var options = new HubClientOptions
        {
            ClientId = "quantumhub.gui",
            DisplayName = "QuantumHub GUI",
            Version = typeof(HubConnection).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            Role = "gui",
            Endpoint = endpoint,
            AutoReconnect = true,
            ConnectTimeout = TimeSpan.FromMilliseconds(250),
        };

        for (var attempt = 0; attempt < ConnectAttempts && Client is null; attempt++)
        {
            try
            {
                Client = await HubClient.ConnectAsync(options);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
            {
                if (attempt == 0) LaunchService();
                await Task.Delay(250);
            }
        }

        if (Client is null)
        {
            App.Log("Could not reach the hub.");
            return;
        }

        Client.Disconnected += () => Post(() => StateChanged?.Invoke(false));
        Client.Reconnected += () => Post(() => StateChanged?.Invoke(true));
        Post(() => StateChanged?.Invoke(true));
    }

    /// <summary>Запускает процесс хаба. Он переживёт закрытие GUI.</summary>
    public void LaunchService()
    {
        var path = FindService();
        if (path is null)
        {
            App.Log("QuantumHub.Service.exe was not found.");
            return;
        }

        Process.Start(new ProcessStartInfo(path, serviceArgs ?? string.Empty) { UseShellExecute = false, CreateNoWindow = true });
    }

    /// <summary>Ищет exe хаба. Раскладка: &lt;root&gt;\gui\QuantumHub.Gui.exe рядом с &lt;root&gt;\QuantumHub.Service.exe.</summary>
    /// <returns>Путь или <see langword="null"/>.</returns>
    public static string? FindService()
    {
        // Published layout: <root>\gui\QuantumHub.Gui.exe next to <root>\QuantumHub.Service.exe.
        var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "QuantumHub.Service.exe"));
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Просит хаб завершить работу.</summary>
    public async Task ShutdownHubAsync()
    {
        if (!IsConnected) return;
        try
        {
            await Client!.CallAsync(Envelopes.HubTarget, "hub.shutdown");
        }
        catch (HubCallException)
        {
            // Он вполне может отключиться раньше, чем ответит.
        }
    }

    /// <summary>Вызов для интерфейса: неудача превращается в <see langword="null"/> и строку в журнале, но не в падение.</summary>
    /// <typeparam name="T">Тип ответа.</typeparam>
    /// <param name="method">Метод хаба.</param>
    /// <param name="request">Запрос.</param>
    public async Task<T?> TryCallAsync<T>(string method, IMessage request) where T : class, IMessage<T>, new()
    {
        if (!IsConnected) return null;
        try
        {
            return await Client!.CallAsync<T>(Envelopes.HubTarget, method, request);
        }
        catch (HubCallException ex)
        {
            App.Log($"{method}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Вызов без ответа для интерфейса.</summary>
    /// <param name="method">Метод хаба.</param>
    /// <param name="request">Запрос.</param>
    /// <returns>Текст ошибки или <see langword="null"/> при успехе.</returns>
    public async Task<string?> TryRunAsync(string method, IMessage request)
    {
        if (!IsConnected) return "Нет связи с хабом.";
        try
        {
            await Client!.CallAsync(Envelopes.HubTarget, method, HubPayload.Proto(request));
            return null;
        }
        catch (HubCallException ex)
        {
            return ex.Message;
        }
    }
}
