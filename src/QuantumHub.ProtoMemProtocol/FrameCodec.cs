using System.Buffers;
using System.Buffers.Binary;
using Google.Protobuf;
using QuantumHub.Contracts.Ipc;
using QuantumHub.Contracts.Proto;

namespace QuantumHub.ProtoMemProtocol;

/// <summary>Кодирование кадров: единый формат, общий для всех транспортов.</summary>
/// <remarks>
/// Кадр на проводе выглядит так: uint32 little-endian с длиной тела, затем тело,
/// то есть сериализованный protobuf <see cref="Envelope"/>. Длина нужна потому, что
/// pipe, сокет и кольцо в памяти — это потоки байт, и границ сообщений в них нет.
/// </remarks>
public static class FrameCodec
{
    /// <summary>Размер префикса длины в байтах.</summary>
    public const int HeaderSize = 4;

    /// <summary>
    /// Предел размера тела кадра, 256 МБ. Защищает от того, что испорченное поле длины
    /// превратится в попытку выделить гигабайты памяти.
    /// </summary>
    public const int MaxBodySize = 256 * 1024 * 1024;

    /// <summary>Разбирает тело кадра в конверт.</summary>
    /// <param name="body">Тело кадра без префикса длины.</param>
    /// <returns>Новый конверт. Он копирует данные, поэтому буфер можно сразу переиспользовать.</returns>
    /// <exception cref="InvalidProtocolBufferException">Тело не является корректным protobuf.</exception>
    public static Envelope Decode(ReadOnlySpan<byte> body) => Envelope.Parser.ParseFrom(body);

    /// <summary>Сериализует конверт в буфер из пула и отправляет его как один кадр.</summary>
    /// <remarks>
    /// Буфер берётся из <see cref="ArrayPool{T}"/>, поэтому на отправку не выделяется новый массив.
    /// Именно этот метод заменил старые <c>ToByteArray</c> и <c>ToArray</c>.
    /// </remarks>
    /// <param name="channel">Канал, в который отправляем.</param>
    /// <param name="envelope">Что отправляем.</param>
    /// <param name="token">Отмена ожидания, если канал занят.</param>
    /// <exception cref="InvalidOperationException">Сообщение больше <see cref="MaxBodySize"/>.</exception>
    public static async ValueTask SendEnvelopeAsync(this IFrameChannel channel, Envelope envelope,
        CancellationToken token = default)
    {
        var size = envelope.CalculateSize();
        if (size > MaxBodySize)
            throw new InvalidOperationException($"Message of {size} bytes exceeds the {MaxBodySize} byte limit.");

        var buffer = ArrayPool<byte>.Shared.Rent(HeaderSize + size);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, size);
            envelope.WriteTo(buffer.AsSpan(HeaderSize, size));
            await channel.SendAsync(buffer.AsMemory(0, HeaderSize + size), token);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Пишет одно сообщение прямо в поток. Нужно только для рукопожатия, пока канала ещё нет.</summary>
    /// <param name="stream">Поток соединения.</param>
    /// <param name="message">Сообщение для записи.</param>
    /// <param name="token">Отмена операции.</param>
    public static async Task WriteMessageAsync(Stream stream, IMessage message, CancellationToken token)
    {
        var size = message.CalculateSize();
        var buffer = new byte[HeaderSize + size];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, size);
        message.WriteTo(buffer.AsSpan(HeaderSize));
        await stream.WriteAsync(buffer, token);
        await stream.FlushAsync(token);
    }

    /// <summary>Читает одно сообщение прямо из потока. Нужно только для рукопожатия, пока канала ещё нет.</summary>
    /// <typeparam name="T">Тип ожидаемого protobuf-сообщения.</typeparam>
    /// <param name="stream">Поток соединения.</param>
    /// <param name="maxSize">Наибольший допустимый размер. Чужой клиент не должен заставить хаб выделить много памяти до проверки.</param>
    /// <param name="token">Отмена операции.</param>
    /// <returns>Разобранное сообщение.</returns>
    /// <exception cref="InvalidDataException">Размер кадра вне допустимых пределов.</exception>
    public static async Task<T> ReadMessageAsync<T>(Stream stream, int maxSize, CancellationToken token)
        where T : IMessage<T>, new()
    {
        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size < 0 || size > maxSize)
            throw new InvalidDataException($"Bad handshake frame size {size}.");

        var body = new byte[size];
        await stream.ReadExactlyAsync(body, token);
        return new MessageParser<T>(() => new T()).ParseFrom(body);
    }
}
