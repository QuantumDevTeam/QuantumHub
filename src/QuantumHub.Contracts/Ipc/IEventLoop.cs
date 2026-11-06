using Quantum.Hub.Contracts.Proto.Core;

namespace QuantumHub.Contracts.Ipc;

public interface IEventLoop
{
    Task RunAsync(IChannel channel, Func<HubMessage, Task> handler, CancellationToken token = default);
    Task StartAsync(IChannel channel, Func<HubMessage, Task> handler, CancellationToken token = default);
    Task StopAsync();
}