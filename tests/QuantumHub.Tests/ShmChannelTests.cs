using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using QuantumHub.Contracts.Ipc;
using QuantumHub.ProtoMemProtocol;
using QuantumHub.ProtoMemProtocol.Shm;
using Xunit;

namespace QuantumHub.Tests;

/// <summary>Каналы сами по себе, без хаба: порядок, большие кадры, конкуренция, закрытие.</summary>
public class ShmChannelTests
{
    private static byte[] Frame(byte[] body)
    {
        var frame = new byte[FrameCodec.HeaderSize + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame.AsSpan(FrameCodec.HeaderSize));
        return frame;
    }

    private static (ShmFrameChannel Server, ShmFrameChannel Client) Pair(uint ringSize)
    {
        var name = HubEndpoint.NewMappingName();
        var server = ShmFrameChannel.CreateServer(name, ringSize);
        return (server, ShmFrameChannel.OpenClient(name, ringSize));
    }

    [Fact]
    public async Task Delivers_frames_in_both_directions()
    {
        var (server, client) = Pair(4096);
        using var _ = server;
        using var __ = client;

        var atServer = new TaskCompletionSource<byte[]>();
        var atClient = new TaskCompletionSource<byte[]>();
        server.Start(body => atServer.TrySetResult(body.ToArray()), _ => { });
        client.Start(body => atClient.TrySetResult(body.ToArray()), _ => { });

        await client.SendAsync(Frame([1, 2, 3]));
        Assert.Equal([1, 2, 3], await atServer.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await server.SendAsync(Frame([4, 5]));
        Assert.Equal([4, 5], await atClient.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_frame_much_larger_than_the_ring_gets_through_intact()
    {
        var (server, client) = Pair(4096);
        using var _ = server;
        using var __ = client;

        var received = new TaskCompletionSource<byte[]>();
        server.Start(body => received.TrySetResult(body.ToArray()), _ => { });
        client.Start(_ => { }, _ => { });

        var payload = RandomNumberGenerator.GetBytes(8 * 1024 * 1024);
        await client.SendAsync(Frame(payload));

        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(payload.AsSpan().SequenceEqual(result));
    }

    [Fact]
    public async Task Keeps_order_across_many_ring_wraps()
    {
        const int count = 200_000;
        var (server, client) = Pair(4096);
        using var _ = server;
        using var __ = client;

        var next = 0;
        var failure = -1;
        var done = new TaskCompletionSource();
        server.Start(body =>
        {
            var value = BinaryPrimitives.ReadInt32LittleEndian(body);
            if (value != next && failure < 0) failure = next;
            if (++next == count) done.TrySetResult();
        }, _ => { });
        client.Start(_ => { }, _ => { });

        var body = new byte[37]; // Намеренно не делитель размера кольца.
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body, i);
            await client.SendAsync(Frame(body));
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(-1, failure);
    }

    [Fact]
    public async Task Concurrent_senders_do_not_interleave_frames()
    {
        const int senders = 8;
        const int perSender = 5_000;
        var (server, client) = Pair(4096);
        using var _ = server;
        using var __ = client;

        var counts = new int[senders];
        var received = 0;
        var corrupt = 0;
        var done = new TaskCompletionSource();
        server.Start(body =>
        {
            // Каждый байт кадра несёт номер отправителя.
            var sender = body[0];
            foreach (var b in body)
            {
                if (b != sender) corrupt++;
            }

            counts[sender]++;
            if (++received == senders * perSender) done.TrySetResult();
        }, _ => { });
        client.Start(_ => { }, _ => { });

        await Task.WhenAll(Enumerable.Range(0, senders).Select(sender => Task.Run(async () =>
        {
            var frame = Frame(Enumerable.Repeat((byte)sender, 300 + sender).ToArray());
            for (var i = 0; i < perSender; i++) await client.SendAsync(frame);
        })));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(0, corrupt);
        Assert.All(counts, count => Assert.Equal(perSender, count));
    }

    [Fact]
    public async Task A_sender_blocked_on_a_full_ring_is_released_when_the_channel_closes()
    {
        var (server, client) = Pair(4096);
        using var _ = server;

        client.Start(_ => { }, _ => { });
        // На сервере никто не читает, поэтому сама эта отправка не закончится никогда.
        var send = client.SendAsync(Frame(new byte[1024 * 1024])).AsTask();
        await Task.Delay(100);
        Assert.False(send.IsCompleted);

        client.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Starting_after_dispose_reports_closed_instead_of_reading_freed_memory()
    {
        var (server, client) = Pair(4096);
        using var _ = client;
        server.Dispose();

        var closed = new TaskCompletionSource();
        server.Start(_ => { }, _ => closed.TrySetResult());

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Sending_after_dispose_throws_instead_of_writing_to_freed_memory()
    {
        var (server, client) = Pair(4096);
        using var _ = server;
        client.Start(_ => { }, _ => { });
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await client.SendAsync(Frame([1])));
    }

    [Fact]
    public async Task The_lifeline_reports_a_dead_peer_at_once()
    {
        var pipeName = $"qh-test-{Guid.NewGuid():N}";
        var serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = serverPipe.WaitForConnectionAsync();
        await clientPipe.ConnectAsync(5000);
        await accept;

        using var server = ShmFrameChannel.CreateServer(HubEndpoint.NewMappingName(), 4096, serverPipe);
        var closed = new TaskCompletionSource();
        server.Start(_ => { }, _ => closed.TrySetResult());

        // То, что ядро делает за нас, когда другой процесс умирает по любой причине.
        await clientPipe.DisposeAsync();

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Pipe_channel_delivers_frames()
    {
        var pipeName = $"qh-test-{Guid.NewGuid():N}";
        var serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = serverPipe.WaitForConnectionAsync();
        await clientPipe.ConnectAsync(5000);
        await accept;

        using IFrameChannel server = new StreamFrameChannel(serverPipe, "pipe");
        using IFrameChannel client = new StreamFrameChannel(clientPipe, "pipe");
        var received = new TaskCompletionSource<byte[]>();
        server.Start(body => received.TrySetResult(body.ToArray()), _ => { });
        client.Start(_ => { }, _ => { });

        await client.SendAsync(Frame([9, 8, 7]));

        Assert.Equal([9, 8, 7], await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
