using Google.Protobuf;
using Quantum.Hub.Contracts.Proto.Core;
using QuantumHub.Contracts.Ipc;

namespace QuantumHub.ProtoMemProtocol.Messaging;

public class ProtobufMessenger(IMemoryBlock input, IMemoryBlock output)
    : IMessenger
{
    public void Send(HubMessage message)
    {
        var data = message.ToByteArray();
        output.Write(data);
        output.SignalDataWritten();
    }

    public Task SendAsync(HubMessage message, CancellationToken token = default)
    {
        Send(message);
        return Task.CompletedTask;
    }

    public async Task<HubMessage> ReceiveAsync(CancellationToken token = default)
    {
        await input.WaitForDataAsync(token);
        Span<byte> buffer = new byte[input.Capacity];
        var len = input.Read(buffer);
        return HubMessage.Parser.ParseFrom(buffer[..len]);
    }
}