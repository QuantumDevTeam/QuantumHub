using Quantum.Hub.Contracts.Proto.Core;

namespace QuantumHub.Contracts.Ipc;

public interface IChannelDispatcher
{
    void RegisterChannel(string channelId, IMessenger messenger);
    Task SendAsync(string channelId, HubMessage message, CancellationToken token = default);
    Task BroadcastAsync(HubMessage message, IEnumerable<string> channelIds, CancellationToken token = default);
}