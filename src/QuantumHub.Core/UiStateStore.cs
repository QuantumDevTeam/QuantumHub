using System.Collections.Concurrent;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Proto;
using QuantumHub.Core.Routing;

namespace QuantumHub.Core;

/// <summary>
/// Последнее состояние интерфейса каждого плагина. Хранится в хабе, чтобы GUI, открытый позже или
/// переподключившийся, сразу видел текущие значения, а плагину не пришлось отправлять их заново.
/// </summary>
/// <param name="router">Роутер для рассылки изменений.</param>
public sealed class UiStateStore(Router router)
{
    /// <summary>Топик изменений.</summary>
    public const string Topic = "hub.ui.state";

    /// <summary>Значения: id плагина, затем ключ.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _state = new();

    /// <summary>Запоминает значение и рассылает его.</summary>
    /// <param name="pluginId">Id плагина.</param>
    /// <param name="key">Ключ.</param>
    /// <param name="value">Значение.</param>
    public void Set(string pluginId, string key, string value)
    {
        _state.GetOrAdd(pluginId, _ => new())[key] = value;
        router.Publish(pluginId, Topic, HubPayload.Proto(new UiStatePatch
        {
            PluginId = pluginId,
            State = { { key, value } },
        }));
    }

    /// <summary>Текущее состояние плагина.</summary>
    /// <param name="pluginId">Id плагина.</param>
    public IEnumerable<KeyValuePair<string, string>> Get(string pluginId) =>
        _state.TryGetValue(pluginId, out var state) ? state : [];
}
