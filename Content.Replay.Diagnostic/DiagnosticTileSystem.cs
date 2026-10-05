namespace Content.Replay.Diagnostic;

// The native event bus freezes subscriptions before entities initialize. Subscribe here,
// then attach the diagnostic listener only after the replay's initial world is ready.
public sealed class DiagnosticTileSystem : EntitySystem
{
    public event EntityEventRefHandler<TileChangedEvent>? TileChanged;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<TileChangedEvent>(OnTileChanged);
    }

    private void OnTileChanged(ref TileChangedEvent ev) => TileChanged?.Invoke(ref ev);
}
