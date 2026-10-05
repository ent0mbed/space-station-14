using Robust.Shared.GameStates;
using Robust.Shared.Map.Components;

namespace Content.Replay.Diagnostic;

public sealed partial class CaptureRunner
{
    private const int MaxParentDepth = 128;
    private readonly HashSet<EntityUid> _nativeMoved = new();
    private readonly HashSet<int> _nativeDeleted = new();
    private readonly HashSet<int> _frameDeleted = new();
    private readonly HashSet<EntityUid> _activeParents = new();
    private readonly HashSet<int> _projectedThisFrame = new();
    private readonly Dictionary<int, int?> _projectedParents = new();
    private readonly Dictionary<int, HashSet<int>> _projectedChildren = new();
    private readonly List<object> _firstTransitionAncestors = new();
    private int _ancestorCandidates;
    private int _ancestorAdditions;
    private int _clientAncestors;
    private int _movedCandidates;
    private int _observedDeletions;
    private int _maxParentDepth;
    private int _captureSequence;

    private void OnNativeMove(ref MoveEvent ev) => _nativeMoved.Add(ev.Sender);
    private void OnNativeDelete(Entity<MetaDataComponent> entity) => _nativeDeleted.Add(entity.Comp.NetEntity.Id);

    private void BeginProjection(GameState state, List<int> deletes, List<object> audioEvents)
    {
        _activeParents.Clear();
        _projectedThisFrame.Clear();
        _frameDeleted.Clear();
        foreach (var deletion in state.EntityDeletions.Value)
            _frameDeleted.Add(deletion.Id);
        _frameDeleted.UnionWith(_nativeDeleted);
        _observedDeletions += _nativeDeleted.Count;
        foreach (var id in _frameDeleted.Order())
            RemoveProjection(id, deletes, audioEvents);
        _nativeDeleted.Clear();
    }

    private void ProjectNative(EntityUid uid, List<object> upserts, List<object> audioEvents,
        bool initial, bool ancestor = false, int depth = 0)
    {
        if (depth > MaxParentDepth)
            throw new InvalidDataException("Native parent chain exceeds diagnostic depth budget.");
        _maxParentDepth = Math.Max(_maxParentDepth, depth);
        if (!_entities.TryGetComponent<TransformComponent>(uid, out var transform)
            || !_entities.TryGetComponent<MetaDataComponent>(uid, out var metadata)
            || metadata.EntityLifeStage < EntityLifeStage.Initialized
            || metadata.EntityLifeStage >= EntityLifeStage.Terminating)
            throw new InvalidDataException($"Required native graph entity {uid.Id} is unavailable or uninitialized.");
        var id = metadata.NetEntity.Id;
        if (!metadata.NetEntity.Valid || _frameDeleted.Contains(id)
            || !_entities.TryGetEntity(metadata.NetEntity, out var mapped) || mapped != uid)
            throw new InvalidDataException($"Required native graph identity {id} is invalid or deleted.");
        if (_projectedThisFrame.Contains(id))
            return;
        if (!_activeParents.Add(uid))
            throw new InvalidDataException($"Native parent cycle at entity {id}.");
        if (transform.ParentUid != EntityUid.Invalid)
            ProjectNative(transform.ParentUid, upserts, audioEvents, initial, true, depth + 1);
        if (ancestor)
        {
            _ancestorCandidates++;
            if (!_fingerprints.ContainsKey(id))
            {
                _ancestorAdditions++;
                if (metadata.NetEntity.IsClientSide()) _clientAncestors++;
                if (_captureSequence == 1 && _firstTransitionAncestors.Count < 64)
                    _firstTransitionAncestors.Add(new { id,
                        parentId = transform.ParentUid == EntityUid.Invalid ? (int?) null
                            : _entities.GetNetEntity(transform.ParentUid).Id,
                        prototype = metadata.EntityPrototype?.ID, clientSide = metadata.NetEntity.IsClientSide(),
                        lifeStage = metadata.EntityLifeStage.ToString(),
                        isMap = _entities.HasComponent<MapComponent>(uid), isGrid = _entities.HasComponent<MapGridComponent>(uid),
                        isChunkEntity = _entities.HasComponent<ChunkEntityComponent>(uid),
                        transform = new { x = transform.LocalPosition.X, y = transform.LocalPosition.Y,
                            rotation = transform.LocalRotation.Theta } });
            }
        }
        Capture(uid, transform, metadata, upserts, audioEvents, initial);
        TrackParent(id, transform.ParentUid == EntityUid.Invalid ? null : _entities.GetNetEntity(transform.ParentUid).Id);
        _activeParents.Remove(uid);
        _projectedThisFrame.Add(id);
    }

    private void ProjectMoved(List<object> upserts, List<object> audioEvents, bool initial)
    {
        // Native reparenting and recursive deletion can affect actors absent from EntityStates.
        foreach (var uid in _nativeMoved.OrderBy(uid => _entities.GetNetEntity(uid).Id))
        {
            if (!_entities.TryGetComponent<MetaDataComponent>(uid, out var metadata)
                || metadata.EntityLifeStage >= EntityLifeStage.Terminating || _frameDeleted.Contains(metadata.NetEntity.Id))
                continue;
            if (metadata.NetEntity.IsClientSide() && !_fingerprints.ContainsKey(metadata.NetEntity.Id))
                continue;
            _movedCandidates++;
            ProjectNative(uid, upserts, audioEvents, initial);
        }
        _nativeMoved.Clear();
        foreach (var id in _frameDeleted)
            if (_projectedChildren.TryGetValue(id, out var children) && children.Count != 0)
                throw new InvalidDataException($"Deleted native parent {id} retains projected children.");
    }

    private void TrackParent(int id, int? parent)
    {
        if (_projectedParents.TryGetValue(id, out var previous) && previous == parent)
            return;
        if (previous is { } old && _projectedChildren.TryGetValue(old, out var oldChildren))
        {
            oldChildren.Remove(id);
            if (oldChildren.Count == 0) _projectedChildren.Remove(old);
        }
        _projectedParents[id] = parent;
        if (parent is not { } current)
            return;
        if (!_projectedChildren.TryGetValue(current, out var children))
            _projectedChildren[current] = children = new();
        children.Add(id);
    }

    private void RemoveProjection(int id, List<int> deletes, List<object> audioEvents)
    {
        if (_fingerprints.Remove(id)) deletes.Add(id);
        _previousSprites.Remove(id);
        if (_projectedParents.Remove(id, out var parent) && parent is { } old
            && _projectedChildren.TryGetValue(old, out var children))
        {
            children.Remove(id);
            if (children.Count == 0) _projectedChildren.Remove(old);
        }
        if (_audio.Remove(id))
        {
            _audioRemovals++;
            audioEvents.Add(new { kind = "remove", id });
        }
    }
}
