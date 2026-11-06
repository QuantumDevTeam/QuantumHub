using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Google.Protobuf;

namespace QuantumHub.Contracts.Bus;

/// <summary>
/// Тело сообщения. Хаб передаёт его, не читая, поэтому формат — частная договорённость двух сторон:
/// protobuf, JSON, текст или сырые байты.
/// </summary>
/// <param name="ContentType">Формат тела. Хаб его не проверяет.</param>
/// <param name="Data">Сами данные.</param>
public readonly record struct HubPayload(string ContentType, ReadOnlyMemory<byte> Data)
{
    /// <summary>Тип для protobuf.</summary>
    public const string ProtobufType = "application/x-protobuf";
    /// <summary>Тип для JSON.</summary>
    public const string JsonType = "application/json";
    /// <summary>Тип для текста в UTF-8.</summary>
    public const string TextType = "text/plain";
    /// <summary>Тип для сырых байт.</summary>
    public const string BinaryType = "application/octet-stream";

    private static volatile JsonSerializerOptions _jsonOptions = CreateJsonOptions();

    /// <summary>Настройки JSON для всех сообщений: camelCase, как принято в вебе.</summary>
    public static JsonSerializerOptions JsonOptions => _jsonOptions;

    /// <summary>Сбрасывает кеши JSON. Хаб вызывает это при выгрузке плагина.</summary>
    /// <remarks>
    /// Настройки кешируют метаданные по типам, в том числе по типам плагина, и этот кеш навсегда
    /// удержал бы сборку выгруженного плагина в памяти, а её файлы — заблокированными.
    /// </remarks>
    public static void ResetJsonCache()
    {
        _jsonOptions = CreateJsonOptions();

        // Есть ещё один статический кеш, доступа к которому нет в публичном API. Его чистит хук hot reload.
        // Если будущая версия .NET уберёт этот метод, файлы плагина просто останутся
        // заблокированными до перезапуска хаба.
        typeof(DefaultJsonTypeInfoResolver)
            .GetMethod("ClearMemberAccessorCaches", BindingFlags.NonPublic | BindingFlags.Static)
            ?.Invoke(null, null);
    }

    // System.Text.Json делит кеш между одинаковыми настройками. Только собственный
    // экземпляр resolver делает эти настройки и их кеш по-настоящему отдельными.
    /// <summary>Создаёт настройки с отдельным кешем.</summary>
    private static JsonSerializerOptions CreateJsonOptions() => new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>Пустое тело.</summary>
    public static HubPayload Empty => new(string.Empty, ReadOnlyMemory<byte>.Empty);

    /// <summary>Пусто ли тело.</summary>
    public bool IsEmpty => Data.IsEmpty;

    /// <summary>Упаковывает protobuf-сообщение.</summary>
    /// <param name="message">Сообщение.</param>
    public static HubPayload Proto(IMessage message) => new(ProtobufType, message.ToByteArray());

    /// <summary>Упаковывает значение в JSON.</summary>
    /// <typeparam name="T">Тип значения.</typeparam>
    /// <param name="value">Значение.</param>
    public static HubPayload Json<T>(T value) =>
        new(JsonType, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    /// <summary>Упаковывает текст.</summary>
    /// <param name="value">Текст.</param>
    public static HubPayload Text(string value) => new(TextType, Encoding.UTF8.GetBytes(value));

    /// <summary>Упаковывает сырые байты.</summary>
    /// <param name="value">Байты.</param>
    public static HubPayload Bytes(ReadOnlyMemory<byte> value) => new(BinaryType, value);

    /// <summary>Читает тело как protobuf-сообщение.</summary>
    /// <typeparam name="T">Тип сообщения.</typeparam>
    public T AsProto<T>() where T : IMessage<T>, new() => new MessageParser<T>(() => new T()).ParseFrom(Data.Span);

    /// <summary>Читает тело как JSON.</summary>
    /// <typeparam name="T">Тип значения.</typeparam>
    public T? AsJson<T>() => JsonSerializer.Deserialize<T>(Data.Span, JsonOptions);

    /// <summary>Читает тело как текст.</summary>
    public string AsText() => Encoding.UTF8.GetString(Data.Span);
}
