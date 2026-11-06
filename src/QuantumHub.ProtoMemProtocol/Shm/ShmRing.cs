using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace QuantumHub.ProtoMemProtocol.Shm;

/// <summary>Кольцевой буфер байт в разделяемой памяти: один писатель и один читатель (SPSC).</summary>
/// <remarks>
/// <para>
/// Позиции <c>head</c> и <c>tail</c> — монотонно растущие 64-битные счётчики, а индекс в буфере
/// равен <c>позиция &amp; (размер - 1)</c>. Поэтому «полно» и «пусто» различаются без запасной ячейки:
/// занято ровно <c>tail - head</c> байт.
/// </para>
/// <para>
/// На быстром пути нет ни одного системного вызова. Каждая сторона публикует позицию через
/// <see cref="Volatile.Write(ref long, long)"/> и обращается к ядру Windows только тогда, когда другая
/// сторона объявила, что спит.
/// </para>
/// <para>
/// Главная опасность такого подхода — потерянное пробуждение: читатель проверил, что данных нет,
/// писатель опубликовал данные и увидел, что читатель не спит, и только потом читатель уснул навсегда.
/// Это закрывается полным барьером памяти с обеих сторон. Спящий поднимает флаг и потом перепроверяет
/// позицию, а писатель двигает позицию и потом проверяет флаг. Хотя бы один из них обязательно увидит
/// действие другого. Без барьера процессор имеет право переставить эти операции местами.
/// </para>
/// </remarks>
internal sealed class ShmRing
{
    // Каждый счётчик лежит в своей кеш-линии. Их пишут разные ядра, и в одной линии
    // они перебрасывали бы её друг другу на каждом сообщении (false sharing).
    /// <summary>Размер блока управления кольцом в разделяемой памяти.</summary>
    internal const int ControlSize = 256;
    private const int HeadOffset = 0;
    private const int TailOffset = 64;
    private const int ConsumerSleepingOffset = 128;
    private const int ProducerSleepingOffset = 192;

    /// <summary>Позиция читателя. Меняет только читатель.</summary>
    private readonly unsafe long* _head;
    /// <summary>Позиция писателя. Меняет только писатель.</summary>
    private readonly unsafe long* _tail;
    /// <summary>Не ноль, если читатель спит на <see cref="DataReady"/>.</summary>
    private readonly unsafe int* _consumerSleeping;
    /// <summary>Не ноль, если писатель спит на <see cref="SpaceReady"/>.</summary>
    private readonly unsafe int* _producerSleeping;
    /// <summary>Начало области данных кольца.</summary>
    private readonly unsafe byte* _data;
    private readonly long _capacity;
    /// <summary>Размер минус один. Размер — степень двойки, поэтому <c>&amp;</c> заменяет дорогое деление по модулю.</summary>
    private readonly long _mask;
    private readonly EventWaitHandle _dataReady;
    private readonly EventWaitHandle _spaceReady;

    /// <summary>Привязывает кольцо к уже отображённой памяти.</summary>
    /// <param name="control">Блок управления кольцом.</param>
    /// <param name="data">Область данных кольца.</param>
    /// <param name="capacity">Размер области данных, степень двойки.</param>
    /// <param name="dataReady">Событие «появились данные».</param>
    /// <param name="spaceReady">Событие «появилось место».</param>
    public unsafe ShmRing(byte* control, byte* data, long capacity, EventWaitHandle dataReady, EventWaitHandle spaceReady)
    {
        _head = (long*)(control + HeadOffset);
        _tail = (long*)(control + TailOffset);
        _consumerSleeping = (int*)(control + ConsumerSleepingOffset);
        _producerSleeping = (int*)(control + ProducerSleepingOffset);
        _data = data;
        _capacity = capacity;
        _mask = capacity - 1;
        _dataReady = dataReady;
        _spaceReady = spaceReady;
    }

    /// <summary>Событие, которым писатель будит спящего читателя.</summary>
    public EventWaitHandle DataReady => _dataReady;
    /// <summary>Событие, которым читатель будит спящего писателя.</summary>
    public EventWaitHandle SpaceReady => _spaceReady;

