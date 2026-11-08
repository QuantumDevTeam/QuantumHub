using QuantumHub.Contracts.Ui;
using System.Buffers.Binary;
using System.Text;

namespace QuantumHub.Plugin.VrChatOsc;

/// <summary>
/// Сообщения OSC 1.0. Формат простой, поэтому внешняя библиотека здесь не нужна: адрес, строка типов
/// и аргументы, всё выровнено по четыре байта.
/// </summary>
public static class OscMessage
{
    /// <summary>Собирает сообщение.</summary>
    /// <param name="address">Адрес, например "/chatbox/input".</param>
    /// <param name="arguments">Аргументы: string, bool, int или float.</param>
    /// <returns>Байты пакета.</returns>
    /// <exception cref="ArgumentException">Тип аргумента не поддерживается.</exception>
    public static byte[] Encode(string address, params object[] arguments)
    {
        using var stream = new MemoryStream();
        WriteString(stream, address);

        var tags = new StringBuilder(",");
        foreach (var argument in arguments)
        {
            tags.Append(argument switch
            {
                string => 's',
                true => 'T',
                false => 'F',
                int => 'i',
                float => 'f',
                _ => throw new ArgumentException(Loc.T($"OSC не поддерживает {argument.GetType().Name}.", $"OSC does not support {argument.GetType().Name}."), nameof(arguments)),
            });
        }

        WriteString(stream, tags.ToString());

        foreach (var argument in arguments)
        {
            switch (argument)
            {
                case string text:
                    WriteString(stream, text);
                    break;
                case int number:
                    WriteBigEndian(stream, BitConverter.GetBytes(number));
                    break;
                case float number:
                    WriteBigEndian(stream, BitConverter.GetBytes(number));
                    break;
            }
        }

        return stream.ToArray();
    }

    /// <summary>Разбирает одно сообщение. Пакеты-связки (bundle) VRChat не присылает.</summary>
    /// <param name="packet">Байты пакета.</param>
    /// <returns>Адрес и аргументы, либо <see langword="null"/>, если это не сообщение OSC.</returns>
    public static (string Address, object[] Arguments)? Decode(ReadOnlySpan<byte> packet)
    {
        var position = 0;
        if (ReadString(packet, ref position) is not { Length: > 0 } address || address[0] != '/') return null;
        if (ReadString(packet, ref position) is not { Length: > 0 } tags || tags[0] != ',') return null;

        var arguments = new List<object>();
        foreach (var tag in tags.AsSpan(1))
        {
            switch (tag)
            {
                case 's':
                    if (ReadString(packet, ref position) is not { } text) return null;
                    arguments.Add(text);
                    break;
                case 'i':
                    if (position + 4 > packet.Length) return null;
                    arguments.Add(BinaryPrimitives.ReadInt32BigEndian(packet[position..]));
                    position += 4;
                    break;
                case 'f':
                    if (position + 4 > packet.Length) return null;
                    arguments.Add(BinaryPrimitives.ReadSingleBigEndian(packet[position..]));
                    position += 4;
                    break;
                case 'T':
                    arguments.Add(true);
                    break;
                case 'F':
                    arguments.Add(false);
                    break;
                default:
                    return null;
            }
        }

        return (address, arguments.ToArray());
    }

    /// <summary>Строка в UTF-8, ноль на конце и выравнивание: хотя бы один нулевой байт есть всегда.</summary>
    private static void WriteString(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes);
        stream.Write(new byte[4 - bytes.Length % 4]);
    }

    private static string? ReadString(ReadOnlySpan<byte> packet, ref int position)
    {
        if (position >= packet.Length) return null;

        var length = packet[position..].IndexOf((byte)0);
        if (length < 0) return null;

        var text = Encoding.UTF8.GetString(packet.Slice(position, length));
        position += (length / 4 + 1) * 4;
        return text;
    }

    private static void WriteBigEndian(Stream stream, byte[] bytes)
    {
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        stream.Write(bytes);
    }
}
