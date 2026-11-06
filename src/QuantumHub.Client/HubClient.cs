using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Ipc;
using QuantumHub.Contracts.Proto;
using QuantumHub.ProtoMemProtocol;
using QuantumHub.ProtoMemProtocol.Shm;

namespace QuantumHub.Client;

/// <summary>Хаб отказал в подключении, обычно из-за недоступной обязательной зависимости.</summary>
/// <param name="status">Код причины.</param>
/// <param name="message">Описание для человека.</param>
/// <param name="dependencies">Что хаб нашёл для каждой зависимости.</param>
public sealed class HubRejectedException(StatusCode status, string message, IReadOnlyList<DependencyResolution> dependencies)
    : Exception(message)
{
    /// <summary>Код причины отказа.</summary>
    public StatusCode Status { get; } = status;
    /// <summary>Что хаб нашёл для каждой зависимости.</summary>
    public IReadOnlyList<DependencyResolution> Dependencies { get; } = dependencies;
}

/// <summary>Подключение к хабу из другого процесса: SDK для внешних плагинов на .NET.</summary>
/// <remarks>
/// Реализует тот же <see cref="IHubBus"/>, что и контекст плагина внутри хаба.
/// Как устроен обмен: кадр приходит на поток чтения, ответы завершают ожидание в <c>_pending</c>,
/// запросы уходят в пул потоков, события — в <see cref="EventPump"/>.
/// </remarks>
/// <example>
/// <code>
/// await using var client = await HubClient.ConnectAsync(new HubClientOptions
/// {
///     ClientId = "acme.linecounter.rider",
///     Dependencies = [new Dependency { Id = "acme.linecounter", Version = ">=1.0.0" }],
/// });
/// await client.CallAsync("acme.linecounter", "lines.add", HubPayload.Json(new { count = 5 }));
/// </code>
/// </example>
public sealed class HubClient : IHubBus, IAsyncDisposable
{
    private readonly HubClientOptions _options;
    /// <summary>Вызовы, ждущие ответа. Ключ — id запроса.</summary>
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<Envelope>> _pending = new();
    /// <summary>Методы, которые этот клиент предоставляет другим.</summary>
    private readonly ConcurrentDictionary<string, HubRequestHandler> _handlers = new();
    private readonly EventPump _events;
    /// <summary>Отменяется при закрытии клиента.</summary>
    private readonly CancellationTokenSource _lifetime = new();
    /// <summary>Текущий канал. <see langword="null"/>, пока связи нет.</summary>
    private volatile IFrameChannel? _channel;
    /// <summary>Ответ хаба на последнее рукопожатие.</summary>
    private HelloResponse _hello = new();
    private ulong _nextId;

    private HubClient(HubClientOptions options)
    {
        _options = options;
        _events = new EventPump(ex => Console.Error.WriteLine($"[QuantumHub] Event handler failed: {ex}"));
    }

    /// <summary>Есть ли связь прямо сейчас.</summary>
    public bool IsConnected => _channel is not null;
    /// <summary>Id сессии у хаба.</summary>
    public string SessionId => _hello.SessionId;
    /// <summary>Выбранный транспорт: "shm" или "stream".</summary>
    public string Transport => _hello.Transport;
    /// <summary>Версия хаба.</summary>
    public string HubVersion => _hello.HubVersion;

    /// <summary>Что хаб нашёл для каждой объявленной зависимости при последнем подключении.</summary>
    public IReadOnlyList<DependencyResolution> Dependencies => _hello.Dependencies;

    /// <summary>Связь потеряна. Срабатывает на фоновом потоке.</summary>
    public event Action? Disconnected;
    /// <summary>Связь восстановлена, подписки уже на месте. Срабатывает на фоновом потоке.</summary>
    public event Action? Reconnected;

