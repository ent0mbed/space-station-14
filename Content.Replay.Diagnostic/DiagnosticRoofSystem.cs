using Content.Shared.Light.Components;
using Content.Shared.Maps;
using Robust.Shared.Containers;
using Robust.Shared.GameStates;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Prototypes;

namespace Content.Replay.Diagnostic;

// Register broadcast/lifecycle observers before the native event bus freezes.
// ComponentHandleState permits only the native handler per component, so recorded
// component candidates are queued separately after native FrameUpdate.
public sealed class DiagnosticRoofSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IComponentFactory _factory = default!;
    private readonly HashSet<ushort> _stateComponents = [];
    private readonly HashSet<ushort> _gridComponents = [];
    internal readonly HashSet<EntityUid> Changed = [];
    internal readonly HashSet<EntityUid> GridChanged = [];
    internal bool TileDefinitionsChanged;

    public override void Initialize()
    {
        base.Initialize();
        ObserveStateType<MapGridComponent>(true);
        ObserveStateType<RoofComponent>(true);
        ObserveStateType<ImplicitRoofComponent>(true);
        ObserveStateType<IsRoofComponent>();
        ObserveStateType<FixturesComponent>();
        ObserveStateType<PhysicsComponent>();
        ObserveStateType<ContainerManagerComponent>();
        SubscribeLocalEvent<PhysicsBodyTypeChangedEvent>(OnBodyType);
        SubscribeLocalEvent<CollisionChangeEvent>(OnCollision);
        SubscribeLocalEvent<EntInsertedIntoContainerMessage>(OnInserted);
        SubscribeLocalEvent<EntRemovedFromContainerMessage>(OnRemoved);
        EntityManager.ComponentAdded += OnAdded;
        EntityManager.ComponentRemoved += OnComponentRemoved;
        EntityManager.EntityDeleted += OnDeleted;
        _prototypes.PrototypesReloaded += OnPrototypes;
    }

    private void ObserveStateType<T>(bool grid = false) where T : Component
    {
        var id = _factory.GetRegistration(typeof(T)).NetID
            ?? throw new InvalidDataException("Native roof invalidation component has no network identity.");
        _stateComponents.Add(id);
        if (grid) _gridComponents.Add(id);
    }
    internal void ObserveReplayState(GameState state)
    {
        foreach (var entity in state.EntityStates.Value)
            foreach (var change in entity.ComponentChanges.Value)
            {
                if (!_stateComponents.Contains(change.NetID)
                    || !EntityManager.TryGetEntity(entity.NetEntity, out var uid) || uid is not { } owner) continue;
                Mark(owner);
                if (_gridComponents.Contains(change.NetID)) GridChanged.Add(owner);
            }
    }
    private void OnBodyType(ref PhysicsBodyTypeChangedEvent args) => Mark(args.Entity);
    private void OnCollision(ref CollisionChangeEvent args) => Mark(args.BodyUid);
    private void OnInserted(EntInsertedIntoContainerMessage args) { Mark(args.Entity); Mark(args.Container.Owner); }
    private void OnRemoved(EntRemovedFromContainerMessage args) { Mark(args.Entity); Mark(args.Container.Owner); }
    private void OnDeleted(Entity<MetaDataComponent> entity) => Mark(entity.Owner);
    private void OnAdded(AddedComponentEventArgs args) => ComponentChanged(args.BaseArgs);
    private void OnComponentRemoved(RemovedComponentEventArgs args) => ComponentChanged(args.BaseArgs);
    private void ComponentChanged(ComponentEventArgs args)
    {
        if (args.Component is MapGridComponent or RoofComponent or ImplicitRoofComponent or IsRoofComponent
            or FixturesComponent or PhysicsComponent or ContainerManagerComponent)
            Mark(args.Owner);
        if (args.Component is MapGridComponent or RoofComponent or ImplicitRoofComponent) GridChanged.Add(args.Owner);
    }
    private void OnPrototypes(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<ContentTileDefinition>()) TileDefinitionsChanged = true;
    }
    private void Mark(EntityUid uid)
    {
        if (!Changed.Contains(uid) && Changed.Count >= Program.MaxPresentationOwners)
            throw new InvalidDataException("Native roof invalidation identity budget exceeded.");
        Changed.Add(uid);
    }
    internal void Clear() { Changed.Clear(); GridChanged.Clear(); TileDefinitionsChanged = false; }

    public override void Shutdown()
    {
        EntityManager.ComponentAdded -= OnAdded;
        EntityManager.ComponentRemoved -= OnComponentRemoved;
        EntityManager.EntityDeleted -= OnDeleted;
        _prototypes.PrototypesReloaded -= OnPrototypes;
        Clear();
        base.Shutdown();
    }
}
