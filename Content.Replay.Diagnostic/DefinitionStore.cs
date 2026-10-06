namespace Content.Replay.Diagnostic;

/// <summary>Owned definition values keyed by ID, with creation-ordered pending emission.</summary>
internal sealed class DefinitionStore<T>(DefinitionIdAllocator ids) where T : class
{
    private readonly Dictionary<int, T> _definitions = new();
    private readonly Queue<int> _pending = new();

    public int Count => _definitions.Count;

    public T Get(int id) => _definitions[id];

    // Static factories receive explicit state so cache-hit callers need no captured-variable frame.
    public int Add<TState>(TState state, Func<int, TState, T> create)
    {
        var id = ids.Allocate();
        var value = create(id, state);
        _definitions.Add(id, value);
        _pending.Enqueue(id);
        return id;
    }

    public void WritePending(Action<T> write)
    {
        while (_pending.TryPeek(out var id))
        {
            write(_definitions[id]);
            _pending.Dequeue();
        }
    }
}
