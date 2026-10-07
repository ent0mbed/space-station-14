using System.Text.Json;

namespace Content.Replay.Diagnostic;

internal static class RoofObservationPolicy
{
    public const string Capability = "native-resolved-roof-observations/1";
    public const int ChunkSize = 8;
    public const int MaxGrids = 4096;
    public const int MaxChunks = 32768;
    public const int MaxTiles = 500_000;
    public const long MaxBytes = 64L * 1024 * 1024;
    internal static long InvalidationBytes(int links, int contributors, int grids, int queued,
        long scratch = 0, long admission = 0) => checked(links * 192L + contributors * 256L
            + grids * 384L + queued * 64L + scratch + admission);
}

internal readonly record struct RoofTile(int X, int Y);
internal readonly record struct RoofSample(RoofTile Tile, LightingColor ColorSrgb);
internal readonly record struct RoofGridObservation(int OwnerId, int TileSize, bool Implicit);
internal readonly record struct RoofChunkKey(int OwnerId, int X, int Y);
internal sealed record RoofLayer(LightingColor ColorSrgb, IReadOnlyList<RoofTile> Tiles);
internal sealed record RoofChunkObservation(int OwnerId, int X, int Y, IReadOnlyList<RoofLayer> Layers);
internal sealed record RoofChanges(bool Complete, List<RoofGridObservation> GridReplacements,
    List<int> GridDeletes, List<RoofChunkObservation> ChunkReplacements, List<RoofChunkKey> ChunkDeletes)
{
    public object Chunk(int index, bool initial) => new { Complete,
        GridReplacements = initial ? GridReplacements.Skip(index * 1000).Take(1000) : GridReplacements,
        GridDeletes,
        ChunkReplacements = initial ? ChunkReplacements.Skip(index * 1000).Take(1000) : ChunkReplacements,
        ChunkDeletes };
}

