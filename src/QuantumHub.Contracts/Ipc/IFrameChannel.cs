namespace QuantumHub.Contracts.Ipc;

/// <summary>Получает тело одного кадра.</summary>
/// <param name="body">Тело. Действительно только на время вызова: буфер будет использован снова.</param>
public delegate void FrameHandler(ReadOnlySpan<byte> body);

/// <summary>
/// Подключённый, упорядоченный и надёжный канал кадров. Чтобы добавить новый транспорт, достаточно
/// реализовать этот интерфейс.
/// </summary>
/// <remarks>Кадр на проводе: длина тела как uint32 little-endian и само тело.</remarks>
public interface IFrameChannel : IDisposable
{
    /// <summary>Название транспорта: "shm", "pipe" или "tcp".</summary>
    string Transport { get; }

    /// <summary>Начинает доставку кадров.</summary>
    /// <param name="onFrame">Получатель кадров.</param>
    /// <param name="onClosed">Вызывается один раз, когда другой стороны больше нет. Аргумент — причина, если это сбой.</param>
    void Start(FrameHandler onFrame, Action<Exception?> onClosed);

    /// <summary>Отправляет кадр. Можно вызывать из разных потоков.</summary>
    /// <param name="frame">Кадр вместе с префиксом длины.</param>
    /// <param name="token">Отмена ожидания очереди на отправку.</param>
    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken token = default);
}
