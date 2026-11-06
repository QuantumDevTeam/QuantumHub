using System.IO.MemoryMappedFiles;
using QuantumHub.Contracts.Ipc;

namespace QuantumHub.ProtoMemProtocol.Memory;

public class MemoryBlock(string name, int capacity, bool isOwner) 
    : IMemoryBlock
{
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private EventWaitHandle? _dataReadyEvent;
    
    public string Name => name;
    public int Capacity => capacity;

    public void Initialize()
    {
        if (isOwner)
        {
            _mmf = MemoryMappedFile.CreateNew(name, 4 + capacity);
            _dataReadyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name + "_Event");
        }
        else
        {
            _mmf = MemoryMappedFile.OpenExisting(name);
            _dataReadyEvent = EventWaitHandle.OpenExisting(name + "_Event");
        }
        _accessor = _mmf.CreateViewAccessor(0, 4 + capacity);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length > capacity) throw new ArgumentException("Data exceeds capacity");
        _accessor!.Write(0, data.Length);
        _accessor.WriteArray(4, data.ToArray(), 0, data.Length);
    }

    public int Read(Span<byte> buffer)
    {
        var length = _accessor!.ReadInt32(0);
        if (length > buffer.Length) throw new ArgumentException("Buffer too small");
        var temp = new byte[length];
        _accessor.ReadArray(4, temp, 0, length);
        temp.CopyTo(buffer);
        return length;
    }

    public Task WaitForDataAsync(CancellationToken token = default)
    {
        return Task.Run(() => _dataReadyEvent!.WaitOne(), token);
    }

    public void SignalDataWritten()
    {
        _dataReadyEvent!.Set();
    }

    public void Dispose()
    {
        _accessor?.Dispose();
        if (!isOwner) return;
        _mmf?.Dispose();
        _dataReadyEvent?.Dispose();
    }
}