using System.Buffers;
using System.Buffers.Binary;
using QuantumHub.Contracts.Ipc;

namespace QuantumHub.ProtoMemProtocol;

/// <summary>Канал кадров поверх любого двустороннего потока: named pipe или TCP-сокета.</summary>
/// <remarks>Медленнее <see cref="Shm.ShmFrameChannel"/>, зато работает из любого языка, потому что pipe открывается как обычный файл.</remarks>
/// <param name="stream">Подключённый поток. Канал становится его владельцем.</param>
/// <param name="transport">Название транспорта для отображения: "pipe" или "tcp".</param>
public sealed class StreamFrameChannel(Stream stream, string transport) : IFrameChannel
{
    /// <summary>Не даёт кадрам из разных потоков перемешаться в байтовом потоке.</summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    /// <inheritdoc/>
    public string Transport { get; } = transport;

    /// <inheritdoc/>
    public void Start(FrameHandler onFrame, Action<Exception?> onClosed) =>
        _ = Task.Run(() => ReadLoopAsync(onFrame, onClosed));

    /// <summary>Читает кадры, пока соединение живо. Обычный обрыв не считается ошибкой.</summary>
    /// <param name="onFrame">Получатель кадров.</param>
    /// <param name="onClosed">Вызывается один раз, когда цикл закончился.</param>
    private async Task ReadLoopAsync(FrameHandler onFrame, Action<Exception?> onClosed)
    {
        Exception? failure = null;
        var header = new byte[FrameCodec.HeaderSize];
        var body = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, _cts.Token);
                var size = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (size < 0 || size > FrameCodec.MaxBodySize)
                    throw new InvalidDataException($"Bad frame size {size}.");

                if (size > body.Length)
                {
                    ArrayPool<byte>.Shared.Return(body);
                    body = ArrayPool<byte>.Shared.Rent(size);
                }

                await stream.ReadExactlyAsync(body.AsMemory(0, size), _cts.Token);
                onFrame(body.AsSpan(0, size));
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or OperationCanceledException or ObjectDisposedException)
        {
            // Обычные способы закончить соединение, ошибкой они не считаются.
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }

        onClosed(failure);
    }

    /// <inheritdoc/>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken token = default)
    {
        await _sendLock.WaitAsync(token);
        try
        {
            await stream.WriteAsync(frame, token);
            await stream.FlushAsync(token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Закрывает поток, цикл чтения завершается сам.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        stream.Dispose();
    }
}
