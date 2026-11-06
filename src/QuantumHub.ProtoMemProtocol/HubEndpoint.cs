using System.Net;
using System.Security.Principal;
using System.Text.Json;

namespace QuantumHub.ProtoMemProtocol;

/// <summary>Где искать работающий хаб.</summary>
/// <remarks>
/// Имена pipe общие на всю машину, поэтому в них входит SID пользователя: два человека в разных
/// сессиях получают по своему хабу. Именованные экземпляры нужны для тестов, которые идут параллельно
/// с настоящим хабом.
/// </remarks>
/// <param name="Instance">Имя экземпляра.</param>
public sealed record HubEndpoint(string Instance)
{
    /// <summary>Имя экземпляра по умолчанию.</summary>
    public const string DefaultInstance = "default";

    /// <summary>Обычный хаб текущего пользователя.</summary>
    public static HubEndpoint Default { get; } = new(DefaultInstance);

    /// <summary>Имя named pipe без префикса <c>\\.\pipe\</c>.</summary>
    public string PipeName { get; } = $"QuantumHub.{WindowsIdentity.GetCurrent().User!.Value}.{Instance}";

    /// <summary>Имя мьютекса, не дающего запустить второй хаб.</summary>
    public string MutexName { get; } = $@"Local\QuantumHub.{Instance}";

    /// <summary>Создаёт имя секции памяти: локальное для сессии и намеренно неугадываемое.</summary>
    public static string NewMappingName() => $@"Local\QuantumHub.{Guid.NewGuid():N}";

    /// <summary>Содержимое endpoint.json. Точка входа для плагинов не на .NET, которым проще прочитать файл, чем вычислять имя pipe.</summary>
    /// <param name="PipePath">Полный путь к pipe.</param>
    /// <param name="ProcessId">Процесс хаба.</param>
    /// <param name="TcpPort">Порт TCP, если он включён.</param>
    /// <param name="TcpToken">Токен для подключения по TCP.</param>
    public sealed record Discovery(string PipePath, int ProcessId, int? TcpPort, string? TcpToken);

    /// <summary>Путь к endpoint.json в папке данных.</summary>
    /// <param name="dataDir">Папка данных хаба.</param>
    public static string DiscoveryPath(string dataDir) => Path.Combine(dataDir, "endpoint.json");

    /// <summary>Публикует endpoint.json.</summary>
    /// <param name="dataDir">Папка данных хаба.</param>
    /// <param name="tcpPort">Порт TCP или <see langword="null"/>.</param>
    /// <param name="tcpToken">Токен или <see langword="null"/>.</param>
    public void WriteDiscovery(string dataDir, int? tcpPort, string? tcpToken)
    {
        var discovery = new Discovery($@"\\.\pipe\{PipeName}", Environment.ProcessId, tcpPort, tcpToken);
        File.WriteAllText(DiscoveryPath(dataDir), JsonSerializer.Serialize(discovery,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    /// <summary>Адрес для TCP: только loopback, наружу хаб не слушает.</summary>
    /// <param name="port">Порт.</param>
    public static IPEndPoint TcpEndPoint(int port) => new(IPAddress.Loopback, port);
}
