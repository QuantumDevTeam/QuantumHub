using Quantum.Hub.Contracts.Proto.Plugin;

namespace QuantumHub.Contracts.Plugin;

public interface IPlugin
{
    string PlugnId { get; }
    PluginManifest Manifest { get; }
    Task InitializeAsync(IPluginContext context);
    Task ShutdownAsync();
}