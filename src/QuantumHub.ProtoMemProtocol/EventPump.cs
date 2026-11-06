using System.Threading.Channels;
using QuantumHub.Contracts.Bus;

namespace QuantumHub.ProtoMemProtocol;

/// <summary>Доставляет события локальным обработчикам по порядку, на отдельной фоновой задаче.</summary>
/// <remarks>Медленный обработчик задерживает только своего владельца: читатель соединения продолжает работать.</remarks>
public sealed class EventPump : IDisposable
{
    private readonly Channel<HubEvent> _queue = Channel.CreateUnbounded<HubEvent>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly List<(string Pattern, Action<HubEvent> Handler)> _handlers = [];
    private readonly Action<Exception> _onError;

    /// <summary>Запускает доставку.</summary>
    /// <param name="onError">Куда сообщать об исключениях обработчиков.</param>
    public EventPump(Action<Exception> onError)
    {
        _onError = onError;
        _ = Task.Run(RunAsync);
    }

    /// <summary>Ставит событие в очередь. Не блокирует.</summary>
    /// <param name="hubEvent">Событие.</param>
    public void Post(HubEvent hubEvent) => _queue.Writer.TryWrite(hubEvent);

    /// <summary>Добавляет обработчик.</summary>
    /// <param name="pattern">Топик или префикс с ".*".</param>
    /// <param name="handler">Обработчик.</param>
    /// <returns><see langword="true"/>, если это первый обработчик шаблона и нужно подписаться у хаба.</returns>
    public bool Add(string pattern, Action<HubEvent> handler)
    {
        lock (_handlers)
        {
            var first = !_handlers.Exists(entry => entry.Pattern == pattern);
            _handlers.Add((pattern, handler));
            return first;
        }
    }

    /// <summary>Убирает обработчик.</summary>
    /// <param name="pattern">Шаблон, с которым он был добавлен.</param>
    /// <param name="handler">Обработчик.</param>
    /// <returns><see langword="true"/>, если обработчиков шаблона больше нет и подписку можно снять.</returns>
    public bool Remove(string pattern, Action<HubEvent> handler)
    {
        lock (_handlers)
        {
            _handlers.Remove((pattern, handler));
            return !_handlers.Exists(entry => entry.Pattern == pattern);
        }
    }

    /// <summary>Все текущие шаблоны. Нужны, чтобы восстановить подписки после переподключения.</summary>
    public string[] Patterns()
    {
        lock (_handlers) return _handlers.Select(entry => entry.Pattern).Distinct().ToArray();
    }

    /// <summary>Цикл доставки. Исключение обработчика не мешает остальным.</summary>
    private async Task RunAsync()
    {
        await foreach (var hubEvent in _queue.Reader.ReadAllAsync())
        {
            (string Pattern, Action<HubEvent> Handler)[] snapshot;
            lock (_handlers) snapshot = _handlers.ToArray();

            foreach (var (pattern, handler) in snapshot)
            {
                if (!TopicPattern.Matches(pattern, hubEvent.Topic)) continue;
                try
                {
                    handler(hubEvent);
                }
                catch (Exception ex)
                {
                    _onError(ex);
                }
            }
        }
    }

    /// <summary>Останавливает доставку и забывает обработчики.</summary>
    public void Dispose()
    {
        _queue.Writer.TryComplete();
        lock (_handlers) _handlers.Clear();
    }
}