    /// <summary>Подключается к хабу.</summary>
    /// <param name="options">Настройки.</param>
    /// <param name="token">Отмена.</param>
    /// <returns>Подключённый клиент.</returns>
    /// <exception cref="HubRejectedException">Хаб работает, но отказал в подключении.</exception>
    /// <exception cref="TimeoutException">Хаб не ответил.</exception>
    public static async Task<HubClient> ConnectAsync(HubClientOptions options, CancellationToken token = default)
    {
        var client = new HubClient(options);
        try
        {
            await client.EstablishAsync(token);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>Рукопожатие: открыть поток, отправить hello, разобрать ответ, поднять канал.</summary>
    private async Task EstablishAsync(CancellationToken token)
    {
        var stream = await OpenStreamAsync(token);
        try
        {
            var hello = new HelloRequest
            {
                ProtocolVersion = Envelopes.ProtocolVersion,
                ClientId = _options.ClientId,
                DisplayName = _options.DisplayName ?? _options.ClientId,
                Version = _options.Version,
                Role = _options.Role,
                ProcessId = (uint)Environment.ProcessId,
                AuthToken = _options.TcpToken ?? string.Empty,
            };
            hello.Transports.Add(_options.Transports);
            hello.Dependencies.Add(_options.Dependencies);
            hello.Provides.Add(_options.Provides);

            var request = Envelopes.Request(_options.ClientId, Envelopes.HubTarget, Envelopes.HelloMethod, HubPayload.Proto(hello));
            await FrameCodec.WriteMessageAsync(stream, request, token);

            // Щедро намеренно: хаб может запускать для нас зависимости по требованию.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var response = await FrameCodec.ReadMessageAsync<Envelope>(stream, 16 * 1024 * 1024, timeout.Token);

            var payload = response.GetPayload();
            var helloResponse = payload.IsEmpty ? new HelloResponse() : payload.AsProto<HelloResponse>();
            if (response.Status != StatusCode.Ok)
                throw new HubRejectedException(response.Status, response.Error, helloResponse.Dependencies);

            IFrameChannel channel = helloResponse.Transport == ShmFrameChannel.TransportName
                ? ShmFrameChannel.OpenClient(helloResponse.Shm.Name, helloResponse.Shm.RingSize, stream)
                : new StreamFrameChannel(stream, helloResponse.Transport);

            _hello = helloResponse;
            _channel = channel;
            channel.Start(OnFrame, _ => OnClosed(channel));
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    /// <summary>Открывает pipe или TCP-соединение.</summary>
    private async Task<Stream> OpenStreamAsync(CancellationToken token)
    {
        if (_options.TcpPort is { } port)
        {
            var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(HubEndpoint.TcpEndPoint(port), token);
            return tcp.GetStream();
        }

        var pipe = new NamedPipeClientStream(".", _options.Endpoint.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)_options.ConnectTimeout.TotalMilliseconds, token);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    /// <summary>Разбирает кадр на потоке чтения и раздаёт по типу. Здесь нельзя делать ничего долгого.</summary>
    private void OnFrame(ReadOnlySpan<byte> body)
    {
        var envelope = FrameCodec.Decode(body);
        switch (envelope.Kind)
        {
            case EnvelopeKind.Response:
                if (_pending.TryRemove(envelope.Id, out var waiter)) waiter.TrySetResult(envelope);
                break;
            case EnvelopeKind.Event:
                _events.Post(new HubEvent(envelope.Source, envelope.Method, envelope.GetPayload()));
                break;
            case EnvelopeKind.Request:
                // Не на потоке чтения, чтобы медленный обработчик не остановил соединение.
                ThreadPool.UnsafeQueueUserWorkItem(static state => _ = state.client.HandleRequestAsync(state.envelope),
                    (client: this, envelope), preferLocal: false);
                break;
        }
    }

    /// <summary>Выполняет входящий запрос и отправляет ответ.</summary>
    private async Task HandleRequestAsync(Envelope request)
    {
        Envelope response;
        try
        {
            if (_handlers.TryGetValue(request.Method, out var handler))
            {
                var payload = new HubRequest(request.Source, request.Method, request.GetPayload());
                response = Envelopes.Response(request, await handler(payload, _lifetime.Token));
            }
            else
            {
                response = Envelopes.Error(request, StatusCode.NotFound, $"'{_options.ClientId}' has no method '{request.Method}'.");
            }
        }
        catch (HubCallException ex)
        {
            response = Envelopes.Error(request, ex.Status, ex.Message);
        }
        catch (Exception ex)
        {
            response = Envelopes.Error(request, StatusCode.Internal, ex.Message);
        }

        try
        {
            if (_channel is { } channel) await channel.SendEnvelopeAsync(response);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException)
        {
            // Вызывающая сторона ушла, этот ответ никто не ждёт.
        }
    }

    /// <summary>Реакция на обрыв: завершить ожидающие вызовы ошибкой и при необходимости начать переподключение.</summary>
    private void OnClosed(IFrameChannel channel)
    {
        if (Interlocked.CompareExchange(ref _channel, null, channel) != channel) return;
        channel.Dispose();

        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiter))
                waiter.TrySetException(new HubCallException(StatusCode.Unavailable, "The connection to the hub was lost."));
        }

        Disconnected?.Invoke();
        if (_options.AutoReconnect && !_lifetime.IsCancellationRequested) _ = ReconnectAsync();
    }

    /// <summary>Повторяет подключение, пока оно не удастся, и восстанавливает подписки.</summary>
    private async Task ReconnectAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, _lifetime.Token);
                await EstablishAsync(_lifetime.Token);

