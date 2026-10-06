using System.Text.Json;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Map.Components;

namespace Content.Replay.Diagnostic;

public sealed partial class CaptureRunner
{
    private readonly LightingObservationInventory _lighting = new();
    private readonly HashSet<int> _lightingGraphMaps = new();
    private long _pointLightingInspections;
    private long _mapLightingInspections;
    private long _pointLightingReplacements;
    private long _mapLightingReplacements;
    private long _pointLightingDeletes;
    private long _mapLightingDeletes;
    private int _initialPointLights;
    private int _initialMapLights;

    private LightingChanges CaptureLighting(List<object> upserts, List<object> audioEvents, bool initial)
    {
        _lighting.Begin();
        // AllEntityQueryEnumerator includes paused entities. Pausing does not remove
        // a component from the observation inventory or grant an implicit delete.
        // Enumerate native membership, including disabled/occluded and client-created owners.
        // Neither replay network candidates nor sprite membership define this inventory.
        var points = _entities.AllEntityQueryEnumerator<PointLightComponent>();
        while (points.MoveNext(out var uid, out var light))
        {
            if (!_entities.TryGetComponent<MetaDataComponent>(uid, out var metadata))
                throw new InvalidDataException("Native point light is missing required metadata.");
            if (!LightingOwnerLive(metadata)) continue;
            if (!_entities.TryGetComponent<TransformComponent>(uid, out var transform))
                throw new InvalidDataException("Native point light is missing its transform.");
            EnsureLightingGraph(uid, metadata, upserts, audioEvents, initial);
            int? mapId = null;
            if (transform.MapUid is { } mapUid)
            {
                if (!_entities.TryGetComponent<MapComponent>(mapUid, out _)
                    || !_entities.TryGetComponent<MetaDataComponent>(mapUid, out var mapMetadata)
                    || !LightingOwnerLive(mapMetadata))
                    throw new InvalidDataException("Native point light has an unavailable map owner.");
                EnsureLightingGraph(mapUid, mapMetadata, upserts, audioEvents, initial);
                mapId = mapMetadata.NetEntity.Id;
            }
            var mask = light.GetReplayMaskTexture();
            ActualLightMask actualMask;
            if (mask == null) actualMask = new("none");
            // An atlas crop cannot be represented by this capability's whole-image binding.
            else if (mask is not AtlasTexture && _texturePaths.TryGetValue(mask, out var path))
                actualMask = new("image", EnsureResource("image", path, BodyMetadata));
            else actualMask = new("unavailable", Reason: mask is AtlasTexture
                ? "Resolved native mask is an atlas region; whole-image binding is unavailable."
                : "Resolved native mask has no loaded image resource path.");
            _lighting.Observe(new PointLightObservation(metadata.NetEntity.Id, mapId,
                light.Enabled, light.ContainerOccluded, LightingColorValue(light.Color),
                new(light.Offset.X, light.Offset.Y), light.Energy, light.Radius, light.Softness,
                light.Falloff, light.CurveFactor, light.CastShadows, (float) light.Rotation.Theta,
                light.MaskAutoRotate, light.LightMask?.ToString(), actualMask));
            _pointLightingInspections++;
        }

        var maps = _entities.AllEntityQueryEnumerator<MapComponent>();
        while (maps.MoveNext(out var uid, out var map))
        {
            if (!_entities.TryGetComponent<MetaDataComponent>(uid, out var metadata))
                throw new InvalidDataException("Native map is missing required metadata.");
            if (!LightingOwnerLive(metadata)) continue;
            EnsureLightingGraph(uid, metadata, upserts, audioEvents, initial);
            if (!_lighting.ContainsMap(metadata.NetEntity.Id))
                ProjectNative(uid, upserts, audioEvents, initial);
            var present = _entities.TryGetComponent<MapLightComponent>(uid, out var ambient);
            _lighting.Observe(new MapLightingObservation(metadata.NetEntity.Id, map.LightingEnabled,
                present, present ? LightingColorValue(ambient!.AmbientLightColor) : null));
            _mapLightingInspections++;
        }
        var changes = _lighting.Finish();
        foreach (var id in changes.MapDeletes)
            if (!_frameDeleted.Contains(id) && _entities.TryGetEntity(new NetEntity(id), out var uid) && uid is { } live)
                ProjectNative(live, upserts, audioEvents, initial);
        _pointLightingReplacements += changes.PointReplacements.Count;
        _mapLightingReplacements += changes.MapReplacements.Count;
        _pointLightingDeletes += changes.PointDeletes.Count;
        _mapLightingDeletes += changes.MapDeletes.Count;
        if (initial) { _initialPointLights = _lighting.PointCount; _initialMapLights = _lighting.MapCount; }
        return changes;
    }

