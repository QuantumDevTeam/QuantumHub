using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace QuantumHub.Plugin.Discord;

/// <summary>Тип кадра в протоколе Discord IPC.</summary>
public enum DiscordOpcode
{
    /// <summary>Рукопожатие.</summary>
    Handshake = 0,

    /// <summary>Команда, ответ или событие.</summary>
    Frame = 1,

    /// <summary>Закрытие соединения.</summary>
    Close = 2,

    /// <summary>Проверка связи от Discord.</summary>
    Ping = 3,

    /// <summary>Ответ на проверку связи.</summary>
    Pong = 4,
}

/// <summary>Кадры Discord IPC: тип и длина как int32 little-endian, затем JSON.</summary>
public static class DiscordFrame
{
    private const int HeaderSize = 8;

    /// <summary>Защита от испорченного поля длины.</summary>
    private const int MaxSize = 16 * 1024 * 1024;

    /// <summary>Записывает кадр.</summary>
    /// <param name="stream">Поток соединения.</param>
    /// <param name="opcode">Тип кадра.</param>
    /// <param name="payload">Тело.</param>
    /// <param name="token">Отмена.</param>
    public static async Task WriteAsync(Stream stream, DiscordOpcode opcode, JsonNode payload, CancellationToken token)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, (int)opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, HeaderSize);
        await stream.WriteAsync(frame, token);
        await stream.FlushAsync(token);
    }

    /// <summary>Читает кадр.</summary>
    /// <param name="stream">Поток соединения.</param>
    /// <param name="token">Отмена.</param>
    /// <returns>Тип и тело.</returns>
    /// <exception cref="EndOfStreamException">Соединение закрыто.</exception>
    /// <exception cref="InvalidDataException">Кадр испорчен.</exception>
    public static async Task<(DiscordOpcode Opcode, JsonNode? Payload)> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, token);

        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > MaxSize) throw new InvalidDataException($"Плохая длина кадра: {length}.");

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        return ((DiscordOpcode)BinaryPrimitives.ReadInt32LittleEndian(header), JsonNode.Parse(body));
    }
}

/// <summary>Подключение к приложению Discord. Отделено, чтобы тесты не трогали настоящий Discord.</summary>
public interface IDiscordConnector
{
    /// <summary>Подключается к Discord.</summary>
    /// <param name="token">Отмена.</param>
    /// <returns>Поток соединения или <see langword="null"/>, если Discord не запущен.</returns>
    Task<Stream?> ConnectAsync(CancellationToken token);
}

/// <summary>Настоящее подключение: Discord слушает пайпы от discord-ipc-0 до discord-ipc-9.</summary>
public sealed class NamedPipeDiscordConnector : IDiscordConnector
{
    private const int PipeCount = 10;
    private const int ConnectTimeoutMs = 500;

    /// <inheritdoc/>
    public async Task<Stream?> ConnectAsync(CancellationToken token)
    {
        for (var i = 0; i < PipeCount; i++)
        {
            var name = $"discord-ipc-{i}";
            if (!File.Exists($@"\\.\pipe\{name}")) continue;

            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(ConnectTimeoutMs, token);
                return pipe;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                await pipe.DisposeAsync();
            }
        }

        return null;
    }
}
