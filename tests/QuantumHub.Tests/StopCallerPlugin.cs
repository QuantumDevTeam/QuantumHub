using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Plugin;

namespace QuantumHub.Tests;

/// <summary>Плагин для теста: при остановке вызывает хаб, как VR Switcher при возврате звука.</summary>
public sealed class StopCallerPlugin : IHubPlugin
{
    /// <summary>Сюда пишется результат вызова.</summary>
    public const string ResultFile = "result.txt";

    private IPluginContext _context = null!;

    /// <inheritdoc/>
    public Task StartAsync(IPluginContext context, CancellationToken token)
    {
        _context = context;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken token)
    {
        var path = Path.Combine(_context.Paths.DataDir, ResultFile);
        try
        {
            var echo = await _context.Bus.CallAsync("hub", "hub.ping", HubPayload.Text("reached"), token: token);
            await File.WriteAllTextAsync(path, echo.AsText(), CancellationToken.None);
        }
        catch (HubCallException ex)
        {
            await File.WriteAllTextAsync(path, ex.Message, CancellationToken.None);
        }
    }
}
