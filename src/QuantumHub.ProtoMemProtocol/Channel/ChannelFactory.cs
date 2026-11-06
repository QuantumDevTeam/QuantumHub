using QuantumHub.Contracts.Ipc;
using QuantumHub.ProtoMemProtocol.Memory;

namespace QuantumHub.ProtoMemProtocol.Channel;

public class ChannelFactory(IMemoryBlockFactory blockFactory)
    : IChannelFactory
{
    public ChannelFactory() : this(new MemoryBlockFactory())
    {
    }

    public IChannel CreateServer(string channelName, int bufferSize = 65536)
    {
        var inputBlock = blockFactory.Create($"{channelName}_to_server", bufferSize, true);
        var outputBlock = blockFactory.Create($"{channelName}_to_client", bufferSize, true);
        return new Channel(channelName, true, inputBlock, outputBlock);
    }

    public IChannel CreateClient(string channelName, int bufferSize = 65536)
    {
        var inputBlock = blockFactory.Create($"{channelName}_to_client", bufferSize, false);
        var outputBlock = blockFactory.Create($"{channelName}_to_server", bufferSize, false);
        return new Channel(channelName, false, inputBlock, outputBlock);
    }
}