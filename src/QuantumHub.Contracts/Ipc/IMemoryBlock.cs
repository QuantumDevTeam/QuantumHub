namespace QuantumHub.Contracts.Ipc;

public interface IMemoryBlock : IDisposable
{
    string Name { get; }
    int Capacity { get; }
    void Write(ReadOnlySpan<byte> data);
    int Read(Span<byte> buffer);
    Task WaitForDataAsync(CancellationToken token = default);
    void SignalDataWritten();
}