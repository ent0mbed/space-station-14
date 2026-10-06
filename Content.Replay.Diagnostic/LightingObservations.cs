using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Content.Replay.Diagnostic;

internal readonly record struct LightingColor(float R, float G, float B, float A);
internal readonly record struct LightingOffset(float X, float Y);
internal readonly record struct ActualLightMask(string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ResourceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);
internal readonly record struct PointLightObservation(int OwnerId, int? MapEntityId,
    bool Enabled, bool ContainerOccluded, LightingColor ColorSrgb, LightingOffset Offset,
    float Energy, float Radius, float Softness, float Falloff, float CurveFactor, bool CastShadows,
    float Rotation, bool MaskAutoRotate, string? DeclaredMaskId, ActualLightMask ActualMask);
internal readonly record struct MapLightingObservation(int OwnerId, bool LightingEnabled,
    bool AmbientPresent, LightingColor? AmbientLinear);
internal sealed record LightOccluderObservation(int OwnerId, bool Enabled,
    IReadOnlyList<LightingOffset> LocalVertices, byte SharedEdges);
internal sealed record LightingChanges(bool Complete, List<PointLightObservation> PointReplacements,
    List<int> PointDeletes, List<MapLightingObservation> MapReplacements, List<int> MapDeletes,
    List<LightOccluderObservation> OccluderReplacements, List<int> OccluderDeletes)
{
    // Views over the owned pending lists are serialized synchronously before Begin
    // can mutate them. Snapshot chunks do not allocate replacement-list copies.
    public LightingChunk Chunk(int index, bool initial) => new(Complete,
        initial ? PointReplacements.Skip(index * 1000).Take(1000) : PointReplacements,
        PointDeletes, initial ? MapReplacements.Skip(index * 1000).Take(1000) : MapReplacements,
        MapDeletes, initial ? OccluderReplacements.Skip(index * 1000).Take(1000) : OccluderReplacements,
        OccluderDeletes);
}
internal sealed record LightingChunk(bool Complete, IEnumerable<PointLightObservation> PointReplacements,
    IReadOnlyList<int> PointDeletes, IEnumerable<MapLightingObservation> MapReplacements, IReadOnlyList<int> MapDeletes,
    IEnumerable<LightOccluderObservation> OccluderReplacements, IReadOnlyList<int> OccluderDeletes);

