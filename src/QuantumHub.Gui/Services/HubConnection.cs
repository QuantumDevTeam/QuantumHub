using QuantumHub.Contracts.Ui;
using System.Diagnostics;
using Google.Protobuf;
using Microsoft.UI.Dispatching;
using QuantumHub.Client;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Proto;
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
    /// <summary>Пауза между попытками подключиться.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
    /// <summary>Как часто запускать хаб заново, пока он не отвечает.</summary>
    private static readonly TimeSpan RelaunchDelay = TimeSpan.FromSeconds(5);
    /// <summary>Каждая какая попытка подключения запускает хаб.</summary>
    private const int LaunchEvery = 10;
    /// <summary>Имя файла хаба.</summary>
    private const string ServiceExe = "QuantumHub.Service.exe";

    /// <summary>Пользователь сам завершил хаб, и поднимать его снова не надо.</summary>
    private bool _stopRequested;
    /// <summary>Цикл перезапуска уже идёт.</summary>
    private bool _restarting;
    /// <summary>Пользователь попросил перезапуск: хаб поднимается и при выключенной настройке.</summary>
    private bool _restartRequested;

    /// <summary>Клиент. <see langword="null"/>, пока первое подключение не удалось.</summary>
    public HubClient? Client { get; private set; }

    /// <summary>Есть ли связь.</summary>
    public bool IsConnected => Client?.IsConnected == true;

    /// <summary>Что мешает работе с хабом, понятным человеку языком. <see langword="null"/>, если ничего не известно.</summary>
    public string? ServiceProblem { get; private set; }

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

        // Хаб может подниматься долго или запуститься позже, поэтому пробуем, пока не получится.
        for (var attempt = 0; Client is null; attempt++)
        {
            try
            {
                Client = await HubClient.ConnectAsync(options);
            }
            catch (UnauthorizedAccessException)
            {
                // Такое бывает, когда хаб и GUI запущены с разными правами: один от администратора, другой нет.
                Report(Loc.T("Хаб запущен с другими правами. Закрой его и запусти QuantumHub без прав администратора.", "The hub runs with different rights. Close it and start QuantumHub without administrator rights."));
                await Task.Delay(RelaunchDelay);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
            {
                if (attempt % LaunchEvery == 0 && !_stopRequested && GuiSettings.Current.StartHub) LaunchService();
                await Task.Delay(RetryDelay);
            }
        }

        await SyncLanguageAsync();
        await Client.SubscribeAsync("hub.language.changed", e => Post(() => SetLanguage(e.Payload.AsText())));

        Client.Disconnected += () => Post(OnDisconnected);
        Client.Reconnected += () => Post(OnConnected);
        Post(OnConnected);
    }

    /// <summary>Настройка языка, которую хранит хаб.</summary>
    public string LanguageSetting { get; private set; } = Loc.Auto;

    /// <summary>Сменился язык. Срабатывает на потоке интерфейса.</summary>
    public event Action? LanguageChanged;

    /// <summary>Берёт язык у хаба: настройка у всей программы одна.</summary>
    private async Task SyncLanguageAsync()
    {
        if (await TryCallAsync<HubStatus>("hub.status", new PluginRef()) is not { } status) return;
        LanguageSetting = status.LanguageSetting;
        Post(() => SetLanguage(status.Language));
    }

    /// <summary>Применяет язык.</summary>
    private void SetLanguage(string language)
    {
        if (Loc.Language == language) return;
        Loc.Language = language;
        LanguageChanged?.Invoke();
    }

    /// <summary>Меняет настройку языка.</summary>
    /// <param name="setting">"auto", "ru" или "en".</param>
    public async Task SetLanguageSettingAsync(string setting)
    {
        LanguageSetting = setting;
        await TryRunAsync("hub.config.set", new ConfigSetRequest { PluginId = "hub", Key = "language", Json = $"\"{setting}\"" });
    }

    /// <summary>Связь есть.</summary>
    private void OnConnected()
    {
        ServiceProblem = null;
        StateChanged?.Invoke(true);
    }

    /// <summary>Связь пропала. Если хаб упал сам, поднимаем его снова.</summary>
    private async void OnDisconnected()
    {
        StateChanged?.Invoke(false);
        if (_stopRequested || _restarting) return;
        if (!GuiSettings.Current.RestartHub && !_restartRequested) return;

        _restarting = true;
        try
        {
            while (!IsConnected && !_stopRequested)
            {
                LaunchService();
                await Task.Delay(RelaunchDelay);
            }
        }
        finally
        {
            _restarting = false;
            _restartRequested = false;
        }
    }

    /// <summary>Запоминает проблему и показывает её в интерфейсе.</summary>
    private void Report(string problem)
    {
        if (ServiceProblem == problem) return;
        ServiceProblem = problem;
        App.Log(problem);
        Post(() => StateChanged?.Invoke(false));
    }

    /// <summary>Запускает процесс хаба. Он переживёт закрытие GUI. Второй экземпляр хаба сам завершится, так что вызывать это безопасно.</summary>
    public void LaunchService()
    {
        _stopRequested = false;
        var path = FindService();
        if (path is null)
        {
            Report(Loc.T($"Не найден {ServiceExe}. Переустанови QuantumHub или собери его командой scripts\\publish.ps1.", $"{ServiceExe} was not found. Reinstall QuantumHub or build it with scripts\\publish.ps1."));
            return;
        }

        var arguments = serviceArgs ?? string.Empty;
        if (endpoint.Instance != HubEndpoint.DefaultInstance && !arguments.Contains("--instance"))
            arguments = $"--instance \"{endpoint.Instance}\" {arguments}".TrimEnd();

        try
        {
            Process.Start(new ProcessStartInfo(path, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(path),
            })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Report(Loc.T($"Не удалось запустить хаб: {ex.Message}", $"Could not start the hub: {ex.Message}"));
        }
    }

    /// <summary>Ищет exe хаба: в установленной программе, потом в репозитории, если GUI собран отдельно.</summary>
    /// <returns>Путь или <see langword="null"/>.</returns>
    public static string? FindService()
    {
        var baseDir = AppContext.BaseDirectory;

        // Раскладка программы: <root>\gui\QuantumHub.Gui.exe рядом с <root>\QuantumHub.Service.exe.
        foreach (var path in new[] { Path.Combine(baseDir, "..", ServiceExe), Path.Combine(baseDir, ServiceExe) })
        {
            if (File.Exists(path)) return Path.GetFullPath(path);
        }

        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "QuantumHub.sln"))) continue;

            // Собранная программа с плагинами лучше голого хаба из bin.
            var published = Path.Combine(dir.FullName, "artifacts", "QuantumHub", ServiceExe);
            if (File.Exists(published)) return published;

            var bin = new DirectoryInfo(Path.Combine(dir.FullName, "src", "QuantumHub.Service", "bin"));
            return bin.Exists
                ? bin.EnumerateFiles(ServiceExe, SearchOption.AllDirectories).OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault()?.FullName
                : null;
        }

        return null;
    }

    /// <summary>Перезапускает хаб.</summary>
    public async Task RestartHubAsync()
    {
        if (!IsConnected) return;
        _restartRequested = true;
        try
        {
            await Client!.CallAsync(Envelopes.HubTarget, "hub.shutdown");
        }
        catch (HubCallException)
        {
            // Он вполне может отключиться раньше, чем ответит.
        }
    }

    /// <summary>Просит хаб завершить работу.</summary>
    public async Task ShutdownHubAsync()
    {
        if (!IsConnected) return;
        _stopRequested = true;
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

    /// <summary>Вызов для интерфейса, которому нужен и ответ, и текст ошибки.</summary>
    /// <typeparam name="T">Тип ответа.</typeparam>
    /// <param name="method">Метод хаба.</param>
    /// <param name="request">Запрос.</param>
    /// <returns>Ответ либо текст ошибки.</returns>
    public async Task<(T? Reply, string? Error)> CallWithErrorAsync<T>(string method, IMessage request) where T : class, IMessage<T>, new()
    {
        if (!IsConnected) return (null, ServiceProblem ?? Loc.T("Хаб ещё запускается. Попробуй через пару секунд.", "The hub is still starting. Try again in a couple of seconds."));
        try
        {
            return (await Client!.CallAsync<T>(Envelopes.HubTarget, method, request), null);
        }
        catch (HubCallException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Вызов без ответа для интерфейса.</summary>
    /// <param name="method">Метод хаба.</param>
    /// <param name="request">Запрос.</param>
    /// <returns>Текст ошибки или <see langword="null"/> при успехе.</returns>
    public async Task<string?> TryRunAsync(string method, IMessage request)
    {
        if (!IsConnected) return ServiceProblem ?? Loc.T("Хаб ещё запускается. Попробуй через пару секунд.", "The hub is still starting. Try again in a couple of seconds.");
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
