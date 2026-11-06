namespace QuantumHub.Contracts.Ipc;

public interface IChannel : IDisposable
{
    string ChannelName { get; }
    bool IsOwner { get; }
    Task ConnectAsync();
    Task DisconnectAsync();
    IMessenger Messenger { get; }
}