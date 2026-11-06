using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Ipc;
using QuantumHub.Contracts.Proto;
using QuantumHub.Core.Plugins;
using QuantumHub.ProtoMemProtocol;
using QuantumHub.ProtoMemProtocol.Shm;

namespace QuantumHub.Core;

/// <summary>Принимает клиентов на всех включённых транспортах и проводит рукопожатие.</summary>
/// <param name="hub">Хаб.</param>
/// <param name="options">Настройки хаба.</param>
public sealed class IpcServer(Hub hub, HubOptions options) : IDisposable
{
    /// <summary>Название потокового транспорта в рукопожатии.</summary>
    private const string StreamTransport = "stream";
    /// <summary>Предел размера hello: до проверки клиента много памяти выделять нельзя.</summary>
    private const int MaxHelloSize = 1024 * 1024;
    /// <summary>Название pipe для показа в GUI.</summary>
    private const string PipeTransport = "pipe";

    /// <summary>Сколько ждать hello. Подключившийся и молчащий клиент не должен висеть вечно.</summary>
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Текущие сессии.</summary>
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly CancellationTokenSource _cts = new();
    private PipeListener? _pipe;
    private TcpListener? _tcp;

    /// <summary>Порт TCP, если он включён.</summary>
    public int? TcpPort { get; private set; }
    /// <summary>Случайный токен для подключения по TCP.</summary>
    public string? TcpToken { get; private set; }

    /// <summary>Включённые транспорты.</summary>
    public IReadOnlyList<string> Transports => TcpPort is null
        ? [ShmFrameChannel.TransportName, PipeTransport]
        : [ShmFrameChannel.TransportName, PipeTransport, "tcp"];

    /// <summary>Начинает принимать подключения и публикует endpoint.json.</summary>
    public void Start()
    {
        _pipe = new PipeListener(options.Endpoint.PipeName, stream => HandleConnectionAsync(stream, false),
            ex => hub.Log.Warning($"Pipe listener: {ex.Message}"));
        _pipe.Start();

        if (options.TcpPort is { } port)
        {
            _tcp = new TcpListener(HubEndpoint.TcpEndPoint(port));
            _tcp.Start();
            TcpPort = ((System.Net.IPEndPoint)_tcp.LocalEndpoint).Port;
            TcpToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _ = AcceptTcpAsync(_tcp);
        }

        options.Endpoint.WriteDiscovery(hub.Paths.DataDir, TcpPort, TcpToken);
    }

