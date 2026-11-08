using QuantumHub.Contracts.Ui;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Proto;
using QuantumHub.ProtoMemProtocol;

namespace QuantumHub.Core.Routing;

/// <summary>Передаёт конверты между конечными точками. Читает только адресные поля, тело не трогает никогда.</summary>
public sealed class Router
{
    /// <summary>Префикс адреса возможности.</summary>
    public const string CapabilityPrefix = "cap:";

    /// <summary>Верхний предел ожидания ответа.</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Адреса конечных точек.</summary>
    private readonly ConcurrentDictionary<string, IEndpoint> _endpoints = new();
    /// <summary>Подписки. Неизменяемый массив: рассылка читает его без блокировок, а подписки меняются редко.</summary>
    private ImmutableArray<(string Pattern, IEndpoint Endpoint)> _subscriptions = [];

    /// <summary>Находит конечную точку, которая ещё не зарегистрирована, при необходимости запуская плагин.</summary>
    public Func<string, CancellationToken, Task<IEndpoint?>>? ActivateTarget { get; set; }

    /// <summary>Регистрирует адрес.</summary>
    /// <param name="address">Адрес.</param>
    /// <param name="endpoint">Конечная точка.</param>
    public void Register(string address, IEndpoint endpoint) => _endpoints[address] = endpoint;

    /// <summary>Убирает адрес, только если он всё ещё указывает на эту точку. Новая сессия с тем же id не пострадает.</summary>
    /// <param name="address">Адрес.</param>
    /// <param name="endpoint">Конечная точка.</param>
    public void Unregister(string address, IEndpoint endpoint) =>
        _endpoints.TryRemove(new KeyValuePair<string, IEndpoint>(address, endpoint));

    /// <summary>Занят ли адрес.</summary>
    /// <param name="address">Адрес.</param>
    public bool IsRegistered(string address) => _endpoints.ContainsKey(address);

    /// <summary>Конечная точка по адресу.</summary>
    /// <param name="address">Адрес.</param>
    public IEndpoint? Find(string address) => _endpoints.GetValueOrDefault(address);

    /// <summary>Доставляет запрос и ждёт ответ. Никогда не бросает исключений: любая неудача становится ответом с ошибкой.</summary>
    /// <param name="request">Запрос.</param>
    /// <param name="token">Отмена.</param>
    public async Task<Envelope> RequestAsync(Envelope request, CancellationToken token = default)
    {
        try
        {
            if (!_endpoints.TryGetValue(request.Target, out var endpoint))
            {
                endpoint = ActivateTarget is null ? null : await ActivateTarget(request.Target, token);
                if (endpoint is null)
                    return Envelopes.Error(request, StatusCode.NotFound, Loc.T($"По адресу '{request.Target}' никого нет.", $"Nothing is listening at '{request.Target}'."));
            }

            // Плагин, который не отвечает, не должен оставлять вызывающего ждать вечно.
            return await endpoint.RequestAsync(request, token).WaitAsync(DefaultTimeout, token);
        }
        catch (TimeoutException)
        {
            return Envelopes.Error(request, StatusCode.Timeout, Loc.T($"'{request.Target}' не ответил на '{request.Method}' вовремя.", $"'{request.Target}' did not answer '{request.Method}' in time."));
        }
        catch (OperationCanceledException)
        {
            return Envelopes.Error(request, StatusCode.Unavailable, Loc.T("Запрос отменён.", "The request was cancelled."));
        }
        catch (HubCallException ex)
        {
            return Envelopes.Error(request, ex.Status, ex.Message);
        }
        catch (Exception ex)
        {
            return Envelopes.Error(request, StatusCode.Internal, ex.Message);
        }
    }

    /// <summary>Подписывает конечную точку.</summary>
    /// <param name="pattern">Шаблон топика.</param>
    /// <param name="endpoint">Подписчик.</param>
    public void Subscribe(string pattern, IEndpoint endpoint) =>
        ImmutableInterlocked.Update(ref _subscriptions, list =>
            list.Contains((pattern, endpoint)) ? list : list.Add((pattern, endpoint)));

    /// <summary>Снимает подписку.</summary>
    /// <param name="pattern">Шаблон топика.</param>
    /// <param name="endpoint">Подписчик.</param>
    public void Unsubscribe(string pattern, IEndpoint endpoint) =>
        ImmutableInterlocked.Update(ref _subscriptions, list => list.Remove((pattern, endpoint)));

    /// <summary>Снимает все подписки точки.</summary>
    /// <param name="endpoint">Подписчик.</param>
    public void UnsubscribeAll(IEndpoint endpoint) =>
        ImmutableInterlocked.Update(ref _subscriptions, list => list.RemoveAll(entry => entry.Endpoint == endpoint));

    /// <summary>Есть ли подписчики у топика. Позволяет не собирать событие, которое некому отправить.</summary>
    /// <param name="topic">Топик.</param>
    public bool HasSubscribers(string topic)
    {
        foreach (var (pattern, _) in _subscriptions)
        {
            if (TopicPattern.Matches(pattern, topic)) return true;
        }

        return false;
    }

    /// <summary>Рассылает готовое событие.</summary>
    /// <param name="hubEvent">Событие.</param>
    public void Publish(Envelope hubEvent)
    {
        foreach (var (pattern, endpoint) in _subscriptions)
        {
            if (TopicPattern.Matches(pattern, hubEvent.Method)) endpoint.DeliverEvent(hubEvent);
        }
    }

    /// <summary>Собирает и рассылает событие, если есть подписчики.</summary>
    /// <param name="source">Отправитель.</param>
    /// <param name="topic">Топик.</param>
    /// <param name="payload">Тело.</param>
    public void Publish(string source, string topic, HubPayload payload)
    {
        if (HasSubscribers(topic)) Publish(Envelopes.Event(source, topic, payload));
    }
}
