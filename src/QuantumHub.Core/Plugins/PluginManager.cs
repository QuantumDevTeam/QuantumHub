using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Proto;
using QuantumHub.Core.Routing;

namespace QuantumHub.Core.Plugins;

/// <summary>Владеет жизненным циклом DLL-плагинов: поиск, порядок зависимостей, запуск, остановка, установка.</summary>
/// <remarks>
/// <para>
/// Запуск идёт через <see cref="EnsureStartedAsync"/>: он сначала поднимает зависимости, а одновременные
/// вызовы получают одну и ту же задачу. Остановка идёт в обратном порядке: сначала останавливаются зависимые,
/// они переходят в состояние Blocked и поднимаются сами, когда зависимость вернётся.
/// </para>
/// <para>Состояния плагина перечислены в <see cref="PluginState"/>, каждый переход публикуется как событие.</para>
/// </remarks>
/// <param name="hub">Хаб.</param>
public sealed class PluginManager(Hub hub)
{
    /// <summary>Ключ в конфиге хаба со списком выключенных плагинов.</summary>
    private const string DisabledKey = "disabledPlugins";
    /// <summary>Значение "activation", при котором плагин стартует только по требованию.</summary>
    private const string OnDemandActivation = "onDemand";

    /// <summary>Сколько ждать запуска плагина.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    /// <summary>Сколько ждать остановки плагина.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Защищает <see cref="_entries"/> и поля записей.</summary>
    private readonly object _gate = new();
    /// <summary>Все известные плагины.</summary>
    private readonly Dictionary<string, Entry> _entries = new();

    // Упорядочивает действия пользователя: включение, установку и прочее. Запуск плагина её намеренно
    // не берёт: плагин может запустить другой плагин прямо из своего StartAsync.
    private readonly SemaphoreSlim _admin = new(1, 1);

    /// <summary>Поставщики вне процесса хаба, то есть подключённые клиенты.</summary>
    public Func<IEnumerable<ProviderCandidate>> ExternalProviders { get; set; } = () => [];

    /// <summary>Запись о плагине.</summary>
    /// <param name="package">Пакет на диске.</param>
    private sealed class Entry(PluginPackage package)
    {
        public PluginPackage Package { get; set; } = package;
        public PluginState State { get; set; } = PluginState.Stopped;
        public string Error { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        /// <summary>Идущий или завершившийся запуск. Общий для всех, кто ждёт старта.</summary>
        public Task? StartTask { get; set; }
        /// <summary>Запущенный плагин.</summary>
        public LoadedPlugin? Loaded { get; set; }
        public PluginManifest Manifest => Package.Manifest;
    }

    /// <summary>Находит плагины на диске и применяет сохранённый список выключенных.</summary>
    public void Discover()
    {
        var disabled = LoadDisabled();
        var packages = PluginCatalog.Discover(hub.Paths.PluginDirs, hub.Log);
        lock (_gate)
        {
            foreach (var package in packages)
            {
                if (_entries.TryGetValue(package.Manifest.Id, out var existing) && existing.Loaded is not null) continue;

                var entry = new Entry(package) { Enabled = !disabled.Contains(package.Manifest.Id) };
                if (package.Error is not null)
                {
                    entry.State = PluginState.Faulted;
                    entry.Error = package.Error;
                }
                else if (!entry.Enabled)
                {
                    entry.State = PluginState.Disabled;
                }

                _entries[package.Manifest.Id] = entry;
                hub.Config.ApplyDefaults(package.Manifest);
            }
        }

        hub.Log.Info($"Found {packages.Count} plugin(s).");
    }

    /// <summary>Читает список выключенных плагинов.</summary>
    private HashSet<string> LoadDisabled()
    {
        var node = hub.Config.Get(Hub.ConfigOwner, DisabledKey);
        return node is System.Text.Json.Nodes.JsonArray array
            ? array.Select(item => item?.GetValue<string>() ?? string.Empty).ToHashSet()
            : [];
    }

