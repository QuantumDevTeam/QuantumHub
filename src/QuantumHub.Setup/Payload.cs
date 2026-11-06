using System.Buffers.Binary;
using System.Text;

namespace QuantumHub.Setup;

/// <summary>
/// Архив с программой, дописанный в конец Setup.exe: сам архив, его длина и метка. Файл без такого хвоста
/// работает как деинсталлятор, поэтому один и тот же exe служит и для установки, и для удаления.
/// </summary>
/// <param name="stream">Архив внутри файла.</param>
/// <param name="offset">Где архив начинается, то есть размер exe без него.</param>
internal sealed class Payload(Stream stream, long offset) : IDisposable
{
    /// <summary>Метка в самом конце файла.</summary>
    private static readonly byte[] Magic = "QHSETUP1"u8.ToArray();
    /// <summary>Размер хвоста: длина и метка.</summary>
    private const int TailSize = sizeof(long) + 8;

    /// <summary>Архив.</summary>
    public Stream Stream { get; } = stream;

    /// <summary>Размер exe без архива.</summary>
    public long Offset { get; } = offset;

    /// <summary>Открывает архив в файле.</summary>
    /// <param name="path">Путь к exe.</param>
    /// <returns>Архив или <see langword="null"/>, если его в файле нет.</returns>
    public static Payload? Open(string path)
    {
        var file = File.OpenRead(path);
        Span<byte> tail = stackalloc byte[TailSize];
        if (file.Length < TailSize)
        {
            file.Dispose();
            return null;
        }

        file.Seek(-TailSize, SeekOrigin.End);
        file.ReadExactly(tail);
        var length = BinaryPrimitives.ReadInt64LittleEndian(tail);
        var start = file.Length - TailSize - length;
        if (!tail[sizeof(long)..].SequenceEqual(Magic) || length <= 0 || start < 0)
        {
            file.Dispose();
            return null;
        }

        return new Payload(new SliceStream(file, start, length), start);
    }

    /// <summary>Дописывает архив к exe. Этим пользуются сборка релиза и тесты.</summary>
    /// <param name="exe">Файл, к которому дописать.</param>
    /// <param name="archive">Архив.</param>
    public static void Append(string exe, string archive)
    {
        using var output = new FileStream(exe, FileMode.Append, FileAccess.Write);
        using var input = File.OpenRead(archive);
        input.CopyTo(output);

        Span<byte> tail = stackalloc byte[TailSize];
        BinaryPrimitives.WriteInt64LittleEndian(tail, input.Length);
        Magic.CopyTo(tail[sizeof(long)..]);
        output.Write(tail);
    }

    /// <summary>Копирует exe без архива: получается деинсталлятор.</summary>
    /// <param name="exe">Исходный exe.</param>
    /// <param name="destination">Куда положить.</param>
    public void CopyProgramTo(string exe, string destination)
    {
        using var input = File.OpenRead(exe);
        using var output = File.Create(destination);
        var buffer = new byte[1 << 20];
        for (var left = Offset; left > 0;)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read == 0) throw new EndOfStreamException();
            output.Write(buffer, 0, read);
            left -= read;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Stream.Dispose();

    /// <summary>Участок файла как отдельный поток: ZipArchive ищет оглавление с конца и иначе увидел бы весь exe.</summary>
    private sealed class SliceStream(Stream inner, long start, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var left = length - _position;
            if (left <= 0) return 0;
            inner.Position = start + _position;
            var read = inner.Read(buffer, offset, (int)Math.Min(count, left));
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
