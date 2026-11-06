using Quantum.Hub.Contracts.Proto.Core;

namespace QuantumHub.Contracts.Ipc;

public interface IMessenger
{
    Task SendAsync(HubMessage message, CancellationToken token = default);
    Task<HubMessage> ReceiveAsync(CancellationToken token = default);
}