    /// <summary>Сохраняет список выключенных плагинов.</summary>
    private void SaveDisabled()
    {
        lock (_gate)
        {
            var array = new System.Text.Json.Nodes.JsonArray();
            foreach (var entry in _entries.Values.Where(entry => !entry.Enabled)) array.Add(entry.Manifest.Id);
            hub.Config.Set(Hub.ConfigOwner, DisabledKey, array);
        }
    }

    /// <summary>Запускает всё, что должно стартовать сразу. Сбой одного плагина не затрагивает остальных.</summary>
    public async Task StartAllAsync()
    {
        List<string> ids;
        lock (_gate)
        {
            ids = _entries.Values
                .Where(entry => entry.Enabled && entry.Package.Error is null && entry.Manifest.Activation != OnDemandActivation)
                .Select(entry => entry.Manifest.Id)
                .ToList();
        }

        await Task.WhenAll(ids.Select(async id =>
        {
            try
            {
                await EnsureStartedAsync(id);
            }
            catch (HubCallException)
            {
                // Уже записано в запись о плагине и в журнал.
            }
        }));
    }

    /// <summary>Установлен ли плагин.</summary>
    /// <param name="id">Id плагина.</param>
    public bool IsKnown(string id)
    {
        lock (_gate) return _entries.ContainsKey(id);
    }

    /// <summary>Манифест плагина.</summary>
    /// <param name="id">Id плагина.</param>
    public PluginManifest? GetManifest(string id)
    {
        lock (_gate) return _entries.TryGetValue(id, out var entry) ? entry.Manifest : null;
    }

    /// <summary>Папка плагина.</summary>
    /// <param name="id">Id плагина.</param>
    public string? GetDirectory(string id)
    {
        lock (_gate) return _entries.TryGetValue(id, out var entry) ? entry.Package.Directory : null;
    }

    /// <summary>Работает ли плагин прямо сейчас.</summary>
    /// <param name="id">Id плагина.</param>
    public bool IsRunning(string id)
    {
        lock (_gate) return _entries.TryGetValue(id, out var entry) && entry.State == PluginState.Running;
    }

    /// <summary>Все плагины с состояниями.</summary>
    public PluginList List()
    {
        lock (_gate)
        {
            var list = new PluginList();
            list.Plugins.Add(_entries.Values.OrderBy(entry => entry.Manifest.Name, StringComparer.CurrentCulture).Select(ToInfo));
            return list;
        }
    }

    /// <summary>Собирает сведения о плагине для клиентов.</summary>
    private PluginInfo ToInfo(Entry entry)
    {
        var info = new PluginInfo
        {
            Manifest = entry.Manifest,
            State = entry.State,
            Error = entry.Error,
            Enabled = entry.Enabled,
            Path = entry.Package.Directory,
        };
        info.Dependents.Add(RequiredDependents(entry.Manifest.Id).Select(dependent => dependent.Manifest.Id));
        return info;
    }

    /// <summary>Все возможные поставщики: плагины и подключённые клиенты.</summary>
    private List<ProviderCandidate> Candidates()
    {
        var candidates = new List<ProviderCandidate>();
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                var reason = entry.Package.Error is not null ? entry.Package.Error
                    : !entry.Enabled ? "the plugin is disabled"
                    : null;
                candidates.Add(new ProviderCandidate(entry.Manifest.Id, entry.Manifest.Version, entry.Manifest.Provides, reason));
            }
        }

