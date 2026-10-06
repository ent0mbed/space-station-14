using System.Text.Json;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;

namespace Content.Replay.Diagnostic;

public sealed partial class CaptureRunner
{
    // Includes phase-free and sprite-free exported owners, so client-only transitions
    // into animation and component add/remove do not require a network/move candidate.
    private readonly Dictionary<int, EntityUid> _presentationOwners = new();
    private readonly List<(int Id, EntityUid Uid)> _appearanceCandidates = new();
    private readonly LayerPhaseValue[] _phaseScratch = new LayerPhaseValue[Program.MaxPhaseLayersPerOwner];
    private readonly Dictionary<int, RetainedPhase> _phaseStates = new();
    private readonly List<object> _phaseProbe = new();
    private long _phaseRetainedBytes;
    private long _phaseRetainedPeakBytes;
    private long _phaseReplacements;
    private long _phaseLayerSamples;
    private long _phaseScalarInspections;
    private long _phaseForceUpdates;
    private long _independentAppearanceCandidates;
    private long _maximumFramePhaseJSONBytes;

    private void TrackPresentationOwner(int id, EntityUid uid)
    {
        if (!_presentationOwners.ContainsKey(id) && _presentationOwners.Count >= Program.MaxPhaseOwners)
            throw new InvalidDataException("Presentation owner budget exceeded.");
        _presentationOwners[id] = uid;
    }

    private static bool TryPhaseState(SpriteComponent component, SpriteComponent.Layer layer, out RSI.State state)
    {
        state = default!;
        var rsi = layer.RSI ?? component.BaseRSI;
        if (!layer.State.IsValid || rsi == null || !rsi.TryGetState(layer.State, out var resolved)
            || resolved.DelayCount <= 1) return false;
        state = resolved;
        return true;
    }

    private void QueueOrdinarySprite(EntityUid uid, SpriteComponent component, SpriteSystem system)
    {
        if (_entities.HasComponent<SyncSpriteComponent>(uid)) return;
        var layers = (IReadOnlyList<SpriteComponent.Layer>) component.AllLayers;
        if (layers.Count > Program.MaxPhaseLayersPerOwner)
            throw new InvalidDataException("Diagnostic sprite layer budget exceeded.");
        // Do not inspect cached IsInert. Its queued recomputation happens inside FrameUpdate.
        foreach (var layer in layers)
            if (layer.Visible && layer.AutoAnimated && TryPhaseState(component, layer, out _))
            {
                system.ForceUpdate(uid);
                _phaseForceUpdates++;
                return;
            }
    }

    private void QueueOrdinaryPhases(Robust.Shared.GameStates.GameState state, bool initial)
    {
        var system = _entities.System<SpriteSystem>();
        if (initial)
        {
            var query = _entities.EntityQueryEnumerator<SpriteComponent, MetaDataComponent>();
            while (query.MoveNext(out var uid, out var sprite, out var metadata))
                if (!metadata.NetEntity.IsClientSide() && metadata.EntityLifeStage >= EntityLifeStage.Initialized
                    && metadata.EntityLifeStage < EntityLifeStage.Terminating)
                    QueueOrdinarySprite(uid, sprite, system);
            return;
        }
        foreach (var uid in _presentationOwners.Values)
            if (_entities.TryGetComponent<SpriteComponent>(uid, out var sprite)
                && _entities.TryGetComponent<MetaDataComponent>(uid, out var metadata)
                && metadata.EntityLifeStage >= EntityLifeStage.Initialized
                && metadata.EntityLifeStage < EntityLifeStage.Terminating)
                QueueOrdinarySprite(uid, sprite, system);
        // New exported network owners need their first native tick, too.
        foreach (var entity in state.EntityStates.Value)
            if (!_presentationOwners.ContainsKey(entity.NetEntity.Id)
                && _entities.TryGetEntity(entity.NetEntity, out var uid)
                && _entities.TryGetComponent<SpriteComponent>(uid, out var sprite))
                QueueOrdinarySprite(uid.Value, sprite, system);
    }

    private void ProjectIndependentAppearance(List<object> upserts, List<object> audioEvents)
    {
        _appearanceCandidates.Clear();
        foreach (var (id, uid) in _presentationOwners)
        {
            _phaseScalarInspections++;
            if (!_entities.TryGetComponent<MetaDataComponent>(uid, out var metadata)
                || metadata.EntityLifeStage >= EntityLifeStage.Terminating)
                throw new InvalidDataException($"Retained presentation owner {id} disappeared without observed deletion.");
            var hasSprite = _entities.TryGetComponent<SpriteComponent>(uid, out var component);
            if (hasSprite ? !NativeAppearanceMatches(uid, id, component!) : _previousSprites.ContainsKey(id))
                _appearanceCandidates.Add((id, uid));
        }
        // The candidate list is reusable; stable owners never run full CaptureSprite/material/bounds work.
        _appearanceCandidates.Sort((left, right) => left.Id.CompareTo(right.Id));
        foreach (var (_, uid) in _appearanceCandidates)
        {
            _independentAppearanceCandidates++;
            ProjectNative(uid, upserts, audioEvents, false);
        }
    }

