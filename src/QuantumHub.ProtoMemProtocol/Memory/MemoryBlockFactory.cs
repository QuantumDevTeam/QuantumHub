using QuantumHub.Contracts.Ipc;

namespace QuantumHub.ProtoMemProtocol.Memory;

public class MemoryBlockFactory
    : IMemoryBlockFactory
{
    public IMemoryBlock Create(string name, int capacity, bool isOwner)
    {
        var block = new MemoryBlock(name, capacity, isOwner);
        block.Initialize();
        return block;
    }
}