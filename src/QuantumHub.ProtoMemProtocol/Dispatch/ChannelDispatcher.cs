using System.Collections.Concurrent;
using Quantum.Hub.Contracts.Proto.Core;
using QuantumHub.Contracts.Ipc;

namespace QuantumHub.ProtoMemProtocol.Dispatch;

public class ChannelDispatcher 
    : IChannelDispatcher
{
    private readonly ConcurrentDictionary<string, IMessenger> _channels = new();

    public void RegisterChannel(string channelId, IMessenger messenger)
    {
        if (!_channels.TryAdd(channelId, messenger))
            throw new InvalidOperationException($"Channel '{channelId}' already registered.");
    }

    public async Task SendAsync(string channelId, HubMessage message, CancellationToken token = default)
    {
        if (!_channels.TryGetValue(channelId, out var messenger))
            throw new KeyNotFoundException($"Channel '{channelId}' not registered.");
        await messenger.SendAsync(message, token);
    }

    public async Task BroadcastAsync(HubMessage message, IEnumerable<string> channelIds, CancellationToken token = default)
    {
        var tasks = channelIds.Select(id => SendAsync(id, message, token));
        await Task.WhenAll(tasks);
    }
}