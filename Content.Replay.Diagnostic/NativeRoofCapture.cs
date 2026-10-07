using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Maps;
using Robust.Client.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.GameStates;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.Replay.Diagnostic;

// One initial membership enumeration. Thereafter native events select members,
// small retained scalar stamps detect local writes, and only affected grids are
// resolved. A grid's world pose is deliberately not part of its local coverage.
internal sealed class NativeRoofCapture : IDisposable
{
    private const int MaxLinks = 500_000;
    private const int MaxDepth = 128;
    private readonly IClientEntityManager _entities;
    private readonly SharedMapSystem _maps;
    private readonly SharedRoofSystem _roof;
    private readonly TurfSystem _turf;
    private readonly SharedTransformSystem _transforms;
    private readonly DiagnosticRoofSystem _observer;
    private readonly DiagnosticTileSystem _tiles;
    private readonly RoofObservationInventory _inventory = new();
    private readonly HashSet<EntityUid> _grids = [];
    private readonly Dictionary<EntityUid, int> _gridIds = new();
    private readonly Dictionary<EntityUid, GridStamp> _gridStamps = new();
    private readonly Dictionary<EntityUid, Contributor> _contributors = new();
    private readonly Dictionary<EntityUid, HashSet<EntityUid>> _dependencies = new();
    private readonly HashSet<EntityUid> _changedContributors = [];
    private readonly HashSet<EntityUid> _dirtyGrids = [];
    private int _links;
    private bool _initial = true;
    private bool _disposed;
    private long _nativeTilesInspected;
    private long _resolvedGrids;
    private long _gridChanges;
    private long _chunkChanges;
    private long _chunkDeletes;
    private long _gridDeletes;
    private int _initialGrids;
    private int _initialChunks;
    private int _initialTiles;

    private readonly record struct GridStamp(bool Implicit, LightingColor ImplicitColor, bool Explicit,
        LightingColor ExplicitColor, uint RoofTick, uint TileTick, int TileSize);
    private readonly record struct ContributorStamp(bool Enabled, LightingColor? Color, EntityUid? Grid,
        uint FixtureTick, int Fixtures, uint PhysicsTick, bool Anchored);
    private sealed record Contributor(ContributorStamp Stamp, EntityUid[] Ancestors);

    public NativeRoofCapture(IClientEntityManager entities)
    {
        _entities = entities;
        _maps = entities.System<SharedMapSystem>();
        _roof = entities.System<SharedRoofSystem>();
        _turf = entities.System<TurfSystem>();
        _transforms = entities.System<SharedTransformSystem>();
        _observer = entities.System<DiagnosticRoofSystem>();
        _tiles = entities.System<DiagnosticTileSystem>();
        _observer.Clear();
        _tiles.TileChanged += OnTileChanged;
        _transforms.OnGlobalMoveEvent += OnMove;
    }

    private void OnTileChanged(ref TileChangedEvent args) => Dirty(args.Entity.Owner);
    private void OnMove(ref MoveEvent args)
    {
        // Moving the grid moves both roof tiles and its contributors together.
        // Existing scene transforms carry that change; local coverage is unchanged.
        if (_grids.Contains(args.Sender)) return;
        QueueDependents(args.Sender);
    }
    private void QueueDependents(EntityUid uid)
    {
        if (_contributors.ContainsKey(uid)) _changedContributors.Add(uid);
        if (_dependencies.TryGetValue(uid, out var members)) _changedContributors.UnionWith(members);
    }
    private void Dirty(EntityUid grid)
    {
        if (!_dirtyGrids.Contains(grid) && _dirtyGrids.Count >= RoofObservationPolicy.MaxGrids)
            throw new InvalidDataException("Native affected roof-grid budget exceeded.");
        _dirtyGrids.Add(grid);
    }
    private bool Live(EntityUid uid) => _entities.TryGetComponent<MetaDataComponent>(uid, out var metadata)
        && metadata.EntityLifeStage >= EntityLifeStage.Initialized && metadata.EntityLifeStage < EntityLifeStage.Terminating;

