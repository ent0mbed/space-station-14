using System.Text.Json;
using System.Diagnostics;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;

namespace Content.Replay.Diagnostic;

public sealed partial class CaptureRunner
{
    // Includes phase-free and sprite-free exported owners, so client-only transitions
    // into animation and component add/remove do not require a network/move candidate.
    private sealed class PresentationOwner(EntityUid uid)
    {
        public readonly EntityUid Uid = uid;
        public AnimationEligibilityEntry Eligibility;
    }

    private readonly Dictionary<int, PresentationOwner> _presentationOwners = new();
    private readonly List<(int Id, EntityUid Uid)> _appearanceCandidates = new();
    private readonly LayerPhaseValue[] _phaseScratch = new LayerPhaseValue[Program.MaxPresentationLayersPerOwner];
    private readonly Dictionary<int, RetainedPresentation> _presentationStates = new();
    private readonly Dictionary<int, PendingPresentation> _pendingPresentation = new();
    private readonly List<int> _pendingPresentationOrder = new();
    private readonly HashSet<int> _presentationBaselineOwners = new();
    private long _pendingPresentationBytes;
    private long _presentationCombinedPeakBytes;
    private long _queueOwnerVisits, _queueLayerVisits, _postOwnerVisits, _postLayerVisits;
    private long _appearanceLayerComparisons, _phaseLayerVisits, _baselineOwnerVisits, _baselineLayerVisits;
    private long _presentationArraysAllocated, _presentationSerializations;
    private double _queueMs, _fusedInspectionMs, _appearanceProjectionMs, _baselineDrainMs, _presentationCommitMs, _presentationSerializationMs;
    private readonly List<object> _ordinaryPhaseProbe = new();
    private long _presentationRetainedBytes;
    private long _presentationRetainedPeakBytes;
    private long _presentationReplacements;
    private long _presentationLayerSamples;
    private long _presentationScalarInspections;
    private long _ordinaryForceUpdates;
    private long _independentAppearanceCandidates;
    private long _maximumFramePresentationJSONBytes;

    private void TrackPresentationOwner(int id, EntityUid uid)
    {
        if (!_presentationOwners.ContainsKey(id))
        {
            if (_presentationOwners.Count >= Program.MaxPresentationOwners)
                throw new InvalidDataException("Presentation owner budget exceeded.");
        }
        if (!_presentationOwners.TryGetValue(id, out var owner) || owner.Uid != uid)
            _presentationOwners[id] = new(uid);
    }

