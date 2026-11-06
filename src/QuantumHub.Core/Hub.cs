using System.Reflection;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Proto;
using QuantumHub.Core.Config;
using QuantumHub.Core.Plugins;
using QuantumHub.Core.Routing;
using QuantumHub.ProtoMemProtocol;
using QuantumHub.ProtoMemProtocol.Shm;

namespace QuantumHub.Core;

/// <summary>Настройки запуска хаба.</summary>
public sealed class HubOptions
{
    /// <summary>Папка программы.</summary>
    public required string RootDir { get; init; }

    /// <summary>Папка данных. По умолчанию %APPDATA%\QuantumHub.</summary>
    public string? DataDir { get; init; }

    /// <summary>Какой экземпляр хаба это будет.</summary>
    public HubEndpoint Endpoint { get; init; } = HubEndpoint.Default;
    /// <summary>Размер кольца в разделяемой памяти.</summary>
    public uint RingSize { get; init; } = ShmFrameChannel.DefaultRingSize;

    /// <summary>Слушать ещё и loopback TCP. 0 выбирает свободный порт. По умолчанию выключено.</summary>
    public int? TcpPort { get; init; }

    /// <summary>Дублировать журнал в консоль.</summary>
    public bool LogToConsole { get; init; }
}

/// <summary>
/// Хаб — это ядро: он маршрутизирует, загружает, хранит и разрешает зависимости, но ничего не знает о том,
/// что делает конкретный плагин. Без плагинов он не делает ничего.
/// </summary>
/// <remarks>
/// Этот класс только собирает части вместе. Смотри <see cref="Routing.Router"/>,
/// <see cref="Plugins.PluginManager"/>, <see cref="IpcServer"/>, <see cref="Config.ConfigStore"/>.
/// </remarks>
public sealed class Hub : IAsyncDisposable
{
    /// <summary>Источник всего, что говорит сам хаб, и владелец его собственного конфига.</summary>
    public const string ConfigOwner = "hub";

    /// <summary>Топик: плагин сменил состояние.</summary>
    public const string PluginStateTopic = "hub.plugin.state";
    /// <summary>Топик: плагин удалён.</summary>
    public const string PluginRemovedTopic = "hub.plugin.removed";
    /// <summary>Топик: клиент подключился.</summary>
    public const string SessionConnectedTopic = "hub.session.connected";
    /// <summary>Топик: клиент отключился.</summary>
    public const string SessionDisconnectedTopic = "hub.session.disconnected";
    /// <summary>Топик: новая запись в журнале.</summary>
    public const string LogTopic = "hub.log";
    /// <summary>Префикс топика изменения настроек, дальше идёт id плагина.</summary>
    public const string ConfigTopicPrefix = "config.";

    /// <summary>Версия хаба из сборки.</summary>
    public static string Version { get; } =
        typeof(Hub).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private readonly HubOptions _options;
    private readonly long _startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    /// <summary>Завершается, когда клиент просит хаб выключиться.</summary>
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Создаёт части хаба. Работать он начнёт после <see cref="StartAsync"/>.</summary>
    /// <param name="options">Настройки.</param>
    public Hub(HubOptions options)
    {
        _options = options;
        Paths = new HubPaths(options.RootDir, options.DataDir);
        Log = new HubLog(Paths.LogsDir, options.LogToConsole);
        Config = new ConfigStore(Paths.ConfigDir, Log);
        Router = new Router();
        UiState = new UiStateStore(Router);
        Plugins = new PluginManager(this);
        Server = new IpcServer(this, options);
    }

    /// <summary>Имя этого экземпляра хаба.</summary>
    public string Instance => _options.Endpoint.Instance;

    /// <summary>Пути.</summary>
    public HubPaths Paths { get; }
    /// <summary>Журнал.</summary>
    public HubLog Log { get; }
    /// <summary>Настройки всех плагинов.</summary>
    public ConfigStore Config { get; }
    /// <summary>Роутер сообщений.</summary>
    public Router Router { get; }
    /// <summary>Состояние интерфейсов.</summary>
    public UiStateStore UiState { get; }
    /// <summary>Плагины.</summary>
    public PluginManager Plugins { get; }
    /// <summary>Приём клиентов.</summary>
    public IpcServer Server { get; }

    /// <summary>Завершается, когда клиент просит хаб выключиться.</summary>
    public Task ShutdownRequested => _shutdown.Task;

    /// <summary>Просит хост завершить работу.</summary>
    public void RequestShutdown() => _shutdown.TrySetResult();

    /// <summary>Запускает хаб: находит плагины, открывает приём подключений, стартует плагины.</summary>
    public async Task StartAsync()
    {
        Log.Info($"QuantumHub {Version} starting from {Paths.RootDir}");

        Router.Register(Envelopes.HubTarget, new HubApi(this));
        Router.ActivateTarget = ActivateTargetAsync;
        Plugins.ExternalProviders = Server.Providers;
        Log.Written += PublishLog;
        Config.Changed += PublishConfigChange;

        Plugins.Discover();
        Server.Start();
        await Plugins.StartAllAsync();

        Log.Info($"Ready. Transports: {string.Join(", ", Server.Transports)}");
    }

    /// <summary>Находит адресата, которого ещё нет в таблице роутера: возможность или плагин по требованию.</summary>
    private async Task<IEndpoint?> ActivateTargetAsync(string target, CancellationToken token)
    {
        var id = target.StartsWith(Router.CapabilityPrefix, StringComparison.Ordinal)
            ? Plugins.FindCapabilityProvider(target[Router.CapabilityPrefix.Length..])
            : target;
        if (id is null) return null;

        if (Plugins.IsKnown(id)) await Plugins.EnsureStartedAsync(id).WaitAsync(token);

        // Если это плагин, он уже зарегистрирован. Подключённый клиент был зарегистрирован с самого начала.
        return Router.Find(id);
    }

    /// <summary>Рассылает запись журнала.</summary>
    private void PublishLog(LogEntry entry) => Router.Publish(ConfigOwner, LogTopic, HubPayload.Proto(entry));

    /// <summary>Рассылает изменение настройки: и как состояние интерфейса, и как событие для клиентов.</summary>
    private void PublishConfigChange(string owner, string key)
    {
        var value = Config.Get(owner, key);
        UiState.Set(owner, "config:" + key, HubApi.FormatConfigValue(value));
        Router.Publish(ConfigOwner, ConfigTopicPrefix + owner, HubPayload.Proto(new ConfigChanged
        {
            PluginId = owner,
            Key = key,
            Json = value?.ToJsonString() ?? string.Empty,
        }));
    }

    /// <summary>Сводка для страницы «Обзор».</summary>
    public HubStatus GetStatus() => new()
    {
        Version = Version,
        ProcessId = (uint)Environment.ProcessId,
        StartedUnixMs = _startedAt,
        RootDir = Paths.RootDir,
        DataDir = Paths.DataDir,
        PluginDirs = { Paths.PluginDirs },
        Transports = { Server.Transports },
    };

    /// <summary>Завершает работу: отключает клиентов, останавливает плагины, сохраняет настройки.</summary>
    public async ValueTask DisposeAsync()
    {
        Log.Info("Shutting down");
        Server.Dispose();
        await Plugins.StopAllAsync();
        Config.Dispose();
        Log.Dispose();
    }
}
