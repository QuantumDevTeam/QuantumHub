using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Plugin;
using QuantumHub.Contracts.Proto;
using QuantumHub.Core.Config;
using QuantumHub.Core.Routing;
using QuantumHub.ProtoMemProtocol;

namespace QuantumHub.Core.Plugins;

/// <summary>Плагин, работающий внутри хаба, и контекст, который он получил.</summary>
/// <remarks>
/// Один объект играет сразу все роли: адресат для роутера (<see cref="IEndpoint"/>) и вся «обратная» сторона
/// плагина (<see cref="IPluginContext"/>, <see cref="IHubBus"/>, настройки, пути). Это удобно ещё и при
/// выгрузке: всё, что держит плагин, держит один объект.
/// </remarks>
public sealed class LoadedPlugin : IEndpoint, IPluginContext, IHubBus, IPluginConfig, IPluginPaths, IPluginUiState
{
    private readonly Hub _hub;
    private readonly PluginPackage _package;
    /// <summary>Методы плагина.</summary>
    private readonly ConcurrentDictionary<string, HubRequestHandler> _handlers = new();
    /// <summary>Доставляет плагину события, на которые он подписан.</summary>
    private readonly EventPump _events;
    /// <summary>Отменяется после остановки: незавершённые вызовы плагина не должны его пережить.</summary>
    private readonly CancellationTokenSource _lifetime = new();
    /// <summary>Область загрузки, существует пока плагин работает.</summary>
    private PluginLoadContext? _loadContext;
    /// <summary>Экземпляр класса плагина.</summary>
    private IHubPlugin? _instance;
    private ulong _nextId;

    /// <summary>Готовит плагин. Загрузка будет в <see cref="StartAsync"/>.</summary>
    /// <param name="hub">Хаб.</param>
    /// <param name="package">Что загружать.</param>
    public LoadedPlugin(Hub hub, PluginPackage package)
    {
        _hub = hub;
        _package = package;
        Logger = hub.Log.For(package.Manifest.Id);
        _events = new EventPump(ex => Logger.Error("An event handler failed", ex));
    }

    /// <summary>Id плагина.</summary>
    public string Id => _package.Manifest.Id;

    // Реализация IPluginContext.
    /// <inheritdoc/>
    public PluginManifest Manifest => _package.Manifest;

    /// <inheritdoc/>
    public string HubInstance => _hub.Instance;
    /// <inheritdoc/>
    public IHubBus Bus => this;
    /// <inheritdoc/>
    public IPluginConfig Config => this;
    /// <inheritdoc/>
    public IPluginLogger Logger { get; }
    /// <inheritdoc/>
    public IPluginPaths Paths => this;
    /// <inheritdoc/>
    public IPluginUiState Ui => this;

    // Реализация IPluginPaths.
    /// <inheritdoc/>
    public string InstallDir => _package.Directory;
    /// <inheritdoc/>
    public string DataDir => Directory.CreateDirectory(Path.Combine(_hub.Paths.PluginDataDir, Id)).FullName;
    /// <inheritdoc/>
    public string CacheDir => Directory.CreateDirectory(Path.Combine(_hub.Paths.CacheDir, Id)).FullName;

