namespace QuantumHub.Contracts.Core;

public interface ILogger
{
    int Ident { get; }
    
    Task LogAsync(LogLevel level, string message, CancellationToken token = default);
}