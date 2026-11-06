using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using QuantumHub.Contracts.Ipc;

namespace QuantumHub.ProtoMemProtocol.Shm;

/// <summary>Канал кадров через разделяемую память: одна секция и по кольцу на каждое направление.</summary>
/// <remarks>
/// <para>Раскладка памяти описана в docs/PROTOCOL.md, она одинакова для всех языков.</para>
/// <para>
/// Разделяемая память не может сообщить, что другая сторона умерла. Поэтому канал держит «линию жизни»:
/// named pipe, на котором прошло рукопожатие. Когда процесс умирает любым способом, Windows закрывает его
/// хэндлы, pipe рвётся, и канал сразу об этом узнаёт.
/// </para>
/// <para>
/// Чтение идёт на выделенном потоке, а не в пуле: он большую часть времени спит или крутится на кольце,
/// и занимать им поток пула незачем.
/// </para>
/// </remarks>
public sealed class ShmFrameChannel : IFrameChannel
{
    /// <summary>Название транспорта в рукопожатии.</summary>
    public const string TransportName = "shm";
    /// <summary>Размер кольца по умолчанию, 1 МБ на направление. Память выделяется лениво, пока её не трогают.</summary>
    public const uint DefaultRingSize = 1024 * 1024;

    /// <summary>Метка в начале секции, чтобы не принять чужую память за свою.</summary>
    private const uint Magic = 0x4D485148; // "HQHM"
    /// <summary>Версия раскладки памяти.</summary>
    private const uint LayoutVersion = 1;
    /// <summary>Смещение начала данных.</summary>
    private const int HeaderSize = 4096;
    /// <summary>Смещение первого блока управления.</summary>
    private const int FirstControlOffset = 256;

    /// <summary>Бюджет активного ожидания. На одном ядре он бессмыслен: другая сторона не сможет работать, пока мы крутимся.</summary>
    private static readonly long DefaultSpinTicks = Environment.ProcessorCount > 1 ? Stopwatch.Frequency / 20_000 : 0; // 50 us

    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly EventWaitHandle[] _events;
    private readonly ShmRing _incoming;
    private readonly ShmRing _outgoing;
    private readonly Stream? _lifeline;
    /// <summary>Сигнал закрытия для спящего читателя.</summary>
    private readonly ManualResetEvent _closed = new(false);
    /// <summary>Кадры не должны перемешиваться, а кольцо рассчитано на одного писателя.</summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly long _spinTicks;
    /// <summary>Упорядочивает <see cref="Start"/> и <see cref="Dispose"/>, смотри комментарий в <see cref="Start"/>.</summary>
    private readonly object _startGate = new();
    private Thread? _reader;
    private Action<Exception?>? _onClosed;
    private int _closedOnce;
    private int _disposed;

    /// <summary>Отображает память и строит кольца. У сервера и клиента они зеркальны.</summary>
    /// <param name="mapping">Секция памяти.</param>
    /// <param name="events">Четыре события в порядке из <see cref="EventNames"/>.</param>
    /// <param name="ringSize">Размер одного кольца.</param>
    /// <param name="isServer">Сервер записывает заголовок, клиент его проверяет.</param>
    /// <param name="lifeline">Поток, обрыв которого значит смерть другой стороны.</param>
    /// <param name="spinTicks">Бюджет активного ожидания.</param>
    private unsafe ShmFrameChannel(MemoryMappedFile mapping, EventWaitHandle[] events, uint ringSize, bool isServer,
        Stream? lifeline, long spinTicks)
    {
        _mapping = mapping;
        _events = events;
        _lifeline = lifeline;
        _spinTicks = spinTicks;
        _view = mapping.CreateViewAccessor(0, HeaderSize + 2L * ringSize);

        byte* basePtr = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        basePtr += _view.PointerOffset;

        if (isServer)
        {
            ((uint*)basePtr)[0] = Magic;
            ((uint*)basePtr)[1] = LayoutVersion;
            ((uint*)basePtr)[2] = ringSize;
        }
        else if (((uint*)basePtr)[0] != Magic || ((uint*)basePtr)[1] != LayoutVersion || ((uint*)basePtr)[2] != ringSize)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            throw new InvalidDataException("Shared memory header does not match this protocol version.");
        }