// A current grid inventory, not a tile/prototype cache. Inputs are resolved native
// observations. No borrowed list, component dictionary or tile array survives ReplaceGrid.
internal sealed class RoofObservationInventory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<int, RoofGridObservation> _grids = new();
    private readonly Dictionary<RoofChunkKey, (RoofChunkObservation Value, int Bytes, int Tiles)> _chunks = new();
    private readonly Dictionary<int, HashSet<RoofChunkKey>> _byGrid = new();
    private readonly long _maxBytes;
    private RoofChanges _changes = new(true, [], [], [], []);
    private long _pendingBytes;
    private long _resolutionBytes;
    private long _outsideBytes;
    public long RetainedBytes { get; private set; }
    public long PeakLiveStagedBytes { get; private set; }
    public int GridCount => _grids.Count;
    public int ChunkCount => _chunks.Count;
    public int TileCount { get; private set; }
    public IEnumerable<RoofGridObservation> Grids => _grids.Values;
    internal RoofChunkObservation GetChunk(RoofChunkKey key) => _chunks[key].Value;

    public RoofObservationInventory(long maxBytes = RoofObservationPolicy.MaxBytes)
    {
        if (maxBytes is <= 0 or > RoofObservationPolicy.MaxBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxBytes = maxBytes;
    }

    public void Begin()
    {
        _changes = new(true, [], [], [], []);
        _pendingBytes = _resolutionBytes = 0;
    }

    public void SetOutsideBytes(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        CheckBudget(checked(bytes - _outsideBytes));
        _outsideBytes = bytes;
    }

    public void ReplaceGrid(int ownerId, int tileSize, IEnumerable<RoofSample> samples, bool implicitRoof = false)
    {
        if (ownerId <= 0 || tileSize is <= 0 or > ushort.MaxValue)
            throw new InvalidDataException("Invalid resolved roof grid identity or tile size.");
        if (!_grids.ContainsKey(ownerId) && _grids.Count >= RoofObservationPolicy.MaxGrids)
            throw new InvalidDataException("Resolved roof grid budget exceeded.");
        var grouped = new Dictionary<RoofChunkKey, List<RoofSample>>();
        var sampled = 0;
        try
        {
            foreach (var sample in samples)
            {
                var c = sample.ColorSrgb;
                if (!float.IsFinite(c.R) || !float.IsFinite(c.G) || !float.IsFinite(c.B) || !float.IsFinite(c.A))
                    throw new InvalidDataException("Nonfinite native roof color.");
                if (++sampled > RoofObservationPolicy.MaxTiles)
                    throw new InvalidDataException("Resolved roof sampling budget exceeded.");
                var key = Key(ownerId, sample.Tile);
                if (!grouped.TryGetValue(key, out var values))
                {
                    if (grouped.Count >= RoofObservationPolicy.MaxChunks)
                        throw new InvalidDataException("Resolved roof chunk staging budget exceeded.");
                    grouped[key] = values = [];
                    _resolutionBytes += 192;
                }
                if (values.Count >= 64 || values.Any(value => value.Tile == sample.Tile))
                    throw new InvalidDataException("Duplicate or excessive resolved roof tiles in chunk.");
                _resolutionBytes += 128;
                CheckBudget();
                values.Add(sample);
            }

            var grid = new RoofGridObservation(ownerId, tileSize, implicitRoof);
            if (!_grids.TryGetValue(ownerId, out var previousGrid) || grid != previousGrid)
            {
                var isNew = !_grids.ContainsKey(ownerId);
                // Empty grids have no chunk admission below: reserve both the
                // retained record and pending replacement before admitting it.
                Charge(64, isNew ? 64 : 0);
                if (isNew) RetainedBytes += 64;
                _grids[ownerId] = grid;
                _changes.GridReplacements.Add(grid);
            }
            _byGrid.TryGetValue(ownerId, out var oldKeys);
            // Remove missing chunks before admitting additions; only this affected grid
            // is visited. Grid removals have their own cascade operation below.
            foreach (var key in oldKeys?.ToArray() ?? [])
                if (!grouped.ContainsKey(key)) RemoveChunk(key);
            foreach (var (key, values) in grouped.OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Y))
            {
                var found = _chunks.TryGetValue(key, out var previous);
                if (found && SameSamples(previous.Value, values)) continue;
                var layers = new List<RoofLayer>();
                // GroupBy preserves native first-encounter color order and tile
                // order within each color. Consumers must preserve these lists.
                foreach (var group in values.GroupBy(value => value.ColorSrgb))
                {
                    var tiles = group.Select(value => value.Tile).ToArray();
                    // Geometry can survive a pure color change. Published arrays are
                    // immutable, so sharing them between old/new observations is safe.
                    var reusable = found ? previous.Value.Layers.FirstOrDefault(layer => layer.Tiles.SequenceEqual(tiles)) : null;
                    IReadOnlyList<RoofTile> owned = reusable?.Tiles ?? Array.AsReadOnly(tiles);
                    layers.Add(reusable?.ColorSrgb == group.Key ? reusable! : new(group.Key, owned));
                }
                var value = new RoofChunkObservation(ownerId, key.X, key.Y, layers.AsReadOnly());
                var bytes = checked(JsonSerializer.SerializeToUtf8Bytes(value, Json).Length + 256 + values.Count * 64);
                if (!found && _chunks.Count >= RoofObservationPolicy.MaxChunks
                    || TileCount - previous.Tiles + values.Count > RoofObservationPolicy.MaxTiles)
                    throw new InvalidDataException("Resolved roof retained inventory budget exceeded.");
                Charge(bytes, Math.Max(0, bytes - previous.Bytes));
                _chunks[key] = (value, bytes, values.Count);
                if (!_byGrid.TryGetValue(ownerId, out var keys)) _byGrid[ownerId] = keys = [];
                keys.Add(key);
                RetainedBytes += bytes - previous.Bytes;
                TileCount += values.Count - previous.Tiles;
                _changes.ChunkReplacements.Add(value);
                CheckBudget();
            }
        }
        finally { _resolutionBytes = 0; }
    }

    private static bool SameSamples(RoofChunkObservation previous, List<RoofSample> samples)
    {
        var groups = samples.GroupBy(sample => sample.ColorSrgb).ToArray();
        if (previous.Layers.Count != groups.Length) return false;
        for (var i = 0; i < groups.Length; i++)
            if (previous.Layers[i].ColorSrgb != groups[i].Key
                || !previous.Layers[i].Tiles.SequenceEqual(groups[i].Select(sample => sample.Tile))) return false;
        return true;
    }

    public void DeleteGrid(int ownerId)
    {
        if (!_grids.Remove(ownerId)) return;
        Charge(16);
        RetainedBytes -= 64;
        if (_byGrid.Remove(ownerId, out var keys))
            foreach (var key in keys)
            {
                var old = _chunks[key];
                _chunks.Remove(key); RetainedBytes -= old.Bytes; TileCount -= old.Tiles;
            }
        _changes.GridDeletes.Add(ownerId);
    }

    private void RemoveChunk(RoofChunkKey key)
    {
        var old = _chunks[key];
        Charge(32);
        RetainedBytes -= old.Bytes; TileCount -= old.Tiles;
        _chunks.Remove(key); _byGrid[key.OwnerId].Remove(key);
        _changes.ChunkDeletes.Add(key);
    }

    public RoofChanges Finish()
    {
        _changes.GridReplacements.Sort((a, b) => a.OwnerId.CompareTo(b.OwnerId));
        _changes.GridDeletes.Sort();
        _changes.ChunkReplacements.Sort((a, b) => CompareKeys(new(a.OwnerId, a.X, a.Y), new(b.OwnerId, b.X, b.Y)));
        _changes.ChunkDeletes.Sort(CompareKeys);
        return _changes;
    }

    public void Reset()
    {
        _grids.Clear(); _chunks.Clear(); _byGrid.Clear();
        _changes = new(true, [], [], [], []);
        RetainedBytes = 0; TileCount = 0;
        _pendingBytes = _resolutionBytes = _outsideBytes = 0;
    }

    internal static RoofChunkKey Key(int ownerId, RoofTile tile) => new(ownerId, tile.X >> 3, tile.Y >> 3);
    private static int CompareKeys(RoofChunkKey a, RoofChunkKey b) => a.OwnerId != b.OwnerId ? a.OwnerId.CompareTo(b.OwnerId)
        : a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y);
    private void Charge(long bytes, long retainedAddition = 0)
    {
        CheckBudget(checked(bytes + retainedAddition));
        _pendingBytes += bytes;
    }
    private void CheckBudget(long additional = 0)
    {
        var bytes = checked(RetainedBytes + _pendingBytes + _resolutionBytes + _outsideBytes + additional);
        if (bytes > _maxBytes) throw new InvalidDataException("Resolved roof live/staged byte budget exceeded.");
        PeakLiveStagedBytes = Math.Max(PeakLiveStagedBytes, bytes);
    }
}
