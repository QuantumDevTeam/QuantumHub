namespace QuantumHub.Contracts.Ipc;

public interface IMemoryBlockFactory
{
    IMemoryBlock Create(string name, int capacity, bool isOwner);
}