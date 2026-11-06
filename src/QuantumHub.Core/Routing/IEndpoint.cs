using QuantumHub.Contracts.Proto;

namespace QuantumHub.Core.Routing;

/// <summary>Всё, чему можно адресовать сообщение: сам хаб, загруженный плагин, сессия клиента.</summary>
public interface IEndpoint
{
    /// <summary>Адрес конечной точки.</summary>
    string Id { get; }

    /// <summary>Выполняет запрос.</summary>
    /// <param name="request">Запрос.</param>
    /// <param name="token">Отмена.</param>
    /// <returns>Ответ, в том числе с ошибкой.</returns>
    Task<Envelope> RequestAsync(Envelope request, CancellationToken token);

    /// <summary>Доставляет событие. Не должен блокировать.</summary>
    /// <param name="hubEvent">Событие.</param>
    void DeliverEvent(Envelope hubEvent);
}
