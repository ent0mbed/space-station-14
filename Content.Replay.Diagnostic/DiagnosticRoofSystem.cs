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

// Register before the native event bus freezes. Queue identities only: handled
// states are sampled after native FrameUpdate, never during an auto-state write.
public sealed class DiagnosticRoofSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    internal readonly HashSet<EntityUid> Changed = [];
    internal readonly HashSet<EntityUid> GridChanged = [];
    internal bool TileDefinitionsChanged;

    public override void Initialize()
    {
        base.Initialize();
        ObserveState<MapGridComponent>();
        ObserveState<RoofComponent>();
        ObserveState<ImplicitRoofComponent>();
        ObserveState<IsRoofComponent>();
        ObserveState<FixturesComponent>();
        ObserveState<PhysicsComponent>();
        ObserveState<ContainerManagerComponent>();
        SubscribeLocalEvent<TransformComponent, PhysicsBodyTypeChangedEvent>(OnBodyType);
        SubscribeLocalEvent<CollisionChangeEvent>(OnCollision);
        SubscribeLocalEvent<TransformComponent, EntGotInsertedIntoContainerMessage>(OnInserted);
        SubscribeLocalEvent<TransformComponent, EntGotRemovedFromContainerMessage>(OnRemoved);
        EntityManager.ComponentAdded += OnAdded;
        EntityManager.ComponentRemoved += OnComponentRemoved;
        EntityManager.EntityDeleted += OnDeleted;
        _prototypes.PrototypesReloaded += OnPrototypes;
    }

    private void ObserveState<T>() where T : Component
        => SubscribeLocalEvent<T, ComponentHandleState>(OnState);
    private void OnState<T>(EntityUid uid, T component, ref ComponentHandleState args) where T : Component
    {
        Mark(uid);
        if (component is MapGridComponent or RoofComponent or ImplicitRoofComponent) GridChanged.Add(uid);
    }
    private void OnBodyType(EntityUid uid, TransformComponent component, ref PhysicsBodyTypeChangedEvent args) => Mark(uid);
    private void OnCollision(ref CollisionChangeEvent args) => Mark(args.BodyUid);
    private void OnInserted(EntityUid uid, TransformComponent component, EntGotInsertedIntoContainerMessage args) => Mark(uid);
    private void OnRemoved(EntityUid uid, TransformComponent component, EntGotRemovedFromContainerMessage args) => Mark(uid);
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