// Own only the latest native observations, plus this frame's pending changes.
// Comparisons include every scalar and resolved mask identity, independent of sprites/network dirtiness.
internal sealed class LightingObservationInventory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<int, (PointLightObservation Value, int Bytes)> _points = new();
    private readonly Dictionary<int, (MapLightingObservation Value, int Bytes)> _maps = new();
    private readonly Dictionary<int, (LightOccluderObservation Value, int Bytes)> _occluders = new();
    private readonly HashSet<int> _pointSeen = new();
    private readonly HashSet<int> _mapSeen = new();
    private readonly HashSet<int> _occluderSeen = new();
    private readonly int _maxOwners;
    private readonly long _maxBytes;
    private LightingChanges _changes = new(true, [], [], [], [], [], []);
    private long _pendingBytes;
    private long _projectionBytes;
    public long RetainedBytes { get; private set; }
    public long PeakLiveStagedBytes { get; private set; }
    public int PointCount => _points.Count;
    public int MapCount => _maps.Count;
    public int OccluderCount => _occluders.Count;
    public bool ContainsMap(int id) => _maps.ContainsKey(id);
    public IEnumerable<PointLightObservation> Points => _points.Values.Select(entry => entry.Value);
    public IEnumerable<MapLightingObservation> Maps => _maps.Values.Select(entry => entry.Value);
    public IEnumerable<LightOccluderObservation> Occluders => _occluders.Values.Select(entry => entry.Value);

    public LightingObservationInventory(int maxOwners = LightingObservationPolicy.MaxOwners,
        long maxBytes = LightingObservationPolicy.MaxRetainedBytes)
    {
        if (maxOwners is <= 0 or > LightingObservationPolicy.MaxOwners
            || maxBytes is <= 0 or > LightingObservationPolicy.MaxRetainedBytes)
            throw new ArgumentOutOfRangeException(nameof(maxOwners));
        _maxOwners = maxOwners;
        _maxBytes = maxBytes;
    }

    public void Begin()
    {
        _pointSeen.Clear();
        _mapSeen.Clear();
        _occluderSeen.Clear();
        _changes = new(true, [], [], [], [], [], []);
        _pendingBytes = 0;
        _projectionBytes = 0;
    }

    public void Observe(PointLightObservation value)
    {
        Validate(value);
        See(value.OwnerId, _pointSeen);
        if (_points.TryGetValue(value.OwnerId, out var previous) && previous.Value == value) return;
        var bytes = checked(JsonSerializer.SerializeToUtf8Bytes(value, Json).Length + 256);
        Charge(bytes);
        _changes.PointReplacements.Add(value);
        _points[value.OwnerId] = (value, bytes);
        RetainedBytes += bytes - previous.Bytes;
        CheckBudget();
    }

    public void Observe(MapLightingObservation value)
    {
        if (value.OwnerId <= 0 || value.AmbientPresent != value.AmbientLinear.HasValue)
            throw new InvalidDataException("Invalid native map lighting observation.");
        if (value.AmbientLinear is { } color) Validate(color);
        See(value.OwnerId, _mapSeen);
        if (_maps.TryGetValue(value.OwnerId, out var previous) && previous.Value == value) return;
        var bytes = checked(JsonSerializer.SerializeToUtf8Bytes(value, Json).Length + 192);
        Charge(bytes);
        _changes.MapReplacements.Add(value);
        _maps[value.OwnerId] = (value, bytes);
        RetainedBytes += bytes - previous.Bytes;
        CheckBudget();
    }

    public void ObserveOccluder(int ownerId, bool enabled, ReadOnlySpan<Vector2> polygon, byte sharedEdges)
    {
        var clockwise = ValidatePolygon(ownerId, polygon, sharedEdges);
        See(ownerId, _occluderSeen);
        var found = _occluders.TryGetValue(ownerId, out var previous);
        var geometryEqual = found && previous.Value.LocalVertices.Count == polygon.Length;
        for (var i = 0; geometryEqual && i < polygon.Length; i++)
        {
            var vertex = polygon[clockwise ? i : polygon.Length - 1 - i];
            geometryEqual = previous.Value.LocalVertices[i] == new LightingOffset(vertex.X, vertex.Y);
        }
        if (geometryEqual && previous.Value.Enabled == enabled && previous.Value.SharedEdges == sharedEdges)
            return;

        IReadOnlyList<LightingOffset> vertices;
        if (geometryEqual) vertices = previous.Value.LocalVertices;
        else
        {
            // Never retain the component's borrowed span. Native setters/state updates may
            // replace or mutate it. Only changed geometry needs a private, immutable copy.
            var owned = new LightingOffset[polygon.Length];
            for (var i = 0; i < polygon.Length; i++)
            {
                var vertex = polygon[clockwise ? i : polygon.Length - 1 - i];
                owned[i] = new(vertex.X, vertex.Y);
            }
            vertices = Array.AsReadOnly(owned);
        }
        // Native OccludingEdges already indexes this exact clockwise render order.
        // Reversing counterclockwise vertices must not reverse/remap the mask again.
        var value = new LightOccluderObservation(ownerId, enabled, vertices, sharedEdges);
        var bytes = checked(JsonSerializer.SerializeToUtf8Bytes(value, Json).Length + 256 + polygon.Length * 8);
        Charge(bytes);
        _changes.OccluderReplacements.Add(value);
        _occluders[ownerId] = (value, bytes);
        RetainedBytes += bytes - previous.Bytes;
        CheckBudget();
    }

    private static bool ValidatePolygon(int ownerId, ReadOnlySpan<Vector2> polygon, byte sharedEdges)
    {
        if (ownerId <= 0 || polygon.Length is < 3 or > LightingObservationPolicy.MaxOccluderVertices
            || (sharedEdges >> polygon.Length) != 0)
            throw new InvalidDataException("Invalid native occluder identity, vertex count or edge mask.");
        var area = 0f;
        for (var i = 0; i < polygon.Length; i++)
        {
            var vertex = polygon[i];
            if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y))
                throw new InvalidDataException("Nonfinite native occluder vertex.");
            for (var j = 0; j < i; j++)
                if (polygon[j] == vertex) throw new InvalidDataException("Duplicate native occluder vertex.");
            var next = polygon[(i + 1) % polygon.Length];
            // Keep native float arithmetic and operation order: signed area < 0
            // preserves order; the other winding uses sourceIndex = n - 1 - i.
            area += vertex.X * next.Y;
            area -= vertex.Y * next.X;
        }
        area *= 0.5f;
        if (!float.IsFinite(area) || area == 0f)
            throw new InvalidDataException("Degenerate or unrepresentable native occluder area.");
        var clockwise = area < 0f;
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            for (var j = 0; j < polygon.Length; j++)
            {
                var vertex = polygon[j];
                var cross = ((double) b.X - a.X) * ((double) vertex.Y - a.Y)
                    - ((double) b.Y - a.Y) * ((double) vertex.X - a.X);
                if (clockwise ? cross > 0 : cross < 0)
                    throw new InvalidDataException("Nonconvex native occluder polygon.");
            }
        }
        return clockwise;
    }

    public LightingChanges Finish()
    {
        foreach (var id in _points.Keys)
            if (!_pointSeen.Contains(id)) { Charge(16); _changes.PointDeletes.Add(id); }
        foreach (var id in _maps.Keys)
            if (!_mapSeen.Contains(id)) { Charge(16); _changes.MapDeletes.Add(id); }
        foreach (var id in _occluders.Keys)
            if (!_occluderSeen.Contains(id)) { Charge(16); _changes.OccluderDeletes.Add(id); }
        foreach (var id in _changes.PointDeletes) { RetainedBytes -= _points[id].Bytes; _points.Remove(id); }
        foreach (var id in _changes.MapDeletes) { RetainedBytes -= _maps[id].Bytes; _maps.Remove(id); }
        foreach (var id in _changes.OccluderDeletes) { RetainedBytes -= _occluders[id].Bytes; _occluders.Remove(id); }
        _changes.PointReplacements.Sort((a, b) => a.OwnerId.CompareTo(b.OwnerId));
        _changes.MapReplacements.Sort((a, b) => a.OwnerId.CompareTo(b.OwnerId));
        _changes.OccluderReplacements.Sort((a, b) => a.OwnerId.CompareTo(b.OwnerId));
        _changes.PointDeletes.Sort();
        _changes.MapDeletes.Sort();
        _changes.OccluderDeletes.Sort();
        return _changes;
    }

    private void See(int id, HashSet<int> seen)
    {
        if (_pointSeen.Count + _mapSeen.Count + _occluderSeen.Count >= _maxOwners || !seen.Add(id))
            throw new InvalidDataException("Lighting owner budget exceeded or duplicate native observation.");
        _projectionBytes += 64;
        CheckBudget();
    }

    private void Charge(long bytes) { _pendingBytes += bytes; CheckBudget(); }
    private void CheckBudget()
    {
        var bytes = checked(RetainedBytes + _pendingBytes + _projectionBytes);
        if (bytes > _maxBytes) throw new InvalidDataException("Lighting live/staged byte budget exceeded.");
        PeakLiveStagedBytes = Math.Max(PeakLiveStagedBytes, bytes);
    }

    internal static void Validate(PointLightObservation value)
    {
        if (value.OwnerId <= 0 || value.MapEntityId is <= 0)
            throw new InvalidDataException("Invalid native point lighting identity.");
        Validate(value.ColorSrgb);
        if (!float.IsFinite(value.Offset.X) || !float.IsFinite(value.Offset.Y)
            || !float.IsFinite(value.Energy) || !float.IsFinite(value.Radius)
            || !float.IsFinite(value.Softness) || !float.IsFinite(value.Falloff)
            || !float.IsFinite(value.CurveFactor) || !float.IsFinite(value.Rotation))
            throw new InvalidDataException("Nonfinite native point lighting value.");
        if (value.DeclaredMaskId != null) ValidateString(value.DeclaredMaskId);
        var mask = value.ActualMask;
        switch (mask.Kind)
        {
            case "none" when mask.ResourceId == null && mask.Reason == null: break;
            case "image" when mask.ResourceId is > 0 && mask.Reason == null: break;
            case "unavailable" when mask.ResourceId == null && mask.Reason != null:
                ValidateString(mask.Reason); break;
            default: throw new InvalidDataException("Invalid actual native light mask observation.");
        }
    }

    private static void Validate(LightingColor value)
    {
        if (!float.IsFinite(value.R) || !float.IsFinite(value.G) || !float.IsFinite(value.B) || !float.IsFinite(value.A))
            throw new InvalidDataException("Nonfinite native lighting color.");
    }
    private static void ValidateString(string value)
    {
        if (string.IsNullOrEmpty(value) || new UTF8Encoding(false, true).GetByteCount(value) > 1024)
            throw new InvalidDataException("Invalid native lighting mask identifier/reason.");
    }
}
