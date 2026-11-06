using System.IO.Pipes;

namespace QuantumHub.ProtoMemProtocol;

/// <summary>Принимает подключения на named pipe хаба. Подключиться может только тот же пользователь.</summary>
/// <param name="pipeName">Имя pipe.</param>
/// <param name="onConnection">Вызывается на каждое подключение. Поток переходит к обработчику.</param>
/// <param name="onError">Сообщает об ошибках приёма.</param>
public sealed class PipeListener(string pipeName, Func<Stream, Task> onConnection, Action<Exception> onError) : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Начинает принимать подключения.</summary>
    /// <exception cref="IOException">Имя уже занято: хаб запущен, или кто-то пытается его подменить.</exception>
    public void Start()
    {
        // FirstPipeInstance бросит исключение, если имя уже занято.
        // Иначе чужой процесс мог бы занять pipe заранее и притвориться хабом.
        var first = Create(PipeOptions.FirstPipeInstance);
        _loop = Task.Run(() => AcceptLoopAsync(first));
    }

    /// <summary>Создаёт следующий экземпляр pipe. Один экземпляр обслуживает ровно одного клиента.</summary>
    private NamedPipeServerStream Create(PipeOptions extra) => new(pipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | extra, BufferSize, BufferSize);

    /// <summary>Цикл приёма. Новый экземпляр создаётся сразу после подключения, чтобы следующий клиент не ждал.</summary>
    private async Task AcceptLoopAsync(NamedPipeServerStream next)
    {
        while (!_cts.IsCancellationRequested)
        {
            var current = next;
            try
            {
                await current.WaitForConnectionAsync(_cts.Token);
                next = Create(PipeOptions.None);
            }
            catch (OperationCanceledException)
            {
                await current.DisposeAsync();
                return;
            }
            catch (Exception ex)
            {
                onError(ex);
                await current.DisposeAsync();
                next = Create(PipeOptions.None);
                continue;
            }

            _ = Task.Run(() => onConnection(current));
        }
    }

    /// <summary>Останавливает приём. Уже подключённых клиентов это не затрагивает.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }
}
