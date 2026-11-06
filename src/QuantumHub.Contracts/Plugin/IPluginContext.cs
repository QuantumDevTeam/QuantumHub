using QuantumHub.Contracts.Core;

namespace QuantumHub.Contracts.Plugin;

public interface IPluginContext
{
    IActionDispatcher Dispatcher { get; }
    IPluginRegistry Registry { get; }
    ILogger Logger { get; }
}