    private static bool LightingOwnerLive(MetaDataComponent metadata)
        => metadata.EntityLifeStage >= EntityLifeStage.Initialized && metadata.EntityLifeStage < EntityLifeStage.Terminating;

    private void EnsureLightingGraph(EntityUid uid, MetaDataComponent metadata,
        List<object> upserts, List<object> audioEvents, bool initial)
    {
        if (!metadata.NetEntity.Valid || _frameDeleted.Contains(metadata.NetEntity.Id)
            || !_entities.TryGetEntity(metadata.NetEntity, out var mapped) || mapped != uid)
            throw new InvalidDataException("Native lighting identity is invalid or deleted.");
        // Existing network/move passes cover retained transforms. Only newly observed graph
        // members need full projection here; stable lighting does not reserialize their sprites.
        if (!_fingerprints.ContainsKey(metadata.NetEntity.Id))
            ProjectNative(uid, upserts, audioEvents, initial);
    }

    private void ValidateLightingClosure()
    {
        var maps = _lighting.Maps.Select(value => value.OwnerId).ToHashSet();
        if (!maps.SetEquals(_lightingGraphMaps))
            throw new InvalidDataException("Native map lighting inventory does not cover the final captured map graph.");
        foreach (var id in maps)
            if (!_fingerprints.ContainsKey(id)) throw new InvalidDataException("Missing map lighting graph owner.");
        foreach (var point in _lighting.Points)
        {
            if (!_fingerprints.ContainsKey(point.OwnerId))
                throw new InvalidDataException("Missing point lighting graph owner.");
            int? map = null;
            int? owner = point.OwnerId;
            for (var depth = 0; owner is { } id; depth++)
            {
                if (depth > MaxParentDepth || !_projectedParents.TryGetValue(id, out owner))
                    throw new InvalidDataException("Incomplete native lighting parent closure.");
                if (maps.Contains(id)) { map = id; break; }
            }
            if (map != point.MapEntityId)
                throw new InvalidDataException("Native light map disagrees with final transform ancestry.");
        }
    }

    private static LightingColor LightingColorValue(Robust.Shared.Maths.Color color)
        => new(color.R, color.G, color.B, color.A);

    private object LightingSummary() => new {
        complete = true, initialPointLights = _initialPointLights, initialMaps = _initialMapLights,
        pointLightsAtEnd = _lighting.PointCount, mapsAtEnd = _lighting.MapCount,
        pointReplacements = _pointLightingReplacements, mapReplacements = _mapLightingReplacements,
        pointDeletes = _pointLightingDeletes, mapDeletes = _mapLightingDeletes,
        scalarPointInspections = _pointLightingInspections, scalarMapInspections = _mapLightingInspections,
        actualMasks = new { none = _lighting.Points.Count(value => value.ActualMask.Kind == "none"),
            image = _lighting.Points.Count(value => value.ActualMask.Kind == "image"),
            unavailable = _lighting.Points.Count(value => value.ActualMask.Kind == "unavailable") },
        retainedBytes = _lighting.RetainedBytes, peakLiveStagedBytes = _lighting.PeakLiveStagedBytes,
        limits = new { owners = LightingObservationPolicy.MaxOwners, retainedBytes = LightingObservationPolicy.MaxRetainedBytes },
        accounting = "Owned JSON value bytes plus 256 per point/192 per map, live and pending values, 64 per sampled projection, 16 per removal. Canonical Go also applies its combined 512 MiB guard.",
        unavailable = new[] { "lighting-rendering", "roofs", "tile-emission", "point-shadows", "eye-fov", "sun", "ambient-occlusion" }
    };

    // Old profiles use their original serializer path and omit the new group entirely.
    private static object WithLighting<T>(T record, string name, object lighting)
    {
        var fields = JsonSerializer.SerializeToElement(record, Json).EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value);
        fields.Add(name, JsonSerializer.SerializeToElement(lighting, Json));
        return fields;
    }
}