    /// <summary>Копирует в кольцо столько, сколько влезает, и публикует результат. Только для писателя.</summary>
    /// <param name="source">Что записать.</param>
    /// <returns>Сколько байт записано. 0 значит, что кольцо заполнено.</returns>
    /// <exception cref="InvalidDataException">Позиции бессмысленны: память испорчена другой стороной.</exception>
    public unsafe int TryWrite(ReadOnlySpan<byte> source)
    {
        var tail = *_tail;
        var used = tail - Volatile.Read(ref *_head);
        if ((ulong)used > (ulong)_capacity) throw new InvalidDataException("Shared memory ring is corrupt.");

        var count = (int)Math.Min(_capacity - used, source.Length);
        if (count == 0) return 0;

        var offset = (int)(tail & _mask);
        var first = (int)Math.Min(count, _capacity - offset);
        source[..first].CopyTo(new Span<byte>(_data + offset, first));
        if (first < count) source[first..count].CopyTo(new Span<byte>(_data, count - first));

        Volatile.Write(ref *_tail, tail + count);
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref *_consumerSleeping) != 0) _dataReady.Set();
        return count;
    }

    /// <summary>Забирает из кольца столько, сколько есть, и освобождает место. Только для читателя.</summary>
    /// <param name="destination">Куда скопировать.</param>
    /// <returns>Сколько байт прочитано. 0 значит, что кольцо пусто.</returns>
    /// <exception cref="InvalidDataException">Позиции бессмысленны: память испорчена другой стороной.</exception>
    public unsafe int TryRead(Span<byte> destination)
    {
        var head = *_head;
        var available = Volatile.Read(ref *_tail) - head;
        if ((ulong)available > (ulong)_capacity) throw new InvalidDataException("Shared memory ring is corrupt.");

        var count = (int)Math.Min(available, destination.Length);
        if (count == 0) return 0;

        var offset = (int)(head & _mask);
        var first = (int)Math.Min(count, _capacity - offset);
        new ReadOnlySpan<byte>(_data + offset, first).CopyTo(destination);
        if (first < count) new ReadOnlySpan<byte>(_data, count - first).CopyTo(destination[first..]);

        Volatile.Write(ref *_head, head + count);
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref *_producerSleeping) != 0) _spaceReady.Set();
        return count;
    }

    /// <summary>Есть ли что читать.</summary>
    private unsafe bool HasData
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref *_tail) != *_head;
    }

    /// <summary>Есть ли куда писать.</summary>
    private unsafe bool HasSpace => *_tail - Volatile.Read(ref *_head) < _capacity;

    /// <summary>Ждёт данные: сначала крутится на месте, потом засыпает на событии.</summary>
    /// <remarks>
    /// В сценарии запрос-ответ ответ приходит за микросекунды, и короткое ожидание в цикле ловит его без
    /// обращения к ядру. Если данных нет дольше бюджета, поток засыпает и перестаёт тратить процессор.
    /// </remarks>
    /// <param name="closed">Хэндл, сигнал которого означает закрытие канала.</param>
    /// <param name="spinTicks">Бюджет активного ожидания в тиках <see cref="Stopwatch"/>. 0 отключает его.</param>
    /// <returns><see langword="false"/>, если канал закрыт.</returns>
    public unsafe bool WaitForData(WaitHandle closed, long spinTicks)
    {
        if (spinTicks > 0)
        {
            var deadline = Stopwatch.GetTimestamp() + spinTicks;
            do
            {
                if (HasData) return true;
                Thread.SpinWait(30);
            } while (Stopwatch.GetTimestamp() < deadline);
        }

        var handles = new[] { _dataReady, closed };
        while (true)
        {
            Volatile.Write(ref *_consumerSleeping, 1);
            Interlocked.MemoryBarrier();
            if (HasData)
            {
                Volatile.Write(ref *_consumerSleeping, 0);
                return true;
            }

            var signaled = WaitHandle.WaitAny(handles);
            Volatile.Write(ref *_consumerSleeping, 0);
            if (signaled == 1) return false;
            if (HasData) return true;
        }
    }

    /// <summary>Ждёт свободное место, не занимая поток.</summary>
    /// <remarks>Это асинхронная пара к <see cref="WaitForData"/>. Благодаря ей зависший клиент с полным кольцом не блокирует потоки хаба.</remarks>
    /// <param name="isClosed">Проверка, что канал закрыт.</param>
    /// <param name="token">Отмена ожидания.</param>
    public async Task WaitForSpaceAsync(Func<bool> isClosed, CancellationToken token)
    {
        while (!isClosed())
        {
            token.ThrowIfCancellationRequested();
            RaiseProducerSleeping();
            if (HasSpace) break;

            var woken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Ожидание с таймаутом: так этот же колбэк заодно замечает закрытие канала.
            var registration = ThreadPool.RegisterWaitForSingleObject(_spaceReady,
                static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), woken, 50, true);
            await woken.Task;
            registration.Unregister(null);
            if (HasSpace) break;
        }

        ClearProducerSleeping();
    }

    /// <summary>Снимает флаг спящего писателя.</summary>
    private unsafe void ClearProducerSleeping() => Volatile.Write(ref *_producerSleeping, 0);

    /// <summary>Объявляет писателя спящим. Барьер обязателен, смотри описание класса.</summary>
    private unsafe void RaiseProducerSleeping()
    {
        Volatile.Write(ref *_producerSleeping, 1);
        Interlocked.MemoryBarrier();
    }
}