        candidates.AddRange(ExternalProviders());
        return candidates;
    }

    /// <summary>Кто предоставляет возможность.</summary>
    /// <param name="capability">Имя возможности.</param>
    /// <returns>Id поставщика или <see langword="null"/>.</returns>
    public string? FindCapabilityProvider(string capability)
    {
        var result = DependencyResolver.Match(new Dependency { Capability = capability }, Candidates());
        return result.State == DependencyState.Resolved ? result.ProviderId : null;
    }

    /// <summary>Находит поставщика и следит, чтобы он действительно работал: запускает его при необходимости.</summary>
    /// <param name="dependency">Зависимость.</param>
    public async Task<DependencyResolution> ResolveAsync(Dependency dependency)
    {
        var result = DependencyResolver.Match(dependency, Candidates());
        if (result.State != DependencyState.Resolved || !IsKnown(result.ProviderId)) return result;

        try
        {
            await EnsureStartedAsync(result.ProviderId);
        }
        catch (HubCallException ex)
        {
            result.State = DependencyState.Failed;
            result.Detail = ex.Message;
        }

        return result;
    }

    /// <summary>Запускает плагин и всё, что ему нужно. Одновременные вызовы получают одну попытку на всех.</summary>
    /// <param name="id">Id плагина.</param>
    /// <exception cref="HubCallException">Плагин не удалось запустить.</exception>
    public Task EnsureStartedAsync(string id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
                return Task.FromException(new HubCallException(StatusCode.NotFound, $"Plugin '{id}' is not installed."));
            if (entry.Package.Error is not null)
                return Task.FromException(new HubCallException(StatusCode.FailedPrecondition, $"Plugin '{id}' is broken: {entry.Package.Error}"));
            if (!entry.Enabled)
                return Task.FromException(new HubCallException(StatusCode.FailedPrecondition, $"Plugin '{id}' is disabled."));

            return entry.StartTask ??= StartCoreAsync(entry);
        }
    }

    /// <summary>Сам запуск: проверка цикла, зависимости, загрузка.</summary>
    private async Task StartCoreAsync(Entry entry)
    {
        await Task.Yield();
        var id = entry.Manifest.Id;
        try
        {
            if (DependencyResolver.FindCycle(id, InitEdges) is { } cycle)
                throw new DependencyBlockedException($"Dependency cycle: {string.Join(" -> ", cycle)}");

            await StartDependenciesAsync(entry);

            SetState(entry, PluginState.Starting);
            hub.Log.Info($"Starting {id} {entry.Manifest.Version}");

            var loaded = new LoadedPlugin(hub, entry.Package);
            entry.Loaded = loaded;
            using var timeout = new CancellationTokenSource(StartTimeout);
            await loaded.StartAsync(timeout.Token).WaitAsync(timeout.Token);

            SetState(entry, PluginState.Running);
        }
        catch (DependencyBlockedException ex)
        {
            Fail(entry, PluginState.Blocked, ex.Message);
            throw new HubCallException(StatusCode.FailedPrecondition, $"'{id}' cannot start: {ex.Message}");
        }
        catch (Exception ex)
        {
            await DiscardAsync(entry);
            Fail(entry, PluginState.Faulted, ex.Message);
            hub.Log.Error($"{id} failed to start: {ex}");
            throw new HubCallException(StatusCode.Internal, $"'{id}' failed to start: {ex.Message}");
        }
    }

    /// <summary>Запускает init-зависимости. Недоступная необязательная зависимость только попадает в журнал.</summary>
    private async Task StartDependenciesAsync(Entry entry)
    {
        foreach (var dependency in entry.Manifest.Dependencies.Where(DependencyResolver.IsInitTime))
        {
            var result = await ResolveAsync(dependency);
            if (result.State == DependencyState.Resolved) continue;

            if (dependency.Optional)
                hub.Log.Info($"{entry.Manifest.Id}: optional dependency unavailable: {result.Detail}");
            else
                throw new DependencyBlockedException(result.Detail);
        }
    }

    /// <summary>Кого плагин обязан дождаться. Рёбра графа для проверки циклов.</summary>
    private IEnumerable<string> InitEdges(string id)
    {
        var manifest = GetManifest(id);
        if (manifest is null) return [];

        var candidates = Candidates();
        return manifest.Dependencies
            .Where(dependency => DependencyResolver.IsInitTime(dependency) && !dependency.Optional)
            .Select(dependency => DependencyResolver.Match(dependency, candidates).ProviderId)
            .Where(provider => provider.Length > 0)
            .ToList();
    }

    /// <summary>Кто не может работать без этого плагина.</summary>
    private List<Entry> RequiredDependents(string id)
    {
        var candidates = Candidates();
        lock (_gate)
        {
            return _entries.Values
                .Where(entry => entry.Manifest.Id != id && entry.Manifest.Dependencies.Any(dependency =>
                    DependencyResolver.IsInitTime(dependency) && !dependency.Optional &&
                    DependencyResolver.Match(dependency, candidates).ProviderId == id))
                .ToList();
        }
    }

    /// <summary>Останавливает плагин, а перед этим всех, кто от него зависит.</summary>
    /// <param name="entry">Плагин.</param>
    /// <param name="finalState">Состояние после остановки.</param>
    /// <param name="reason">Причина для показа пользователю.</param>
    private async Task StopCoreAsync(Entry entry, PluginState finalState, string reason = "")
    {
        foreach (var dependent in RequiredDependents(entry.Manifest.Id))
        {
            if (dependent.Loaded is not null)
                await StopCoreAsync(dependent, PluginState.Blocked, $"'{entry.Manifest.Id}' was stopped.");
        }

        Task? pendingStart;
        lock (_gate) pendingStart = entry.StartTask;
        if (pendingStart is not null)
        {
            try { await pendingStart; } catch (HubCallException) { }
        }

        if (entry.Loaded is not null)
        {
            SetState(entry, PluginState.Stopping);
            hub.Log.Info($"Stopping {entry.Manifest.Id}");
            await DiscardAsync(entry);
        }

        lock (_gate) entry.StartTask = null;
        entry.Error = reason;
        SetState(entry, finalState);
    }

    /// <summary>Останавливает и выгружает. Зависший плагин не задерживает хаб дольше таймаута.</summary>
    private async Task DiscardAsync(Entry entry)
    {
        var loaded = entry.Loaded;
        if (loaded is null) return;
        entry.Loaded = null;

        try
        {
            using var timeout = new CancellationTokenSource(StopTimeout);
            await loaded.StopAsync(timeout.Token).WaitAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            hub.Log.Warning($"{entry.Manifest.Id} did not stop cleanly: {ex.Message}");
        }

        loaded.Unload();
    }

    /// <summary>Отмечает неудачный запуск, разрешая повторить его.</summary>
    private void Fail(Entry entry, PluginState state, string error)
    {
        lock (_gate) entry.StartTask = null;
        entry.Error = error;
        SetState(entry, state);
    }

    /// <summary>Меняет состояние и сообщает подписчикам.</summary>
    private void SetState(Entry entry, PluginState state)
    {
        PluginInfo info;
        lock (_gate)
        {
            entry.State = state;
            info = ToInfo(entry);
        }

        hub.Router.Publish(Hub.ConfigOwner, Hub.PluginStateTopic, HubPayload.Proto(info));
    }

    /// <summary>Включает или выключает плагин. Выбор сохраняется между запусками.</summary>
    /// <param name="id">Id плагина.</param>
    /// <param name="enabled">Новое значение.</param>
    public async Task SetEnabledAsync(string id, bool enabled)
    {
        await _admin.WaitAsync();
        try
        {
            var entry = GetEntry(id);
            if (entry.Enabled == enabled) return;

            entry.Enabled = enabled;
            SaveDisabled();

            if (!enabled)
            {
                await StopCoreAsync(entry, PluginState.Disabled);
                return;
            }

            entry.Error = string.Empty;
            SetState(entry, PluginState.Stopped);
            if (entry.Manifest.Activation != OnDemandActivation) await TryStartAsync(id);
            await RetryBlockedAsync();
        }
        finally
        {
            _admin.Release();
        }
    }

    /// <summary>Перезапускает плагин.</summary>
    /// <param name="id">Id плагина.</param>
    public async Task RestartAsync(string id)
    {
        await _admin.WaitAsync();
        try
        {
            await StopCoreAsync(GetEntry(id), PluginState.Stopped);
            await EnsureStartedAsync(id);
            await RetryBlockedAsync();
        }
        finally
        {
            _admin.Release();
        }
    }

    /// <summary>Устанавливает или обновляет плагин без перезапуска хаба.</summary>
    /// <param name="source">Архив, DLL или папка.</param>
    /// <returns>Сведения об установленном плагине.</returns>
    public async Task<PluginInfo> InstallAsync(string source)
    {
        await _admin.WaitAsync();
        try
        {
            var incoming = PluginCatalog.Inspect(source);
            if (incoming is not null && TryGetEntry(incoming, out var previous))
            {
                await StopCoreAsync(previous, PluginState.Stopped);
                await WaitForUnloadAsync();
            }

            var package = PluginCatalog.Install(source, hub.Paths.InstallTargetDir());
            var id = package.Manifest.Id;

            Entry entry;
            lock (_gate)
            {
                entry = new Entry(package);
                _entries[id] = entry;
            }

            SaveDisabled();
            hub.Config.ApplyDefaults(package.Manifest);
            hub.Log.Info($"Installed {id} {package.Manifest.Version}");
            SetState(entry, PluginState.Stopped);

            if (package.Manifest.Activation != OnDemandActivation) await TryStartAsync(id);
            await RetryBlockedAsync();

            lock (_gate) return ToInfo(entry);
        }
        finally
        {
            _admin.Release();
        }
    }

    /// <summary>Удаляет плагин. Его настройки остаются в папке пользователя.</summary>
    /// <param name="id">Id плагина.</param>
    public async Task UninstallAsync(string id)
    {
        await _admin.WaitAsync();
        try
        {
            var entry = GetEntry(id);
            await StopCoreAsync(entry, PluginState.Stopped);
            await WaitForUnloadAsync();

            lock (_gate) _entries.Remove(id);

            // Файлы могут остаться заблокированными, если плагин оставил что-то, что держит его код.
            // Тогда они удалятся при следующем запуске, а из хаба плагин пропадает в любом случае.
            try
            {
                Directory.Delete(entry.Package.Directory, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                hub.Log.Warning($"Files of {id} are in use and stay until the next restart: {ex.Message}");
                File.WriteAllText(Path.Combine(entry.Package.Directory, PluginCatalog.PendingDeleteMarker), string.Empty);
            }

            hub.Log.Info($"Uninstalled {id}");
            hub.Router.Publish(Hub.ConfigOwner, Hub.PluginRemovedTopic, HubPayload.Text(id));
        }
        finally
        {
            _admin.Release();
        }
    }

    /// <summary>Останавливает всё при завершении хаба.</summary>
    public async Task StopAllAsync()
    {
        List<Entry> entries;
        lock (_gate) entries = _entries.Values.ToList();

        foreach (var entry in entries)
        {
            if (entry.Loaded is not null) await StopCoreAsync(entry, PluginState.Stopped);
        }
    }

    /// <summary>Пробует запустить, неудача попадает только в журнал.</summary>
    private async Task TryStartAsync(string id)
    {
        try
        {
            await EnsureStartedAsync(id);
        }
        catch (HubCallException ex)
        {
            hub.Log.Warning(ex.Message);
        }
    }

    /// <summary>Изменение могло разблокировать плагины, ждущие зависимость.</summary>
    private async Task RetryBlockedAsync()
    {
        List<string> ids;
        lock (_gate)
        {
            ids = _entries.Values
                .Where(entry => entry is { State: PluginState.Blocked, Enabled: true })
                .Select(entry => entry.Manifest.Id)
                .ToList();
        }

        foreach (var id in ids) await TryStartAsync(id);
    }

    /// <summary>Даёт среде выполнения время отпустить файлы плагина после выгрузки.</summary>
    private static async Task WaitForUnloadAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(20);
        }
    }

    /// <summary>Находит запись по пакету.</summary>
    private bool TryGetEntry(PluginPackage package, out Entry entry)
    {
        lock (_gate) return _entries.TryGetValue(package.Manifest.Id, out entry!);
    }

    /// <summary>Находит запись или сообщает, что плагин не установлен.</summary>
    private Entry GetEntry(string id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry)
                ? entry
                : throw new HubCallException(StatusCode.NotFound, $"Plugin '{id}' is not installed.");
        }
    }

    /// <summary>Запуску мешает зависимость. Это не сбой плагина, поэтому состояние будет Blocked, а не Faulted.</summary>
    private sealed class DependencyBlockedException(string message) : Exception(message);
}
