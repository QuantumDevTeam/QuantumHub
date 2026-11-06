using System.Collections.Concurrent;
using QuantumHub.Contracts.Ipc;
using QuantumHub.Contracts.Proto;
using QuantumHub.Core.Routing;
using QuantumHub.ProtoMemProtocol;

namespace QuantumHub.Core;

/// <summary>Подключённый клиент: сторона хаба в одном IPC-соединении.</summary>
public sealed class Session : IEndpoint
{
    private readonly Hub _hub;
    private readonly IFrameChannel _channel;
    private readonly HelloRequest _hello;
    /// <summary>Запросы от хаба к клиенту, ждущие ответа.</summary>
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<Envelope>> _pending = new();
    /// <summary>Отменяется при обрыве.</summary>
    private readonly CancellationTokenSource _lifetime = new();
    private readonly long _connectedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private ulong _nextId;
    private ulong _messagesIn;
    private ulong _messagesOut;

    /// <summary>Создаёт сессию. Читать она начнёт после <see cref="Start"/>.</summary>
    /// <param name="hub">Хаб.</param>
    /// <param name="channel">Канал.</param>
    /// <param name="hello">Что клиент сообщил о себе.</param>
    /// <param name="sessionId">Id сессии.</param>
    /// <param name="dependencies">Результат разрешения зависимостей.</param>
    public Session(Hub hub, IFrameChannel channel, HelloRequest hello, string sessionId,
        IReadOnlyList<DependencyResolution> dependencies)
    {
        _hub = hub;
        _channel = channel;
        _hello = hello;
        SessionId = sessionId;
        Dependencies = dependencies;
    }

    /// <summary>Id сессии.</summary>
    public string SessionId { get; }
    /// <summary>Id клиента.</summary>
    public string Id => _hello.ClientId;
    /// <summary>Результат разрешения зависимостей при подключении.</summary>
    public IReadOnlyList<DependencyResolution> Dependencies { get; }
    /// <summary>Что клиент предоставляет другим.</summary>
    public IReadOnlyList<Capability> Provides => _hello.Provides;
    /// <summary>Версия клиента.</summary>
    public string Version => _hello.Version;
    /// <summary>Адрес именно этого соединения. Отличается от id, если клиентов с таким id несколько.</summary>
    public string Address => $"session:{SessionId}";

    /// <summary>Соединение закрылось.</summary>
    public event Action<Session>? Closed;

    /// <summary>Начинает обмен.</summary>
    public void Start() => _channel.Start(OnFrame, OnClosed);

    /// <summary>Разбирает кадр на потоке чтения и раздаёт по типу.</summary>
    private void OnFrame(ReadOnlySpan<byte> body)
    {
        var envelope = FrameCodec.Decode(body);
        Interlocked.Increment(ref _messagesIn);

        // Личность берётся из соединения, а не из того, что клиент о себе заявил.
        envelope.Source = Id;

        switch (envelope.Kind)
        {
            case EnvelopeKind.Response:
                if (_pending.TryRemove(envelope.Id, out var waiter)) waiter.TrySetResult(envelope);
                break;

            case EnvelopeKind.Event:
                _hub.Router.Publish(envelope);
                break;

            case EnvelopeKind.Request when envelope is { Target: Envelopes.HubTarget, Method: "hub.ping" }:
                // Отвечаем прямо на потоке чтения: это замер задержки самого транспорта.
                _ = SendAsync(Envelopes.Response(envelope, envelope.GetPayload()));
                break;

            case EnvelopeKind.Request:
                ThreadPool.UnsafeQueueUserWorkItem(static state => _ = state.session.HandleRequestAsync(state.envelope),
                    (session: this, envelope), preferLocal: false);
                break;
        }
    }

    /// <summary>Выполняет запрос клиента через роутер и отправляет ответ.</summary>
    private async Task HandleRequestAsync(Envelope request)
    {
        var response = request is { Target: Envelopes.HubTarget, Method: "hub.subscribe" or "hub.unsubscribe" }
            ? HandleSubscription(request)
            : await _hub.Router.RequestAsync(request, _lifetime.Token);

        response.Kind = EnvelopeKind.Response;
        response.Id = request.Id;
        await SendAsync(response);
    }

    /// <summary>Обрабатывает подписку. Она сделана здесь, потому что ей нужна сама сессия.</summary>
    private Envelope HandleSubscription(Envelope request)
    {
        var topics = request.GetPayload().AsProto<SubscribeRequest>().Topics;
        foreach (var topic in topics)
        {
            if (request.Method == "hub.subscribe") _hub.Router.Subscribe(topic, this);
            else _hub.Router.Unsubscribe(topic, this);
        }

        return Envelopes.Response(request, default);
    }

    /// <summary>Отправляет конверт. Если клиента уже нет, ошибки не будет: уборкой займётся <see cref="OnClosed"/>.</summary>
    private async Task SendAsync(Envelope envelope)
    {
        try
        {
            Interlocked.Increment(ref _messagesOut);
            await _channel.SendEnvelopeAsync(envelope, _lifetime.Token);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or OperationCanceledException)
        {
            // Клиент ушёл, уборкой займётся OnClosed.
        }
    }

    /// <summary>Передаёт запрос клиенту и ждёт ответ. Id заменяется своим, чтобы не пересечься с другими вызывающими.</summary>
    /// <param name="request">Запрос.</param>
    /// <param name="token">Отмена.</param>
    public async Task<Envelope> RequestAsync(Envelope request, CancellationToken token)
    {
        var outgoing = request.Clone();
        outgoing.Id = Interlocked.Increment(ref _nextId);

        var waiter = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[outgoing.Id] = waiter;
        try
        {
            await SendAsync(outgoing);
            var response = await waiter.Task.WaitAsync(token);
            response.Id = request.Id;
            return response;
        }
        finally
        {
            _pending.TryRemove(outgoing.Id, out _);
        }
    }

    /// <inheritdoc/>
    public void DeliverEvent(Envelope hubEvent) => _ = SendAsync(hubEvent);

    /// <summary>Уборка после обрыва: завершить ожидающие запросы ошибкой и сообщить владельцу.</summary>
    private void OnClosed(Exception? failure)
    {
        _lifetime.Cancel();
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiter))
                waiter.TrySetResult(Envelopes.Error(new Envelope { Id = id }, StatusCode.Unavailable, $"'{Id}' disconnected."));
        }

        _channel.Dispose();
        if (failure is not null) _hub.Log.Warning($"Connection of {Id} failed: {failure.Message}");
        Closed?.Invoke(this);
    }

    /// <summary>Закрывает соединение со стороны хаба.</summary>
    public void Close() => _channel.Dispose();

    /// <summary>Сведения о сессии для GUI.</summary>
    public SessionInfo ToInfo()
    {
        var info = new SessionInfo
        {
            SessionId = SessionId,
            ClientId = _hello.ClientId,
            DisplayName = _hello.DisplayName,
            Version = _hello.Version,
            Role = _hello.Role,
            Transport = _channel.Transport,
            ProcessId = _hello.ProcessId,
            ConnectedUnixMs = _connectedAt,
            MessagesIn = Interlocked.Read(ref _messagesIn),
            MessagesOut = Interlocked.Read(ref _messagesOut),
        };
        info.Dependencies.Add(Dependencies);
        return info;
    }
}
