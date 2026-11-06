using QuantumHub.Contracts.Plugin;

namespace QuantumHub.Contracts.Core;

public interface IPluginRegistry
{
    IReadOnlyList<IPlugin> LoadedPlugins { get; }

    IPlugin GetPlugin(string pluginId);
}