    /// <summary>Цикл приёма TCP.</summary>
    private async Task AcceptTcpAsync(TcpListener listener)
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(_cts.Token);
                client.NoDelay = true;
                _ = Task.Run(() => HandleConnectionAsync(client.GetStream(), true));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Хаб завершается.
        }
    }

    /// <summary>Рукопожатие: читает hello, проверяет его, разрешает зависимости, выбирает транспорт, создаёт сессию.</summary>
    /// <param name="stream">Поток соединения.</param>
    /// <param name="isTcp">Подключение пришло по TCP и обязано предъявить токен.</param>
    private async Task HandleConnectionAsync(Stream stream, bool isTcp)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(HelloTimeout);
            var request = await FrameCodec.ReadMessageAsync<Envelope>(stream, MaxHelloSize, timeout.Token);

            if (request.Method != Envelopes.HelloMethod)
            {
                await RejectAsync(stream, request, StatusCode.InvalidArgument, "Expected hub.hello.", null);
                return;
            }

            var hello = request.GetPayload().AsProto<HelloRequest>();
            if (hello.ProtocolVersion != Envelopes.ProtocolVersion)
            {
                await RejectAsync(stream, request, StatusCode.FailedPrecondition,
                    $"Protocol {hello.ProtocolVersion} is not supported; the hub speaks {Envelopes.ProtocolVersion}.", null);
                return;
            }

            if (isTcp && !TokenMatches(hello.AuthToken))
            {
                await RejectAsync(stream, request, StatusCode.Unauthenticated, "Wrong auth token.", null);
                return;
            }

            var dependencies = await ResolveDependenciesAsync(hello);
            var response = new HelloResponse
            {
                ProtocolVersion = Envelopes.ProtocolVersion,
                SessionId = Guid.NewGuid().ToString("N"),
                HubVersion = Hub.Version,
                Dependencies = { dependencies },
            };

            var unmet = dependencies.Where(result =>
                !result.Dependency.Optional && result.State is not (DependencyState.Resolved or DependencyState.Deferred)).ToList();
            if (unmet.Count > 0)
            {
                var message = "Required dependencies are unavailable: " + string.Join("; ", unmet.Select(result => result.Detail));
                hub.Log.Warning($"Rejected {hello.ClientId}: {message}");
                await RejectAsync(stream, request, StatusCode.FailedPrecondition, message, response);
                return;
            }

            var channel = OpenChannel(stream, isTcp, hello, response);
            var session = new Session(hub, channel, hello, response.SessionId, dependencies);
            Register(session);

            await FrameCodec.WriteMessageAsync(stream, Envelopes.Response(request, HubPayload.Proto(response)), timeout.Token);
            session.Start();

            hub.Log.Info($"Connected: {hello.ClientId} {hello.Version} over {channel.Transport}");
            hub.Router.Publish(Hub.ConfigOwner, Hub.SessionConnectedTopic, HubPayload.Proto(session.ToInfo()));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or EndOfStreamException
                                       or Google.Protobuf.InvalidProtocolBufferException or InvalidDataException)
        {
            hub.Log.Warning($"Handshake failed: {ex.Message}");
            await stream.DisposeAsync();
        }
    }

    /// <summary>Сравнивает токен за постоянное время, чтобы его нельзя было подобрать по времени ответа.</summary>
    private bool TokenMatches(string token) => TcpToken is not null && CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(TcpToken));

    /// <summary>Отправляет отказ и закрывает соединение.</summary>
    private static async Task RejectAsync(Stream stream, Envelope request, StatusCode status, string message, HelloResponse? details)
    {
        var response = Envelopes.Error(request, status, message);
        if (details is not null)
        {
            var payload = HubPayload.Proto(details);
            response.ContentType = payload.ContentType;
            response.Payload = Google.Protobuf.ByteString.CopyFrom(payload.Data.Span);
        }

        await FrameCodec.WriteMessageAsync(stream, response, CancellationToken.None);
        await stream.DisposeAsync();
    }

    /// <summary>Разрешает init-зависимости клиента. Ленивые отмечаются как отложенные.</summary>
    private async Task<List<DependencyResolution>> ResolveDependenciesAsync(HelloRequest hello)
    {
        var results = new List<DependencyResolution>();
        foreach (var dependency in hello.Dependencies)
        {
            results.Add(DependencyResolver.IsInitTime(dependency)
                ? await hub.Plugins.ResolveAsync(dependency)
                : new DependencyResolution { Dependency = dependency, State = DependencyState.Deferred });
        }

        return results;
    }

    /// <summary>Выбирает транспорт и создаёт канал.</summary>
    private IFrameChannel OpenChannel(Stream stream, bool isTcp, HelloRequest hello, HelloResponse response)
    {
        // Разделяемой памяти нужен pipe как линия жизни. По TCP другая сторона может
        // вообще не уметь работать с памятью, поэтому TCP всегда остаётся обычным потоком.
        if (!isTcp && hello.Transports.Contains(ShmFrameChannel.TransportName))
        {
            var name = HubEndpoint.NewMappingName();
            response.Transport = ShmFrameChannel.TransportName;
            response.Shm = new ShmInfo { Name = name, RingSize = options.RingSize };
            return ShmFrameChannel.CreateServer(name, options.RingSize, stream);
        }

        response.Transport = StreamTransport;
        return new StreamFrameChannel(stream, isTcp ? "tcp" : PipeTransport);
    }

    /// <summary>Делает сессию доступной по адресам.</summary>
    private void Register(Session session)
    {
        _sessions[session.SessionId] = session;
        hub.Router.Register(session.Address, session);
        hub.Router.Register(session.Id, session);
        session.Closed += OnSessionClosed;
    }

    /// <summary>Убирает сессию из всех таблиц.</summary>
    private void OnSessionClosed(Session session)
    {
        _sessions.TryRemove(session.SessionId, out _);
        hub.Router.Unregister(session.Address, session);
        hub.Router.Unregister(session.Id, session);
        hub.Router.UnsubscribeAll(session);
        hub.Log.Info($"Disconnected: {session.Id}");
        hub.Router.Publish(Hub.ConfigOwner, Hub.SessionDisconnectedTopic, HubPayload.Proto(session.ToInfo()));
    }

    /// <summary>Текущие подключения.</summary>
    public SessionList ListSessions()
    {
        var list = new SessionList();
        list.Sessions.Add(_sessions.Values.Select(session => session.ToInfo()));
        return list;
    }

    /// <summary>Клиенты как поставщики возможностей.</summary>
    public IEnumerable<ProviderCandidate> Providers() => _sessions.Values
        .Where(session => session.Provides.Count > 0)
        .Select(session => new ProviderCandidate(session.Id, session.Version, session.Provides));

    /// <summary>Останавливает приём и закрывает все сессии.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _pipe?.Dispose();
        _tcp?.Stop();
        foreach (var session in _sessions.Values) session.Close();
        try { File.Delete(HubEndpoint.DiscoveryPath(hub.Paths.DataDir)); } catch (IOException) { }
    }
}
