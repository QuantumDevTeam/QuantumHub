using Quantum.Hub.Contracts.Proto.Core;
using QuantumHub.Contracts.Ipc;

namespace QuantumHub.ProtoMemProtocol.Loop;

public class EventLoop
    : IEventLoop
{
    private CancellationTokenSource? _cts;
    private Task? _runningTask;

    public async Task RunAsync(IChannel channel, Func<HubMessage, Task> handler, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var message = await channel.Messenger.ReceiveAsync(token);
                await handler(message);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // ILogger using
            }
        }
        await Task.CompletedTask;
    }

    public async Task StartAsync(IChannel channel, Func<HubMessage, Task> handler, CancellationToken token)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _runningTask = Task.Run(async () => await RunAsync(channel, handler, _cts.Token), _cts.Token);
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        await _cts?.CancelAsync()!;
        if (_runningTask != null)
        {
            try
            {
                await _runningTask;
            }
            catch (Exception)
            {
                // ILogger using
            }
        }
    }
}