using QuantumHub.Contracts.Ipc;
using QuantumHub.ProtoMemProtocol.Messaging;

namespace QuantumHub.ProtoMemProtocol.Channel;

public class Channel(string channelName, bool isOwner, IMemoryBlock inputBlock, IMemoryBlock outputBlock)
    : IChannel
{
    private bool _connected;

    public string ChannelName { get; } = channelName;
    public bool IsOwner { get; } = isOwner;
    public IMessenger Messenger { get; } = new ProtobufMessenger(inputBlock, outputBlock);

    public Task ConnectAsync()
    {
        if (_connected) return Task.CompletedTask;
        _connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        if (!_connected) return Task.CompletedTask;
        _connected = false;
        inputBlock.Dispose();
        outputBlock.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        inputBlock.Dispose();
        outputBlock.Dispose();
    }
}