    public RoofChanges Capture(GameState state, Action<EntityUid> ensureOwner)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NativeRoofCapture));
        _inventory.Begin();
        if (_initial)
        {
            var grids = _entities.AllEntityQueryEnumerator<MapGridComponent>();
            while (grids.MoveNext(out var uid, out _)) if (Live(uid)) AddGrid(uid);
            var contributors = _entities.AllEntityQueryEnumerator<IsRoofComponent>();
            while (contributors.MoveNext(out var uid, out _)) if (Live(uid)) _changedContributors.Add(uid);
        }
        _observer.ObserveReplayState(state);
        foreach (var uid in _observer.Changed)
        {
            if (Live(uid) && _entities.HasComponent<MapGridComponent>(uid)) AddGrid(uid);
            if (_observer.GridChanged.Contains(uid) || _grids.Contains(uid) && !Live(uid)) Dirty(uid);
            QueueDependents(uid);
            if (Live(uid) && _entities.HasComponent<IsRoofComponent>(uid)) _changedContributors.Add(uid);
        }
        if (_observer.TileDefinitionsChanged) foreach (var uid in _grids) Dirty(uid);

        // These bounded inventories contain roof/grid members, never all entities
        // or all tiles. Sample final scalar values after native FrameUpdate,
        // including paused members; replay component candidates are queued above.
        foreach (var (uid, contributor) in _contributors)
            if (!TryContributor(uid, out var stamp) || stamp != contributor.Stamp) _changedContributors.Add(uid);
        foreach (var uid in _changedContributors) RefreshContributor(uid);
        _changedContributors.Clear();
        foreach (var uid in _grids)
        {
            if (!Live(uid) || !_entities.TryGetComponent<MapGridComponent>(uid, out var grid)) { Dirty(uid); continue; }
            var implicitPresent = _entities.TryGetComponent<ImplicitRoofComponent>(uid, out var implicitRoof);
            var explicitPresent = _entities.TryGetComponent<RoofComponent>(uid, out var explicitRoof);
            var stamp = new GridStamp(implicitPresent, implicitPresent ? Color(implicitRoof!.Color) : default,
                explicitPresent, explicitPresent ? Color(explicitRoof!.Color) : default,
                explicitPresent ? explicitRoof!.LastModifiedTick.Value : 0, grid.LastTileModifiedTick.Value, grid.TileSize);
            if (!_gridStamps.TryGetValue(uid, out var old) || old != stamp) { Dirty(uid); _gridStamps[uid] = stamp; }
        }
        ChargeInvalidation();

        foreach (var uid in _dirtyGrids.OrderBy(uid => uid.Id))
        {
            if (!Live(uid) || !_entities.TryGetComponent<MapGridComponent>(uid, out var grid))
            {
                RemoveGrid(uid);
                if (Live(uid)) ensureOwner(uid); // Preserve removal of the scene isGrid flag.
                continue;
            }
            var implicitPresent = _entities.TryGetComponent<ImplicitRoofComponent>(uid, out var implicitRoof);
            var explicitPresent = _entities.TryGetComponent<RoofComponent>(uid, out var explicitRoof);
            if (!implicitPresent && !explicitPresent)
            {
                if (_gridIds.Remove(uid, out var oldId)) _inventory.DeleteGrid(oldId);
                continue;
            }
            ensureOwner(uid);
            var metadata = _entities.GetComponent<MetaDataComponent>(uid);
            if (!metadata.NetEntity.Valid) throw new InvalidDataException("Native roof grid has invalid network identity.");
            var id = metadata.NetEntity.Id;
            if (_gridIds.TryGetValue(uid, out var previousId) && previousId != id) _inventory.DeleteGrid(previousId);
            _gridIds[uid] = id;
            ChargeInvalidation();
            _inventory.ReplaceGrid(id, grid.TileSize, ResolveTiles(uid, grid, implicitRoof, explicitRoof), implicitPresent);
            _resolvedGrids++;
        }
        _dirtyGrids.Clear();
        _observer.Clear();
        var changes = _inventory.Finish();
        _gridChanges += changes.GridReplacements.Count; _gridDeletes += changes.GridDeletes.Count;
        _chunkChanges += changes.ChunkReplacements.Count; _chunkDeletes += changes.ChunkDeletes.Count;
        if (_initial)
        {
            _initialGrids = _inventory.GridCount; _initialChunks = _inventory.ChunkCount; _initialTiles = _inventory.TileCount;
            _initial = false;
        }
        return changes;
    }

    private void ChargeInvalidation() => _inventory.SetOutsideBytes(
        _links * 192L + _contributors.Count * 256L + _grids.Count * 384L
        + (_observer.Changed.Count + _observer.GridChanged.Count + _dirtyGrids.Count + _changedContributors.Count) * 64L);

    private void AddGrid(EntityUid uid)
    {
        if (_grids.Contains(uid)) return;
        if (_grids.Count >= RoofObservationPolicy.MaxGrids) throw new InvalidDataException("Native roof-grid membership budget exceeded.");
        _grids.Add(uid); Dirty(uid);
    }
    private void RemoveGrid(EntityUid uid)
    {
        _grids.Remove(uid); _gridStamps.Remove(uid);
        if (_gridIds.Remove(uid, out var id)) _inventory.DeleteGrid(id);
    }
    private bool TryContributor(EntityUid uid, out ContributorStamp stamp)
    {
        stamp = default;
        if (!Live(uid) || !_entities.TryGetComponent<IsRoofComponent>(uid, out var roof)) return false;
        if (!_entities.TryGetComponent<TransformComponent>(uid, out var transform))
            throw new InvalidDataException("Native roof contributor is missing its transform.");
        _entities.TryGetComponent<FixturesComponent>(uid, out var fixtures);
        _entities.TryGetComponent<PhysicsComponent>(uid, out var physics);
        var grid = transform.GridUid ?? transform.MapUid;
        if (grid is { } candidate && !_entities.HasComponent<MapGridComponent>(candidate)) grid = null;
        stamp = new(roof.Enabled, roof.Color is { } color ? Color(color) : null, grid,
            fixtures?.LastModifiedTick.Value ?? 0, fixtures?.FixtureCount ?? 0, physics?.LastModifiedTick.Value ?? 0, transform.Anchored);
        return true;
    }
    private void RefreshContributor(EntityUid uid)
    {
        if (_contributors.Remove(uid, out var previous))
        {
            if (previous.Stamp.Grid is { } oldGrid) Dirty(oldGrid);
            foreach (var ancestor in previous.Ancestors)
            {
                var members = _dependencies[ancestor]; members.Remove(uid); _links--;
                if (members.Count == 0) _dependencies.Remove(ancestor);
            }
        }
        if (!TryContributor(uid, out var stamp)) return;
        if (_contributors.Count >= Program.MaxPresentationOwners)
            throw new InvalidDataException("Native roof-contributor membership budget exceeded.");
        var ancestors = new List<EntityUid>();
        var current = uid;
        while (current != EntityUid.Invalid && !_entities.HasComponent<MapGridComponent>(current))
        {
            if (ancestors.Count >= MaxDepth || ancestors.Contains(current)
                || !_entities.TryGetComponent<TransformComponent>(current, out var transform))
                throw new InvalidDataException("Incomplete/cyclic native roof-contributor ancestry.");
            if (_links >= MaxLinks) throw new InvalidDataException("Native roof invalidation ancestry budget exceeded.");
            if (!_dependencies.TryGetValue(current, out var members)) _dependencies[current] = members = [];
            if (members.Add(uid)) _links++;
            ancestors.Add(current); current = transform.ParentUid;
        }
        _contributors[uid] = new(stamp, ancestors.ToArray());
        if (stamp.Grid is { } grid) Dirty(grid);
    }

    private IEnumerable<RoofSample> ResolveTiles(EntityUid uid, MapGridComponent grid,
        ImplicitRoofComponent? implicitRoof, RoofComponent? explicitRoof)
    {
        var tiles = _maps.GetAllTiles(uid, grid); // Same native nonempty tile domain as RoofOverlay.
        var inspected = 0;
        while (tiles.MoveNext(out var nextTile))
        {
            if (nextTile is not { } tile) throw new InvalidDataException("Native roof tile enumerator returned no tile.");
            if (++inspected > RoofObservationPolicy.MaxTiles)
                throw new InvalidDataException("Native roof tile-inspection budget exceeded.");
            _nativeTilesInspected++;
            // Pinned TurfSystem checks ContentTileDefinition.MapAtmosphere, not
            // tile presence/ID zero or a synthesized roof from floor geometry.
            if (_turf.IsSpace(tile)) continue;
            var color = implicitRoof?.Color ?? _roof.GetColor((uid, grid, explicitRoof!), tile.GridIndices);
            if (color is { } value) yield return new(new(tile.GridIndices.X, tile.GridIndices.Y), Color(value));
        }
    }

    public IEnumerable<RoofGridObservation> Grids => _inventory.Grids;
    public object Summary() => new { complete = true, initialGrids = _initialGrids, initialChunks = _initialChunks,
        initialTiles = _initialTiles, gridsAtEnd = _inventory.GridCount, chunksAtEnd = _inventory.ChunkCount,
        implicitGridsAtEnd = _inventory.Grids.Count(grid => grid.Implicit),
        explicitGridsAtEnd = _inventory.Grids.Count(grid => !grid.Implicit),
        tilesAtEnd = _inventory.TileCount, gridReplacements = _gridChanges, gridDeletes = _gridDeletes,
        chunkReplacements = _chunkChanges, chunkDeletes = _chunkDeletes, baselineMembershipScans = 1,
        resolvedGridPasses = _resolvedGrids, nativeTilesInspected = _nativeTilesInspected,
        contributorCount = _contributors.Count, ancestryLinks = _links, retainedBytes = _inventory.RetainedBytes,
        peakLiveStagedBytes = _inventory.PeakLiveStagedBytes,
        limits = new { grids = RoofObservationPolicy.MaxGrids, chunks = RoofObservationPolicy.MaxChunks,
            tiles = RoofObservationPolicy.MaxTiles, retainedBytes = RoofObservationPolicy.MaxBytes, chunkSize = RoofObservationPolicy.ChunkSize },
        semantics = "Pinned RoofOverlay: implicit precedence, native Turf.IsSpace/MapAtmosphere exclusion, native SharedRoof.GetColor explicit/contributor resolution; sRGB grid-local tile coverage.",
        accounting = "Owned JSON values plus grid/chunk/tile indexes, live pending changes and affected-grid resolution scratch; retained contributor/ancestry indexes also charged. Not a process-wide allocation cap." };
    private static LightingColor Color(Robust.Shared.Maths.Color color) => new(color.R, color.G, color.B, color.A);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tiles.TileChanged -= OnTileChanged;
        _transforms.OnGlobalMoveEvent -= OnMove;
        _observer.Clear();
        _grids.Clear(); _gridIds.Clear(); _gridStamps.Clear(); _contributors.Clear(); _dependencies.Clear();
        _changedContributors.Clear(); _dirtyGrids.Clear();
        _inventory.Reset();
    }
}
