using Google.Protobuf;
using QuantumHub.Contracts.Proto;

namespace QuantumHub.Contracts.Bus;

/// <summary>Входящий запрос, как его видит обработчик.</summary>
/// <param name="Source">Кто спрашивает. Заполнено хабом, подделать нельзя.</param>
/// <param name="Method">Имя метода.</param>
/// <param name="Payload">Тело запроса.</param>
public sealed record HubRequest(string Source, string Method, HubPayload Payload);

/// <summary>Событие, как его видит подписчик.</summary>
/// <param name="Source">Кто опубликовал.</param>
/// <param name="Topic">Топик.</param>
/// <param name="Payload">Тело события.</param>
public sealed record HubEvent(string Source, string Topic, HubPayload Payload);

/// <summary>Обработчик метода плагина.</summary>
/// <param name="request">Запрос.</param>
/// <param name="token">Отменяется, когда вызывающая сторона ушла.</param>
/// <returns>Тело ответа.</returns>
public delegate ValueTask<HubPayload> HubRequestHandler(HubRequest request, CancellationToken token);

/// <summary>Вызов не удался: цель вернула ошибку или недоступна.</summary>
/// <param name="status">Код ошибки.</param>
/// <param name="message">Описание для человека.</param>
public sealed class HubCallException(StatusCode status, string message) : Exception(message)
{
    /// <summary>Код ошибки.</summary>
    public StatusCode Status { get; } = status;
}

/// <summary>
/// То, что плагин видит от хаба. Плагин внутри хаба и внешний клиент получают один и тот же интерфейс,
/// поэтому вынос плагина наружу почти не затрагивает его код.
/// </summary>
public interface IHubBus
{
    /// <summary>Вызывает метод другого плагина или самого хаба и ждёт ответ.</summary>
    /// <param name="target">Id плагина, "hub" или "cap:имя-возможности".</param>
    /// <param name="method">Имя метода.</param>
    /// <param name="payload">Тело запроса.</param>
    /// <param name="timeout">Сколько ждать. По умолчанию 30 секунд.</param>
    /// <param name="token">Отмена ожидания.</param>
    /// <returns>Тело ответа.</returns>
    /// <exception cref="HubCallException">Цель вернула ошибку или недоступна.</exception>
    ValueTask<HubPayload> CallAsync(string target, string method, HubPayload payload = default,
        TimeSpan? timeout = null, CancellationToken token = default);

    /// <summary>Публикует событие для всех подписчиков. Ответа не ждёт.</summary>
    /// <param name="topic">Топик.</param>
    /// <param name="payload">Тело события.</param>
    /// <param name="token">Отмена отправки.</param>
    ValueTask PublishAsync(string topic, HubPayload payload = default, CancellationToken token = default);

    /// <summary>Подписывается на события.</summary>
    /// <param name="topic">Точный топик или префикс, оканчивающийся на ".*".</param>
    /// <param name="handler">Обработчик. События приходят по порядку и не на потоке соединения.</param>
    /// <param name="token">Отмена подписки.</param>
    /// <returns>Объект, <see cref="IDisposable.Dispose"/> которого снимает подписку.</returns>
    ValueTask<IDisposable> SubscribeAsync(string topic, Action<HubEvent> handler, CancellationToken token = default);

    /// <summary>Объявляет метод, который могут вызывать другие плагины и клиенты.</summary>
    /// <param name="method">Имя метода.</param>
    /// <param name="handler">Обработчик.</param>
    /// <returns>Объект, <see cref="IDisposable.Dispose"/> которого убирает метод.</returns>
    IDisposable Handle(string method, HubRequestHandler handler);

    /// <summary>Разрешает зависимости прямо сейчас и при необходимости запускает поставщиков.</summary>
    /// <param name="dependencies">Что нужно найти.</param>
    /// <param name="token">Отмена.</param>
    /// <returns>Результат для каждой зависимости, в том же порядке.</returns>
    ValueTask<IReadOnlyList<DependencyResolution>> ResolveAsync(IEnumerable<Dependency> dependencies,
        CancellationToken token = default);
}

/// <summary>Удобные обёртки над <see cref="IHubBus"/>.</summary>
public static class HubBusExtensions
{
    /// <summary>Вызов с запросом и ответом в protobuf.</summary>
    /// <typeparam name="TResponse">Тип ответа.</typeparam>
    /// <param name="bus">Шина.</param>
    /// <param name="target">Адресат.</param>
    /// <param name="method">Имя метода.</param>
    /// <param name="request">Запрос.</param>
    /// <param name="token">Отмена.</param>
    /// <returns>Разобранный ответ.</returns>
    public static async ValueTask<TResponse> CallAsync<TResponse>(this IHubBus bus, string target, string method,
        IMessage request, CancellationToken token = default)
        where TResponse : IMessage<TResponse>, new()
    {
        var response = await bus.CallAsync(target, method, HubPayload.Proto(request), null, token);
        return response.AsProto<TResponse>();
    }

    /// <summary>Разрешает одну зависимость.</summary>
    /// <param name="bus">Шина.</param>
    /// <param name="dependency">Зависимость.</param>
    /// <param name="token">Отмена.</param>
    /// <returns>Результат.</returns>
    public static async ValueTask<DependencyResolution> ResolveAsync(this IHubBus bus, Dependency dependency,
        CancellationToken token = default) => (await bus.ResolveAsync([dependency], token))[0];
}
