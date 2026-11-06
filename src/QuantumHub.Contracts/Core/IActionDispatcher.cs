namespace QuantumHub.Contracts.Core;

public interface IActionDispatcher
{
    Task<TResponse> ExecuteActionAsync<TRequest, TResponse>(string targetPluginId, string action, TRequest request,
        CancellationToken token = default)
        where TRequest : class
        where TResponse : class;
}