    /// <summary>Загружает сборку, находит класс плагина и запускает его.</summary>
    /// <param name="token">Отменяется по таймауту запуска.</param>
    /// <exception cref="InvalidDataException">Манифест не называет сборку, или она вне папки плагина.</exception>
    public async Task StartAsync(CancellationToken token)
    {
        var entry = Manifest.Service;
        if (entry is null || entry.Assembly.Length == 0)
            throw new InvalidDataException("The manifest names no service assembly.");

        var path = Path.GetFullPath(Path.Combine(_package.Directory, entry.Assembly));
        if (!path.StartsWith(Path.GetFullPath(_package.Directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The service assembly must be inside the plugin folder.");

        _loadContext = new PluginLoadContext(path, Id);
        var assembly = _loadContext.LoadFromAssemblyPath(path);
        var type = FindPluginType(assembly, entry.Type);
        _instance = (IHubPlugin)Activator.CreateInstance(type)!;

        _hub.Config.Changed += OnConfigChanged;
        _hub.Router.Register(Id, this);
        await _instance.StartAsync(this, token);
    }

    /// <summary>Находит класс плагина: по имени из манифеста, по [HubPlugin] или единственный реализующий интерфейс.</summary>
    private static Type FindPluginType(Assembly assembly, string typeName)
    {
        if (typeName.Length > 0)
            return assembly.GetType(typeName, true)!;

        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // В моно-DLL могут быть классы UI, базовых типов которых нет в процессе хаба.
            types = ex.Types;
        }

        var candidates = types.Where(t => t is { IsAbstract: false } && typeof(IHubPlugin).IsAssignableFrom(t)).ToList();
        return candidates.FirstOrDefault(t => t!.GetCustomAttribute<HubPluginAttribute>() is not null)
               ?? candidates.FirstOrDefault()
               ?? throw new InvalidDataException($"'{assembly.GetName().Name}' has no class implementing IHubPlugin.");
    }

    /// <summary>Отключает плагин от хаба и останавливает его. Освобождает всё, что держит его типы.</summary>
    /// <param name="token">Отменяется по таймауту остановки.</param>
    public async Task StopAsync(CancellationToken token)
    {
        _hub.Router.Unregister(Id, this);
        _hub.Router.UnsubscribeAll(this);
        _hub.Config.Changed -= OnConfigChanged;

        try
        {
            if (_instance is not null) await _instance.StopAsync(token);
        }
        finally
        {
            // Только теперь: при остановке плагин ещё вправе вызывать других, например чтобы вернуть всё как было.
            await _lifetime.CancelAsync();
            _events.Dispose();
            _handlers.Clear();
            Changed = null;
            _instance = null;
        }
    }

    /// <summary>Начинает выгрузку кода плагина. Закончится она, когда на него не останется ссылок.</summary>
    /// <returns>Слабая ссылка, по которой можно проверить, что выгрузка завершилась.</returns>
    public WeakReference? Unload()
    {
        if (_loadContext is null) return null;
        HubPayload.ResetJsonCache();
        var reference = new WeakReference(_loadContext);
        _loadContext.Unload();
        _loadContext = null;
        return reference;
    }

    // Реализация IEndpoint.

    /// <summary>Выполняет запрос к плагину. Ошибка плагина становится ответом с ошибкой и не затрагивает хаб.</summary>
    /// <param name="request">Запрос.</param>
    /// <param name="token">Отмена.</param>
    public async Task<Envelope> RequestAsync(Envelope request, CancellationToken token)
    {
        try
        {
            if (_handlers.TryGetValue(request.Method, out var handler))
            {
                var payload = new HubRequest(request.Source, request.Method, request.GetPayload());
                return Envelopes.Response(request, await handler(payload, token));
            }

            if (_instance is IUiProvider provider)
            {
                switch (request.Method)
                {
                    case "ui.get":
                        return Envelopes.Response(request, HubPayload.Proto(new UiResponse { Pages = { provider.GetPages() } }));
                    case "ui.event":
                        provider.OnUiEvent(request.GetPayload().AsProto<UiEvent>());
                        return Envelopes.Response(request, HubPayload.Empty);
                }
            }

            return Envelopes.Error(request, StatusCode.NotFound, $"'{Id}' has no method '{request.Method}'.");
        }
        catch (HubCallException ex)
        {
            return Envelopes.Error(request, ex.Status, ex.Message);
        }
        catch (Exception ex)
        {
            // Ошибка плагина — это ответ с ошибкой вызывающему, а не проблема хаба.
            Logger.Error($"Method '{request.Method}' failed", ex);
            return Envelopes.Error(request, StatusCode.Internal, ex.Message);
        }
    }

    /// <inheritdoc/>
    public void DeliverEvent(Envelope hubEvent) =>
        _events.Post(new HubEvent(hubEvent.Source, hubEvent.Method, hubEvent.GetPayload()));

    // Реализация IHubBus.

    /// <inheritdoc/>
    public async ValueTask<HubPayload> CallAsync(string target, string method, HubPayload payload = default,
        TimeSpan? timeout = null, CancellationToken token = default)
    {
        var request = Envelopes.Request(Id, target, method, payload);
        request.Id = Interlocked.Increment(ref _nextId);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        if (timeout is { } limit) linked.CancelAfter(limit);
        var response = await _hub.Router.RequestAsync(request, linked.Token);
        return response.Unwrap();
    }

    /// <inheritdoc/>
    public ValueTask PublishAsync(string topic, HubPayload payload = default, CancellationToken token = default)
    {
        _hub.Router.Publish(Id, topic, payload);
        return default;
    }

    /// <inheritdoc/>
    public ValueTask<IDisposable> SubscribeAsync(string topic, Action<HubEvent> handler, CancellationToken token = default)
    {
        if (_events.Add(topic, handler)) _hub.Router.Subscribe(topic, this);

        IDisposable subscription = new Disposer(() =>
        {
            if (_events.Remove(topic, handler)) _hub.Router.Unsubscribe(topic, this);
        });
        return ValueTask.FromResult(subscription);
    }

    /// <inheritdoc/>
    public IDisposable Handle(string method, HubRequestHandler handler)
    {
        _handlers[method] = handler;
        return new Disposer(() => _handlers.TryRemove(method, out _));
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<DependencyResolution>> ResolveAsync(IEnumerable<Dependency> dependencies,
        CancellationToken token = default)
    {
        var results = new List<DependencyResolution>();
        foreach (var dependency in dependencies) results.Add(await _hub.Plugins.ResolveAsync(dependency));
        return results;
    }

    // Реализация IPluginConfig.

    /// <inheritdoc/>
    public event Action<string>? Changed;

    /// <inheritdoc/>
    public T? Get<T>(string key, T? fallback = default)
    {
        var node = _hub.Config.Get(Id, key);
        return node is null ? fallback : node.Deserialize<T>(HubPayload.JsonOptions);
    }

    /// <inheritdoc/>
    public void Set<T>(string key, T value) =>
        _hub.Config.Set(Id, key, JsonSerializer.SerializeToNode(value, HubPayload.JsonOptions));

    /// <inheritdoc/>
    public JsonObject Snapshot() => _hub.Config.Snapshot(Id);

    /// <summary>Передаёт плагину изменения его настроек. Чужие изменения отбрасываются.</summary>
    private void OnConfigChanged(string owner, string key)
    {
        if (owner != Id) return;
        try
        {
            Changed?.Invoke(key);
        }
        catch (Exception ex)
        {
            Logger.Error("A config change handler failed", ex);
        }
    }

    // Реализация IPluginUiState.

    void IPluginUiState.Set(string key, string value) => _hub.UiState.Set(Id, key, value);

    /// <summary>Выполняет действие один раз при <see cref="IDisposable.Dispose"/>.</summary>
    private sealed class Disposer(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
