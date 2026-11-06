using System.Diagnostics;
using Google.Protobuf.WellKnownTypes;
using Quantum.Hub.Contracts.Proto.Core;
using QuantumHub.Contracts.Ipc;
using QuantumHub.ProtoMemProtocol.Channel;
using QuantumHub.ProtoMemProtocol.Dispatch;
using QuantumHub.ProtoMemProtocol.Loop;

namespace QuantumHub.IpcTest.Client;

class Program
{
    static async Task TestOnce(uint messageId, IChannelDispatcher clientDispatcher, CancellationToken cancellationToken)
    {
        var request = new HubMessage
        {
            MessageId = messageId,
            SourcePluginId = "client",
            TargetPluginId = "server",
            Action = "ping",
            Payload = Any.Pack(new StringValue { Value = "Signal Check. Over." }),
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await clientDispatcher.SendAsync("main", request, cancellationToken);
    }

    static async Task Main()
    {
        Console.Write("Enter channelName:\n>");
        var channelName = Console.ReadLine() ?? throw new InvalidOperationException();
        var factory = new ChannelFactory();

        Console.WriteLine("[Client] Creating client channel...");
        var clientChannel = factory.CreateClient(channelName);
        await clientChannel.ConnectAsync();

        Console.WriteLine("[Client] Creating client channel dispatcher...");
        var clientDispatcher = new ChannelDispatcher();
        clientDispatcher.RegisterChannel("main", clientChannel.Messenger);

        Console.WriteLine("[Client] Creating event loop...");
        var clientLoop = new EventLoop();

        using var cts = new CancellationTokenSource();

        Console.WriteLine("[Client] Run loop...");
        await clientLoop.StartAsync(clientChannel, msg =>
        {
            Console.WriteLine($"[Client] Received: {msg.Action}, \"{msg.Payload.Unpack<StringValue>().Value}\"");
            return Task.CompletedTask;
        }, cts.Token);

        HubMessage serviceMsg;

        serviceMsg = new HubMessage
        {
            MessageId = 1,
            SourcePluginId = "client",
            TargetPluginId = "server",
            Action = "check",
            Payload = Any.Pack(new StringValue { Value = "Radio Check, Over." }),
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await clientDispatcher.SendAsync("main", serviceMsg, cts.Token);

        Console.WriteLine("[Client] All ready, press any key to start test...");
        Console.ReadKey();
        
        int ticks = 100;
        
        serviceMsg = new HubMessage
        {
            MessageId = 1,
            SourcePluginId = "client",
            TargetPluginId = "server",
            Action = "mission_def",
            Payload = Any.Pack(new StringValue { Value = $"{ticks} MSG. Mission brief: Send payload. Over." }),
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await clientDispatcher.SendAsync("main", serviceMsg, cts.Token);
        
        var sw = new Stopwatch();
        long totalTicks = 0;

        for (uint i = 1; i < ticks + 1; i++)
        {
            sw.Restart();
            await TestOnce(i + 1, clientDispatcher, cts.Token);
            sw.Stop();

            totalTicks += sw.ElapsedTicks;
            Thread.Sleep(100);
        }
        
        serviceMsg = new HubMessage
        {
            MessageId = 100 + 2,
            SourcePluginId = "client",
            TargetPluginId = "server",
            Action = "STOP",
            Payload = Any.Pack(new StringValue { Value = "Roger. Out." }),
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await clientDispatcher.SendAsync("main", serviceMsg, cts.Token);

        double totalSec = (double)totalTicks / Stopwatch.Frequency;
        Console.WriteLine("\nTest completed successfully. Time: " + totalSec);
    }
}