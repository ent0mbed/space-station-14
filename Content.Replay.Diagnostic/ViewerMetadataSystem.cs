namespace Content.Replay.Diagnostic;

// Entity-system initialization is the engine's permitted event-registration phase.
// The export loop cannot register new subscriptions after native replay startup.
public sealed class ViewerMetadataSystem : EntitySystem
{
    private readonly HashSet<EntityUid> _renamed = new();
    internal IEnumerable<EntityUid> Renamed => _renamed;
    internal void ClearRenamed() => _renamed.Clear();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<EntityRenamedEvent>(OnRenamed);
    }

    private void OnRenamed(ref EntityRenamedEvent ev)
    {
        if (!_renamed.Contains(ev.Uid) && _renamed.Count >= Program.MaxPresentationOwners)
            throw new InvalidDataException("Native rename observer membership budget exceeded.");
        _renamed.Add(ev.Uid);
    }
}