    private void ResetAnimationEligibility()
    {
        foreach (var owner in _presentationOwners.Values) owner.Eligibility = default;
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

    // The pinned Robust 289.0.3 adapter exposes its backing List through AllLayers.
    // Read it only within the current pass; never retain native layers across FrameUpdate.
    private static List<SpriteComponent.Layer> GetNativePresentationLayers(SpriteComponent component)
        => (List<SpriteComponent.Layer>) component.AllLayers;

    private void QueueOrdinarySprite(EntityUid uid, SpriteComponent component, SpriteSystem system,
        in EntityQuery<SyncSpriteComponent> syncSprites, PresentationOwner? owner = null)
    {
        _queueOwnerVisits++;
        if (syncSprites.HasComponent(uid))
        {
            if (owner != null) owner.Eligibility = default;
            return;
        }
        var layers = GetNativePresentationLayers(component);
        if (layers.Count > Program.MaxPresentationLayersPerOwner)
            throw new InvalidDataException("Diagnostic sprite layer budget exceeded.");
        bool eligible;
        if (Program.OrdinaryAnimationEligibility == AnimationEligibilityStrategy.Cached && owner != null)
        {
            var before = ReadEligibilityStamp(component, layers.Count);
            if (!owner.Eligibility.TryGet(before, out eligible))
            {
                eligible = ScanOrdinaryEligibility(component, layers);
                // A racing resource load/mutation must not publish a stale cached
                // result. The current selection still uses the reference scan result.
                owner.Eligibility = AnimationEligibilityEntry.AfterScan(before,
                    ReadEligibilityStamp(component, layers.Count), eligible);
            }
        }
        else
        {
            eligible = ScanOrdinaryEligibility(component, layers);
        }
        if (!eligible) return;
        system.ForceUpdate(uid);
        _ordinaryForceUpdates++;
    }

    private static AnimationEligibilityStamp ReadEligibilityStamp(SpriteComponent component, int layerCount)
        => new(component.ReplayAnimationIdentity, component.ReplayAnimationEligibilityRevision,
            RSI.ReplayAnimationStateEpoch, layerCount);

    private bool ScanOrdinaryEligibility(SpriteComponent component, List<SpriteComponent.Layer> layers)
    {
        // Do not inspect cached IsInert. Its queued recomputation happens inside FrameUpdate.
        for (var index = 0; index < layers.Count; index++)
        {
            var layer = layers[index];
            _queueLayerVisits++;
            if (layer.Visible && layer.AutoAnimated && TryPhaseState(component, layer, out _))
            {
                return true;
            }
        }
        return false;
    }

    private void QueueOrdinaryPhases(Robust.Shared.GameStates.GameState state, bool initial)
    {
        var start = Stopwatch.GetTimestamp();
        QueueOrdinaryPhasesCore(state, initial);
        _queueMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private void QueueOrdinaryPhasesCore(Robust.Shared.GameStates.GameState state, bool initial)
    {
        var system = _entities.System<SpriteSystem>();
        // Pass-local queries keep the native live-dictionary lookup and Deleted check.
        var sprites = _entities.GetEntityQuery<SpriteComponent>();
        var metadataQuery = _entities.GetEntityQuery<MetaDataComponent>();
        var syncSprites = _entities.GetEntityQuery<SyncSpriteComponent>();
        if (initial)
        {
            ResetAnimationEligibility();
            var query = _entities.EntityQueryEnumerator<SpriteComponent, MetaDataComponent>();
            while (query.MoveNext(out var uid, out var sprite, out var metadata))
                if (!metadata.NetEntity.IsClientSide() && metadata.EntityLifeStage >= EntityLifeStage.Initialized
                    && metadata.EntityLifeStage < EntityLifeStage.Terminating)
                    QueueOrdinarySprite(uid, sprite, system, in syncSprites);
            return;
        }
        foreach (var owner in _presentationOwners.Values)
            if (sprites.TryGetComponent(owner.Uid, out var sprite)
                && metadataQuery.TryGetComponent(owner.Uid, out var metadata)
                && metadata.EntityLifeStage >= EntityLifeStage.Initialized
                && metadata.EntityLifeStage < EntityLifeStage.Terminating)
                QueueOrdinarySprite(owner.Uid, sprite, system, in syncSprites, owner);
            else
                owner.Eligibility = default;
        // New exported network owners need their first native tick, too.
        foreach (var entity in state.EntityStates.Value)
            if (!_presentationOwners.ContainsKey(entity.NetEntity.Id)
                && _entities.TryGetEntity(entity.NetEntity, out var uid)
                && sprites.TryGetComponent(uid, out var sprite))
                QueueOrdinarySprite(uid.Value, sprite, system, in syncSprites);
    }

    private void InspectSpritePresentation(bool inspectAppearance)
    {
        var start = Stopwatch.GetTimestamp();
        _appearanceCandidates.Clear();
        _pendingPresentation.Clear();
        _pendingPresentationOrder.Clear();
        _pendingPresentationBytes = 0;
        var sprites = _entities.GetEntityQuery<SpriteComponent>();
        var metadataQuery = _entities.GetEntityQuery<MetaDataComponent>();
        var syncSprites = _entities.GetEntityQuery<SyncSpriteComponent>();
        foreach (var (id, owner) in _presentationOwners)
        {
            var uid = owner.Uid;
            _postOwnerVisits++;
            _presentationScalarInspections++;
            if (!metadataQuery.TryGetComponent(uid, out var metadata)
                || metadata.EntityLifeStage >= EntityLifeStage.Terminating)
                throw new InvalidDataException($"Retained presentation owner {id} disappeared without observed deletion.");
            if (!sprites.TryGetComponent(uid, out var component))
            {
                if (inspectAppearance && _previousSprites.ContainsKey(id)) _appearanceCandidates.Add((id, uid));
                continue;
            }
            var layers = GetNativePresentationLayers(component);
            if (layers.Count > _phaseScratch.Length) throw new InvalidDataException("Diagnostic sprite layer budget exceeded.");
            var spriteId = _previousSprites.GetValueOrDefault(id);
            var definition = spriteId == 0 ? null : _spriteDefinitions[spriteId - 1];
            var matches = !inspectAppearance || definition != null
                && ReadSpriteHead(component, definition.Head.NativeLocalBounds) == definition.Head
                && layers.Count == definition.Layers.Length;
            var reason = syncSprites.HasComponent(uid) ? "native-realtime-sync" : null;
            var count = 0;
            for (var index = 0; index < layers.Count; index++)
            {
                _postLayerVisits++;
                var layer = layers[index];
                if (inspectAppearance && matches)
                {
                    _appearanceLayerComparisons++;
                    if (!TryReadCapturedMaterial(layer.Shader, out var material)
                        || ReadLayerValue(uid, component, layer, index, layers.Count, material) != definition!.Layers[index])
                        matches = false;
                }
                if (reason == null)
                {
                    _phaseLayerVisits++;
                    ReadPhaseLayer(id, component, layer, index, ref count);
                }
            }
            if (inspectAppearance && matches && !NativePostAppearanceMatches(component, definition!)) matches = false;
            if (!matches) _appearanceCandidates.Add((id, uid));
            StagePresentation(id, spriteId, VectorValue.From(component.Offset), reason, count, !matches);
        }
        _fusedInspectionMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private void ProjectPresentationAppearance(List<object> upserts, List<object> audioEvents)
    {
        var start = Stopwatch.GetTimestamp();
        // The candidate list is reusable; stable owners never run full CaptureSprite/material/bounds work.
        _appearanceCandidates.Sort((left, right) => left.Id.CompareTo(right.Id));
        foreach (var (_, uid) in _appearanceCandidates)
        {
            _independentAppearanceCandidates++;
            ProjectNative(uid, upserts, audioEvents, false);
        }
        _appearanceProjectionMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private void ReadPhaseLayer(int id, SpriteComponent component, SpriteComponent.Layer layer, int index, ref int count)
    {
        if (!TryPhaseState(component, layer, out var state)) return;
        if (layer.AnimationFrame < 0 || layer.AnimationFrame >= state.DelayCount || !float.IsFinite(layer.AnimationTimeLeft))
            throw new InvalidDataException($"Invalid native RSI phase on {id}:{index}.");
        _phaseScratch[count++] = new(index, layer.AnimationFrame, layer.AnimationTimeLeft, layer.AutoAnimated, layer.Reversed);
    }

    private static bool PresentationMatches(SpritePresentationReplacement previous, int spriteId,
        VectorValue offset, string? reason, ReadOnlySpan<LayerPhaseValue> layers)
        => previous.SpriteId == spriteId && previous.Offset == offset && previous.PhaseUnavailableReason == reason
            && Program.Presentation.LayersEqual(previous.Layers, layers);

    private void StagePresentation(int id, int spriteId, VectorValue offset, string? reason, int count, bool rebind)
    {
        if (!float.IsFinite(offset.X) || !float.IsFinite(offset.Y))
            throw new InvalidDataException($"Nonfinite native sprite offset on {id}.");
        var layers = _phaseScratch.AsSpan(0, count);
        if (!rebind && _presentationStates.TryGetValue(id, out var previous)
            && PresentationMatches(previous.Value, spriteId, offset, reason, layers)) return;
        if (_pendingPresentation.ContainsKey(id)) throw new InvalidDataException("Duplicate staged presentation owner.");
        var bytes = 192 + count * 32;
        if (_presentationRetainedBytes + _pendingPresentationBytes + bytes > Program.MaxPresentationRetainedBytes)
            throw new InvalidDataException("Combined live/staged presentation sublimit exceeded.");
        _pendingPresentation[id] = new(offset, reason, layers.ToArray(), bytes);
        if (count != 0) _presentationArraysAllocated++;
        _pendingPresentationOrder.Add(id);
        _pendingPresentationBytes += bytes;
        _presentationCombinedPeakBytes = Math.Max(_presentationCombinedPeakBytes, _presentationRetainedBytes + _pendingPresentationBytes);
    }

    private List<SpritePresentationReplacement> FinishSpritePresentation(long sampleReplayTime100ns, int sequence)
    {
        var start = Stopwatch.GetTimestamp();
        // ProjectNative/parent closure may add owners or rebind IDs after the map walk.
        // Already-staged owners keep their immutable scalars; projection does not advance native phase.
        foreach (var id in _presentationBaselineOwners)
        {
            _baselineOwnerVisits++;
            if (_pendingPresentation.ContainsKey(id)) continue;
            if (!_presentationOwners.TryGetValue(id, out var owner)) continue;
            var uid = owner.Uid;
            if (!_previousSprites.TryGetValue(id, out var spriteId)
                || !_entities.TryGetComponent<SpriteComponent>(uid, out var component)) continue;
            var layers = (IReadOnlyList<SpriteComponent.Layer>) component.AllLayers;
            if (layers.Count > _phaseScratch.Length) throw new InvalidDataException("Diagnostic sprite layer budget exceeded.");
            // Offset is available for every sprite, independently of RSI phase coverage.
            var offset = VectorValue.From(component.Offset);
            var reason = _entities.HasComponent<SyncSpriteComponent>(uid) ? "native-realtime-sync" : null;
            var count = 0;
            for (var index = 0; reason == null && index < layers.Count; index++)
            {
                _baselineLayerVisits++;
                ReadPhaseLayer(id, component, layers[index], index, ref count);
            }
            StagePresentation(id, spriteId, offset, reason, count, false);
        }
        _presentationBaselineOwners.Clear();
        _baselineDrainMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        start = Stopwatch.GetTimestamp();
        var serializationBefore = _presentationSerializationMs;
        var replacements = new List<SpritePresentationReplacement>(_pendingPresentationOrder.Count);
        long frameBytes = 0;
        foreach (var id in _pendingPresentationOrder)
        {
            var pending = _pendingPresentation[id];
            _pendingPresentationBytes -= pending.Bytes;
            if (!_previousSprites.TryGetValue(id, out var spriteId)) continue;
            if (!TryReplaceSpritePresentation(id, spriteId, sampleReplayTime100ns, pending.Offset, pending.Reason,
                    pending.Layers.Length, out var replacement, out var encodedBytes, pending.Layers)) continue;
            replacements.Add(replacement!);
            frameBytes += encodedBytes;
            _presentationCombinedPeakBytes = Math.Max(_presentationCombinedPeakBytes, _presentationRetainedBytes + _pendingPresentationBytes);
        }
        _pendingPresentation.Clear();
        _pendingPresentationOrder.Clear();
        _presentationCommitMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds - (_presentationSerializationMs - serializationBefore);
        _maximumFramePresentationJSONBytes = Math.Max(_maximumFramePresentationJSONBytes, frameBytes);
        CapturePhaseProbe(sampleReplayTime100ns, sequence);
        return replacements;
    }

    // One complete owner-local value: empty phases still retain an available offset.
    // Immutable copies are allocated only when identity or actual presentation values change.
    private bool TryReplaceSpritePresentation(int id, int spriteId, long sampleReplayTime100ns,
        VectorValue offset, string? phaseUnavailableReason, int count,
        out SpritePresentationReplacement? replacement, out int encodedBytes, LayerPhaseValue[]? ownedLayers = null)
    {
        if (!float.IsFinite(offset.X) || !float.IsFinite(offset.Y))
            throw new InvalidDataException($"Nonfinite native sprite offset on {id}.");
        if (count < 0 || count > _phaseScratch.Length || sampleReplayTime100ns < 0)
            throw new InvalidDataException("Invalid native presentation sample.");
        var values = (ownedLayers ?? _phaseScratch).AsSpan(0, count);
        var existed = _presentationStates.TryGetValue(id, out var previous);
        replacement = null;
        encodedBytes = 0;
        // Silence keeps the actual previously emitted countdown and timestamp.
        if (existed && PresentationMatches(previous.Value, spriteId, offset, phaseUnavailableReason, values)) return false;
        if (!existed && _presentationStates.Count >= Program.MaxPresentationOwners)
            throw new InvalidDataException("Retained presentation owner budget exceeded.");
        replacement = new(id, spriteId, sampleReplayTime100ns, offset, phaseUnavailableReason, ownedLayers ?? values.ToArray());
        var serializationStart = Stopwatch.GetTimestamp();
        encodedBytes = JsonSerializer.SerializeToUtf8Bytes(replacement, Json).Length;
        _presentationSerializationMs += Stopwatch.GetElapsedTime(serializationStart).TotalMilliseconds;
        _presentationSerializations++;
        // Includes empty-phase owners and conservative owned record/array/dictionary overhead.
        var retainedBytes = Math.Max(encodedBytes, 192 + count * 32);
        var next = _presentationRetainedBytes - (existed ? previous.Bytes : 0) + retainedBytes;
        if (next + _pendingPresentationBytes > Program.MaxPresentationRetainedBytes)
            throw new InvalidDataException("Retained presentation sublimit exceeded.");
        _presentationRetainedBytes = next;
        _presentationRetainedPeakBytes = Math.Max(_presentationRetainedPeakBytes, next);
        _presentationStates[id] = new(replacement, retainedBytes);
        _presentationReplacements++;
        _presentationLayerSamples += count;
        return true;
    }

    private void RemovePresentationState(int id)
    {
        if (_presentationStates.Remove(id, out var previous)) _presentationRetainedBytes -= previous.Bytes;
    }

    private void CapturePhaseProbe(long replayTime100ns, int sequence)
    {
        if (Program.AssertPhaseEntity is not { } id) return;
        if (!_presentationOwners.TryGetValue(id, out var owner)
            || !_entities.TryGetComponent<SpriteComponent>(owner.Uid, out var component)
            || !_entities.TryGetComponent<MetaDataComponent>(owner.Uid, out var metadata)
            || !_entities.TryGetComponent<TransformComponent>(owner.Uid, out var transform))
            throw new InvalidDataException($"Asserted phase owner {id} is unavailable.");
        var uid = owner.Uid;
        if (_entities.HasComponent<SyncSpriteComponent>(uid))
            throw new InvalidDataException($"Asserted ordinary phase owner {id} has runtime SyncSprite.");
        var layers = (IReadOnlyList<SpriteComponent.Layer>) component.AllLayers;
        var index = Program.AssertPhaseLayer;
        if (index >= layers.Count || !TryPhaseState(component, layers[index], out var state))
            throw new InvalidDataException($"Asserted phase layer {id}:{index} has no valid multi-frame RSI.");
        if (_ordinaryPhaseProbe.Count >= 128) throw new InvalidDataException("Phase assertion sample budget exceeded; choose a short clip.");
        var layer = layers[index];
        _ordinaryPhaseProbe.Add(new { sequence, entityId = id, spriteId = _previousSprites[id], index,
            sampleReplayTime100ns = replayTime100ns, offset = VectorValue.From(component.Offset),
            syncSprite = false, metadata.EntityPaused,
            layer.Visible, layer.AnimationFrame, layer.AnimationTimeLeft, layer.AutoAnimated, layer.Reversed,
            layer.Loop, layer.Cycle, spriteLoop = component.Loop, delaysSeconds = state.GetDelays().ToArray(),
            rsiPath = (layer.RSI ?? component.BaseRSI)!.Path.ToString(), rsiState = layer.State.Name,
            parentId = transform.ParentUid == EntityUid.Invalid ? (int?) null : _entities.GetNetEntity(transform.ParentUid).Id,
            transform = new { x = transform.LocalPosition.X, y = transform.LocalPosition.Y, rotation = transform.LocalRotation.Theta } });
    }

    private sealed record SpritePresentationReplacement(int EntityId, int SpriteId, long SampleReplayTime100ns,
        VectorValue Offset, string? PhaseUnavailableReason, LayerPhaseValue[] Layers);
    private readonly record struct RetainedPresentation(SpritePresentationReplacement Value, int Bytes);
    private sealed record PendingPresentation(VectorValue Offset, string? Reason, LayerPhaseValue[] Layers, int Bytes);
}
