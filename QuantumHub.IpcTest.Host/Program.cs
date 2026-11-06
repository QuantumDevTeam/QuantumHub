using Google.Protobuf.WellKnownTypes;
using Quantum.Hub.Contracts.Proto.Core;
using QuantumHub.ProtoMemProtocol.Channel;
using QuantumHub.ProtoMemProtocol.Dispatch;
using QuantumHub.ProtoMemProtocol.Loop;

namespace QuantumHub.IpcTest.Host;

class Program
{
    static async Task Main()
    {
        var channelName = "test_channel_" + Guid.NewGuid().ToString("N");
        Console.WriteLine("[Server] channelName: " + channelName);
        var factory = new ChannelFactory();

        Console.WriteLine("[Server] Creating server channel...");
        var serverChannel = factory.CreateServer(channelName);
        await serverChannel.ConnectAsync();

        Console.WriteLine("[Server] Creating server channel dispatcher...");
        var serverDispatcher = new ChannelDispatcher();
        serverDispatcher.RegisterChannel("main", serverChannel.Messenger);

        Console.WriteLine("[Server] Creating event loop...");
        var serverLoop = new EventLoop();
        
        using var cts = new CancellationTokenSource();
        
        Console.WriteLine("[Server] Run loop...");
        await serverLoop.RunAsync(serverChannel, async msg =>
        {
            Console.WriteLine($"[Server] Received: {msg.Action}, \"{msg.Payload.Unpack<StringValue>().Value}\"");
            HubMessage response;
            
            if (msg.Action == "check")
            {
                response = new HubMessage
                {
                    MessageId = msg.MessageId + 1,
                    SourcePluginId = "server",
                    TargetPluginId = msg.SourcePluginId,
                    Action = "check_response",
                    Payload = Any.Pack(new StringValue { Value = "Loud and clear. Over." }),
                    TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }
            else if (msg.Action == "mission_def")
            {
                response = new HubMessage
                {
                    MessageId = msg.MessageId + 1,
                    SourcePluginId = "server",
                    TargetPluginId = msg.SourcePluginId,
                    Action = "mission_def_response",
                    Payload = Any.Pack(new StringValue { Value = "Acknowledged. Over." }),
                    TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }
            else if (msg.Action == "STOP")
            {
                response = new HubMessage
                {
                    MessageId = msg.MessageId + 1,
                    SourcePluginId = "server",
                    TargetPluginId = msg.SourcePluginId,
                    Action = "STOP_response",
                    Payload = Any.Pack(new StringValue { Value = "Negative, I'm still talking. Over." }),
                    TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                await cts.CancelAsync();
            }
            else
            {
                response = new HubMessage
                {
                    MessageId = msg.MessageId + 1,
                    SourcePluginId = "server",
                    TargetPluginId = msg.SourcePluginId,
                    Action = "echo_response",
                    Payload = Any.Pack(new StringValue { Value = "10-4. Copy that. Over." }),
                    TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }
            await serverDispatcher.SendAsync("main", response, cts.Token);
        }, cts.Token);

        Console.WriteLine("Test completed successfully.");
    }
}