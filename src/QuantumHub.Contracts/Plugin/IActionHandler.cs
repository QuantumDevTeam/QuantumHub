namespace QuantumHub.Contracts.Plugin;

public interface IActionHandler
{
    Task<TResponse> HandleActionAsync<TRequest, TResponse>(string action, TRequest request,
        CancellationToken token = default)
        where TRequest : class
        where TResponse : class;
}