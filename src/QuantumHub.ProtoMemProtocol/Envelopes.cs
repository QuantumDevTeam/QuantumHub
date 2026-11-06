using Google.Protobuf;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Proto;

namespace QuantumHub.ProtoMemProtocol;

/// <summary>Фабрика конвертов и общие константы протокола.</summary>
public static class Envelopes
{
    /// <summary>Адрес самого хаба.</summary>
    public const string HubTarget = "hub";
    /// <summary>Метод рукопожатия.</summary>
    public const string HelloMethod = "hub.hello";
    /// <summary>Версия протокола. Меняется при несовместимых изменениях.</summary>
    public const uint ProtocolVersion = 1;

    /// <summary>Собирает запрос.</summary>
    /// <param name="source">Отправитель. По IPC хаб всё равно подставит настоящий.</param>
    /// <param name="target">Адресат.</param>
    /// <param name="method">Имя метода.</param>
    /// <param name="payload">Тело запроса.</param>
    public static Envelope Request(string source, string target, string method, HubPayload payload) => new()
    {
        Kind = EnvelopeKind.Request,
        Source = source,
        Target = target,
        Method = method,
        ContentType = payload.ContentType ?? string.Empty,
        Payload = Wrap(payload),
    };

    /// <summary>Собирает событие.</summary>
    /// <param name="source">Отправитель.</param>
    /// <param name="topic">Топик.</param>
    /// <param name="payload">Тело события.</param>
    public static Envelope Event(string source, string topic, HubPayload payload) => new()
    {
        Kind = EnvelopeKind.Event,
        Source = source,
        Method = topic,
        ContentType = payload.ContentType ?? string.Empty,
        Payload = Wrap(payload),
    };

    /// <summary>Собирает успешный ответ на запрос.</summary>
    /// <param name="request">Исходный запрос: из него берутся id и адресат.</param>
    /// <param name="payload">Тело ответа.</param>
    public static Envelope Response(Envelope request, HubPayload payload) => new()
    {
        Kind = EnvelopeKind.Response,
        Id = request.Id,
        Target = request.Source,
        ContentType = payload.ContentType ?? string.Empty,
        Payload = Wrap(payload),
    };

    /// <summary>Собирает ответ с ошибкой.</summary>
    /// <param name="request">Исходный запрос.</param>
    /// <param name="status">Код ошибки.</param>
    /// <param name="message">Описание для человека.</param>
    public static Envelope Error(Envelope request, StatusCode status, string message) => new()
    {
        Kind = EnvelopeKind.Response,
        Id = request.Id,
        Target = request.Source,
        Status = status,
        Error = message,
    };

    /// <summary>Достаёт тело без копирования.</summary>
    /// <param name="envelope">Конверт.</param>
    public static HubPayload GetPayload(this Envelope envelope) =>
        new(envelope.ContentType, envelope.Payload.Memory);

    /// <summary>Превращает ответ с ошибкой в исключение, которого ждёт вызывающий код.</summary>
    /// <param name="response">Ответ.</param>
    /// <returns>Тело успешного ответа.</returns>
    /// <exception cref="HubCallException">В ответе ошибка.</exception>
    public static HubPayload Unwrap(this Envelope response) => response.Status == StatusCode.Ok
        ? response.GetPayload()
        : throw new HubCallException(response.Status, response.Error);

    // Тело читается только до сериализации, поэтому копия не нужна.
    /// <summary>Оборачивает тело без копирования.</summary>
    private static ByteString Wrap(HubPayload payload) =>
        payload.Data.IsEmpty ? ByteString.Empty : UnsafeByteOperations.UnsafeWrap(payload.Data);
}