    private List<SpritePhaseReplacement> CaptureLayerPhases(long sampleReplayTime100ns, int sequence)
    {
        var replacements = new List<SpritePhaseReplacement>();
        long frameBytes = 0;
        foreach (var (id, uid) in _presentationOwners)
        {
            if (!_previousSprites.TryGetValue(id, out var spriteId)
                || !_entities.TryGetComponent<SpriteComponent>(uid, out var component)) continue;
            var layers = (IReadOnlyList<SpriteComponent.Layer>) component.AllLayers;
            if (layers.Count > _phaseScratch.Length) throw new InvalidDataException("Diagnostic sprite layer budget exceeded.");
            var count = 0;
            for (var index = 0; index < layers.Count; index++)
            {
                var layer = layers[index];
                if (!TryPhaseState(component, layer, out var state)) continue;
                if (layer.AnimationFrame < 0 || layer.AnimationFrame >= state.DelayCount
                    || !float.IsFinite(layer.AnimationTimeLeft))
                    throw new InvalidDataException($"Invalid native RSI phase on {id}:{index}.");
                _phaseScratch[count++] = new(index, layer.AnimationFrame, layer.AnimationTimeLeft,
                    layer.AutoAnimated, layer.Reversed);
            }
            var reason = count != 0 && _entities.HasComponent<SyncSpriteComponent>(uid) ? "native-realtime-sync" : null;
            if (reason != null) count = 0;
            var values = _phaseScratch.AsSpan(0, count);
            var existed = _phaseStates.TryGetValue(id, out var previous);
            if (existed && previous.Value.SpriteId == spriteId && previous.Value.UnavailableReason == reason
                && values.SequenceEqual(previous.Value.Layers)) continue; // timestamp alone never dirties phase
            if (!existed && count == 0 && reason == null) continue;
            var replacement = new SpritePhaseReplacement(id, spriteId, sampleReplayTime100ns, reason, values.ToArray());
            // Count encoded snapshots plus conservative owned record/array overhead, not a second unbounded map.
            var encodedBytes = JsonSerializer.SerializeToUtf8Bytes(replacement, Json).Length;
            var retainedBytes = Math.Max(encodedBytes, 128 + count * 32);
            var next = _phaseRetainedBytes - (existed ? previous.Bytes : 0)
                + (count == 0 && reason == null ? 0 : retainedBytes);
            if (next > Program.MaxPhaseRetainedBytes) throw new InvalidDataException("Retained phase sublimit exceeded.");
            _phaseRetainedBytes = next;
            _phaseRetainedPeakBytes = Math.Max(_phaseRetainedPeakBytes, next);
            if (count == 0 && reason == null) _phaseStates.Remove(id);
            else _phaseStates[id] = new(replacement, retainedBytes);
            replacements.Add(replacement);
            frameBytes += encodedBytes;
            _phaseReplacements++;
            _phaseLayerSamples += count;
        }
        _maximumFramePhaseJSONBytes = Math.Max(_maximumFramePhaseJSONBytes, frameBytes);
        CapturePhaseProbe(sampleReplayTime100ns, sequence);
        return replacements;
    }

    private void RemovePhaseState(int id)
    {
        if (_phaseStates.Remove(id, out var previous)) _phaseRetainedBytes -= previous.Bytes;
    }

    private void CapturePhaseProbe(long replayTime100ns, int sequence)
    {
        if (Program.AssertPhaseEntity is not { } id) return;
        if (!_presentationOwners.TryGetValue(id, out var uid)
            || !_entities.TryGetComponent<SpriteComponent>(uid, out var component)
            || !_entities.TryGetComponent<MetaDataComponent>(uid, out var metadata)
            || !_entities.TryGetComponent<TransformComponent>(uid, out var transform))
            throw new InvalidDataException($"Asserted phase owner {id} is unavailable.");
        if (_entities.HasComponent<SyncSpriteComponent>(uid))
            throw new InvalidDataException($"Asserted ordinary phase owner {id} has runtime SyncSprite.");
        var layers = (IReadOnlyList<SpriteComponent.Layer>) component.AllLayers;
        var index = Program.AssertPhaseLayer;
        if (index >= layers.Count || !TryPhaseState(component, layers[index], out var state))
            throw new InvalidDataException($"Asserted phase layer {id}:{index} has no valid multi-frame RSI.");
        if (_phaseProbe.Count >= 128) throw new InvalidDataException("Phase assertion sample budget exceeded; choose a short clip.");
        var layer = layers[index];
        _phaseProbe.Add(new { sequence, entityId = id, spriteId = _previousSprites[id], index,
            sampleReplayTime100ns = replayTime100ns, syncSprite = false, metadata.EntityPaused,
            layer.Visible, layer.AnimationFrame, layer.AnimationTimeLeft, layer.AutoAnimated, layer.Reversed,
            layer.Loop, layer.Cycle, spriteLoop = component.Loop, delaysSeconds = state.GetDelays().ToArray(),
            rsiPath = (layer.RSI ?? component.BaseRSI)!.Path.ToString(), rsiState = layer.State.Name,
            parentId = transform.ParentUid == EntityUid.Invalid ? (int?) null : _entities.GetNetEntity(transform.ParentUid).Id,
            transform = new { x = transform.LocalPosition.X, y = transform.LocalPosition.Y, rotation = transform.LocalRotation.Theta } });
    }

    private sealed record SpritePhaseReplacement(int EntityId, int SpriteId, long SampleReplayTime100ns,
        string? UnavailableReason, LayerPhaseValue[] Layers);
    private readonly record struct LayerPhaseValue(int Index, int AnimationFrame, float AnimationTimeLeft,
        bool AutoAnimated, bool Reversed);
    private readonly record struct RetainedPhase(SpritePhaseReplacement Value, int Bytes);
}
