using QuantumHub.Contracts.Proto;
using QuantumHub.ProtoMemProtocol;

namespace QuantumHub.Client;

/// <summary>Настройки подключения внешнего плагина к хабу.</summary>
public sealed class HubClientOptions
{
    /// <summary>Постоянный идентификатор, например "acme.linecounter.rider". Разным частям плагина нужны разные id.</summary>
    public required string ClientId { get; init; }

    /// <summary>Имя для показа в GUI. По умолчанию равно <see cref="ClientId"/>.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Версия клиента.</summary>
    public string Version { get; init; } = "0.0.0";

    /// <summary>"plugin" для плагина, "gui" для админки.</summary>
    public string Role { get; init; } = "plugin";

    /// <summary>Зависимости. Хаб разберёт их при подключении и откажет, если обязательной init-зависимости нет.</summary>
    public IReadOnlyList<Dependency> Dependencies { get; init; } = [];
    /// <summary>Возможности, которые этот клиент предоставляет другим.</summary>
    public IReadOnlyList<Capability> Provides { get; init; } = [];
    /// <summary>К какому хабу подключаться.</summary>
    public HubEndpoint Endpoint { get; init; } = HubEndpoint.Default;

    /// <summary>Транспорты в порядке предпочтения. Убери "shm", чтобы остаться на named pipe.</summary>
    public IReadOnlyList<string> Transports { get; init; } = [ProtoMemProtocol.Shm.ShmFrameChannel.TransportName, "stream"];

    /// <summary>Подключиться по loopback TCP вместо pipe. Нужен <see cref="TcpToken"/>.</summary>
    public int? TcpPort { get; init; }

    /// <summary>Токен из endpoint.json для подключения по TCP.</summary>
    public string? TcpToken { get; init; }

    /// <summary>Пытаться вернуться после пропажи хаба и восстанавливать подписки.</summary>
    public bool AutoReconnect { get; init; }

    /// <summary>Сколько ждать подключения к pipe.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Сколько ждать ответа на вызов, если не указано иное.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