                foreach (var pattern in _events.Patterns())
                    await CallAsync(Envelopes.HubTarget, "hub.subscribe", HubPayload.Proto(Topics(pattern)));

                Reconnected?.Invoke();
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or HubRejectedException or HubCallException or UnauthorizedAccessException)
            {
                // Пока не вернулся.
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask<HubPayload> CallAsync(string target, string method, HubPayload payload = default,
        TimeSpan? timeout = null, CancellationToken token = default)
    {
        var channel = _channel ?? throw new HubCallException(StatusCode.Unavailable, "Not connected to the hub.");

        var request = Envelopes.Request(_options.ClientId, target, method, payload);
        request.Id = Interlocked.Increment(ref _nextId);
        var waiter = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = waiter;

        try
        {
            await channel.SendEnvelopeAsync(request, token);
            var response = await waiter.Task.WaitAsync(timeout ?? _options.CallTimeout, token);
            return response.Unwrap();
        }
        catch (TimeoutException)
        {
            throw new HubCallException(StatusCode.Timeout, $"'{target}' did not answer '{method}' in time.");
        }
        catch (ObjectDisposedException)
        {
            throw new HubCallException(StatusCode.Unavailable, "The connection to the hub was lost.");
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    /// <inheritdoc/>
    public async ValueTask PublishAsync(string topic, HubPayload payload = default, CancellationToken token = default)
    {
        var channel = _channel ?? throw new HubCallException(StatusCode.Unavailable, "Not connected to the hub.");
        await channel.SendEnvelopeAsync(Envelopes.Event(_options.ClientId, topic, payload), token);
    }

    /// <inheritdoc/>
    public async ValueTask<IDisposable> SubscribeAsync(string topic, Action<HubEvent> handler, CancellationToken token = default)
    {
        if (_events.Add(topic, handler))
            await CallAsync(Envelopes.HubTarget, "hub.subscribe", HubPayload.Proto(Topics(topic)), null, token);

        return new Disposer(() =>
        {
            if (_events.Remove(topic, handler) && IsConnected)
                _ = CallAsync(Envelopes.HubTarget, "hub.unsubscribe", HubPayload.Proto(Topics(topic))).AsTask()
                    .ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        });
    }

    /// <summary>Собирает запрос подписки на один топик.</summary>
    private static SubscribeRequest Topics(string topic) => new() { Topics = { topic } };

    /// <inheritdoc/>
    public IDisposable Handle(string method, HubRequestHandler handler)
    {
        _handlers[method] = handler;
        return new Disposer(() => _handlers.TryRemove(method, out _));
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<DependencyResolution>> ResolveAsync(IEnumerable<Dependency> dependencies,
        CancellationToken token = default)
    {
        var response = await this.CallAsync<ResolveResponse>(Envelopes.HubTarget, "hub.resolve",
            new ResolveRequest { Dependencies = { dependencies } }, token);
        return response.Results;
    }

    /// <summary>Закрывает соединение. Переподключения после этого не будет.</summary>
    public ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        var channel = Interlocked.Exchange(ref _channel, null);
        channel?.Dispose();
        _events.Dispose();
        return default;
    }

    /// <summary>Выполняет действие один раз при <see cref="IDisposable.Dispose"/>.</summary>
    private sealed class Disposer(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