        var toServer = new ShmRing(basePtr + FirstControlOffset, basePtr + HeaderSize, ringSize, events[0], events[1]);
        var toClient = new ShmRing(basePtr + FirstControlOffset + ShmRing.ControlSize, basePtr + HeaderSize + ringSize,
            ringSize, events[2], events[3]);
        _incoming = isServer ? toServer : toClient;
        _outgoing = isServer ? toClient : toServer;
    }

    /// <inheritdoc/>
    public string Transport => TransportName;

    /// <summary>Создаёт секцию и события. Вызывается на стороне хаба.</summary>
    /// <param name="name">Имя секции. События получают имена от него.</param>
    /// <param name="ringSize">Размер кольца, степень двойки.</param>
    /// <param name="lifeline">Необязательная линия жизни, канал становится её владельцем.</param>
    /// <param name="spinTicks">Бюджет активного ожидания, по умолчанию около 50 мкс.</param>
    /// <returns>Готовый канал. Читать он начнёт после <see cref="Start"/>.</returns>
    /// <exception cref="ArgumentException">Размер не является степенью двойки.</exception>
    public static ShmFrameChannel CreateServer(string name, uint ringSize, Stream? lifeline = null, long? spinTicks = null)
    {
        if (ringSize == 0 || (ringSize & (ringSize - 1)) != 0)
            throw new ArgumentException("Ring size must be a power of two.", nameof(ringSize));

        var events = EventNames(name)
            .Select(eventName => new EventWaitHandle(false, EventResetMode.AutoReset, eventName))
            .ToArray();
        var mapping = MemoryMappedFile.CreateNew(name, HeaderSize + 2L * ringSize);
        return new ShmFrameChannel(mapping, events, ringSize, true, lifeline, spinTicks ?? DefaultSpinTicks);
    }

    /// <summary>Подключается к существующей секции. Вызывается на стороне клиента.</summary>
    /// <param name="name">Имя секции из ответа на hello.</param>
    /// <param name="ringSize">Размер кольца из ответа на hello.</param>
    /// <param name="lifeline">Необязательная линия жизни, канал становится её владельцем.</param>
    /// <param name="spinTicks">Бюджет активного ожидания, по умолчанию около 50 мкс.</param>
    /// <returns>Готовый канал.</returns>
    /// <exception cref="InvalidDataException">Заголовок секции не совпал: другая версия протокола.</exception>
    public static ShmFrameChannel OpenClient(string name, uint ringSize, Stream? lifeline = null, long? spinTicks = null)
    {
        var events = EventNames(name).Select(EventWaitHandle.OpenExisting).ToArray();
        var mapping = MemoryMappedFile.OpenExisting(name);
        return new ShmFrameChannel(mapping, events, ringSize, false, lifeline, spinTicks ?? DefaultSpinTicks);
    }

    /// <summary>Имена событий: данные к серверу, место к серверу, данные к клиенту, место к клиенту.</summary>
    private static string[] EventNames(string name) => [$"{name}.c2s.data", $"{name}.c2s.space", $"{name}.s2c.data", $"{name}.s2c.space"];

    /// <inheritdoc/>
    public void Start(FrameHandler onFrame, Action<Exception?> onClosed)
    {
        // Dispose может успеть раньше, например если хаб завершается посреди рукопожатия.
        // Читатель тогда не должен стартовать: память, которую он стал бы читать, уже освобождена.
        lock (_startGate)
        {
            _onClosed = onClosed;
            if (Volatile.Read(ref _disposed) != 0)
            {
                ThreadPool.QueueUserWorkItem(_ => onClosed(null));
                return;
            }

            _reader = new Thread(() => ReadLoop(onFrame)) { IsBackground = true, Name = "QuantumHub shm reader" };
            _reader.Start();
        }

        if (_lifeline is not null) _ = WatchLifelineAsync(_lifeline);
    }

    /// <summary>Ждёт обрыва линии жизни. Данных по ней не ходит, поэтому чтение просто висит до конца.</summary>
    private async Task WatchLifelineAsync(Stream lifeline)
    {
        var buffer = new byte[1];
        try
        {
            while (await lifeline.ReadAsync(buffer) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Разорванный pipe значит то же, что и обычный конец потока.
        }

        SignalClosed(null);
    }

    /// <summary>Цикл чтения на выделенном потоке: заголовок, тело, разбор.</summary>
    /// <param name="onFrame">Получатель кадров.</param>
    private void ReadLoop(FrameHandler onFrame)
    {
        Exception? failure = null;
        Span<byte> header = stackalloc byte[FrameCodec.HeaderSize];
        var body = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (ReadExact(header))
            {
                var size = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (size < 0 || size > FrameCodec.MaxBodySize)
                    throw new InvalidDataException($"Bad frame size {size}.");

                if (size > body.Length)
                {
                    ArrayPool<byte>.Shared.Return(body);
                    body = ArrayPool<byte>.Shared.Rent(size);
                }

                if (!ReadExact(body.AsSpan(0, size))) break;
                onFrame(body.AsSpan(0, size));
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }

        SignalClosed(failure);
    }

    /// <summary>Читает ровно столько байт, сколько нужно. Кадр может лежать в кольце частями, если он больше кольца.</summary>
    /// <returns><see langword="false"/>, если канал закрылся раньше.</returns>
    private bool ReadExact(Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            var count = _incoming.TryRead(destination);
            if (count > 0)
            {
                destination = destination[count..];
                continue;
            }

            if (!_incoming.WaitForData(_closed, _spinTicks)) return false;
        }

        return true;
    }

    /// <summary>Отмечает закрытие ровно один раз и сообщает владельцу.</summary>
    private void SignalClosed(Exception? failure)
    {
        if (Interlocked.Exchange(ref _closedOnce, 1) != 0) return;
        try { _closed.Set(); } catch (ObjectDisposedException) { }

        // Не на этом потоке: колбэк обычно вызывает Dispose, а тот ждёт завершения читателя.
        var callback = _onClosed;
        if (callback is not null) ThreadPool.QueueUserWorkItem(_ => callback(failure));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Обычно завершается синхронно и без выделения памяти: кольцо не заполнено, и <see cref="ValueTask"/>
    /// не создаёт <see cref="Task"/>. Асинхронный путь нужен, только если кольцо заполнено или канал занят.
    /// </remarks>
    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken token = default)
    {
        if (Volatile.Read(ref _closedOnce) != 0) throw new ObjectDisposedException(nameof(ShmFrameChannel));

        if (_sendLock.Wait(0))
        {
            int written;
            try
            {
                // Проверяем ещё раз под блокировкой: Dispose берёт её перед тем, как убрать память.
                if (Volatile.Read(ref _closedOnce) != 0) throw new ObjectDisposedException(nameof(ShmFrameChannel));
                written = _outgoing.TryWrite(frame.Span);
            }
            catch
            {
                _sendLock.Release();
                throw;
            }

            if (written == frame.Length)
            {
                _sendLock.Release();
                return default;
            }

            return new ValueTask(FinishSendAsync(frame[written..], token));
        }

        return new ValueTask(SendContendedAsync(frame, token));
    }

    /// <summary>Медленный путь, когда блокировкой уже владеет другой отправитель.</summary>
    private async Task SendContendedAsync(ReadOnlyMemory<byte> frame, CancellationToken token)
    {
        await _sendLock.WaitAsync(token);
        await FinishSendAsync(frame, token);
    }

    /// <summary>
    /// Дописывает остаток кадра, когда кольцо заполнено. Держит блокировку отправки до конца:
    /// кадры нельзя перемешивать.
    /// </summary>
    /// <param name="rest">Что осталось отправить.</param>
    /// <param name="token">Не используется для прерывания: оборванный на середине кадр сломал бы поток.</param>
    private async Task FinishSendAsync(ReadOnlyMemory<byte> rest, CancellationToken token)
    {
        try
        {
            while (!rest.IsEmpty)
            {
                if (Volatile.Read(ref _closedOnce) != 0) throw new ObjectDisposedException(nameof(ShmFrameChannel));

                var written = _outgoing.TryWrite(rest.Span);
                rest = rest[written..];
                if (written == 0 || !rest.IsEmpty)
                    await _outgoing.WaitForSpaceAsync(() => Volatile.Read(ref _closedOnce) != 0, CancellationToken.None);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Закрывает канал и освобождает память, когда её больше никто не трогает.</summary>
    public void Dispose()
    {
        Thread? reader;
        lock (_startGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            reader = _reader;
        }

        SignalClosed(null);
        _lifeline?.Dispose();

        // Снять отображение, пока кто-то читает кольца, значит получить access violation, а не исключение.
        // Если что-то отказывается завершаться, память намеренно не освобождается:
        // это стоит немного памяти до конца процесса, зато процесс не падает.
        var readerDone = reader is null || (reader != Thread.CurrentThread && reader.Join(TimeSpan.FromSeconds(2)));
        if (!readerDone || !_sendLock.Wait(TimeSpan.FromSeconds(2))) return;

        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mapping.Dispose();
        foreach (var handle in _events) handle.Dispose();
        _closed.Dispose();

        // Отправители в очереди на блокировку проснутся, увидят закрытый канал и бросят исключение.
        _sendLock.Release();
    }
}
