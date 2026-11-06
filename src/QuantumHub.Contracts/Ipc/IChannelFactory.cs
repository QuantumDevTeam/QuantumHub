namespace QuantumHub.Contracts.Ipc;

public interface IChannelFactory
{
    IChannel CreateServer(string channelName, int bufferSize = 65536);
    IChannel CreateClient(string channelName, int bufferSize = 65536);
}