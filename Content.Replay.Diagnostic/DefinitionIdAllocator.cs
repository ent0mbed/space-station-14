namespace Content.Replay.Diagnostic;

/// <summary>One definition kind's positive IDs, independent of resident definition count.</summary>
internal sealed class DefinitionIdAllocator
{
    private int _lastAllocatedId;

    public DefinitionIdAllocator(int lastAllocatedId = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastAllocatedId);
        _lastAllocatedId = lastAllocatedId;
    }

    public int Allocate()
    {
        _lastAllocatedId = checked(_lastAllocatedId + 1);
        return _lastAllocatedId;
    }
}
