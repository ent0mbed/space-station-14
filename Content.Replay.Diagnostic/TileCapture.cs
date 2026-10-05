using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.Replay.Diagnostic;

public sealed partial class CaptureRunner
{
    private const string TileSchema = "ss14-diagnostic-tiles/0.1";
    private const int ExportChunkSize = 16;
    private const int TilePixels = 32;
    private const int MaxTileGrids = 4096;
    private const int MaxTileDefinitions = 4096;
    private const int MaxTileChunks = 32768;
    private const int MaxNonemptyTiles = 500_000;
    private const long MaxTileOutputBytes = 256L * 1024 * 1024;

    [Dependency] private ITileDefinitionManager _tileDefinitions = default!;
    private FileStream? _tileOutput;
    private SharedMapSystem _tileMap = default!;
    private DiagnosticTileSystem _tileObserver = default!;
    private readonly Dictionary<int, TileGrid> _tileGrids = new();
    private readonly HashSet<int> _tileDefinitionIds = new();
    private readonly Dictionary<string, TileImage> _tileImages = new(StringComparer.Ordinal);
    private readonly HashSet<(EntityUid Grid, Vector2i Chunk)> _dirtyTileChunks = new();
    private readonly HashSet<int> _activeTileGrids = new();
    private bool _tileDirtyOverflow;
    private int _tileFrames;
    private int _tileCount;
    private int _tileChunkCount;
    private int _initialTileGrids;
    private int _initialTileCount;
    private int _initialTileChunks;
    private int _tileGridCreations;
    private int _tileGridDeletions;
    private int _tileDeltaReplacements;
    private int _tileChunkRemovals;
    private long _nativeTileChanges;
    private double _tileCaptureMs;
    private double _tileOutputMs;
    private double _tileValidationMs;

    private sealed class TileGrid(ushort tileSize)
    {
        public ushort TileSize = tileSize;
        public int FilledTiles;
        public readonly Dictionary<Vector2i, Tile[]> Chunks = new();
    }

    private sealed record TileImage(string Path, int Width, int Height);
    private sealed record TileChunkRecord(int GridId, int X, int Y, string Action, List<int[]> Tiles);

    private void StartTiles(FileStream output, int frameCount, uint sourceStartTick, int? roundId)
    {
        _tileOutput = output;
        _tileMap = _entities.System<SharedMapSystem>();
        _tileObserver = _entities.System<DiagnosticTileSystem>();
        _tileObserver.TileChanged += OnNativeTileChanged;
        WriteTiles(new { kind = "tile-header", schema = TileSchema, sceneSchema = "ss14-diagnostic/0.2",
            capability = "grid-tiles", finalizedTransport = false, frameCount, timeUnit = "100ns",
            sourceClockOrigin100ns = _sourceClockOrigin, gameBuild = Program.GameBuild,
            engineVersion = Program.EngineVersion,
            scope = new { gameBuild = Program.GameBuild, forkId = Program.ForkId,
                bundleSha256 = Program.ResourceBundleSha256, roundId, sourceStartTick },
            coordinateSpace = "grid-local-tile-indices", exportChunkSize = ExportChunkSize,
            pixelsPerMeter = TilePixels,
            tileTuple = new[] { "localX", "localY", "typeId", "flags", "variant", "rotationMirroring" },
            limits = new { grids = MaxTileGrids, definitions = MaxTileDefinitions,
                retainedChunks = MaxTileChunks, dirtyChunksPerFrame = MaxTileChunks,
                nonemptyTiles = MaxNonemptyTiles, outputBytes = MaxTileOutputBytes },
            errorTileImage = ReadTileImage("/Textures/noTile.png") });
        EnsureTileDefinition(0);
    }

    private void OnNativeTileChanged(ref TileChangedEvent ev)
    {
        _nativeTileChanges += ev.Changes.Length;
        foreach (var change in ev.Changes)
        {
            var key = (ev.Entity.Owner, ExportChunk(change.GridIndices));
            if (_dirtyTileChunks.Count >= MaxTileChunks && !_dirtyTileChunks.Contains(key))
            {
                _tileDirtyOverflow = true;
                return;
            }
            _dirtyTileChunks.Add(key);
        }
    }

    private static int FloorChunk(int coordinate) => (int) Math.Floor(coordinate / (double) ExportChunkSize);
    private static Vector2i ExportChunk(Vector2i coordinate) => new(FloorChunk(coordinate.X), FloorChunk(coordinate.Y));
    private static int TileIndex(Vector2i coordinate, Vector2i chunk) =>
        checked((coordinate.X - chunk.X * ExportChunkSize) * ExportChunkSize + coordinate.Y - chunk.Y * ExportChunkSize);

    private void CaptureTiles(int sequence, uint sourceTick, long sourceTime100ns)
    {
        if (_tileOutput == null) return;
        var start = Stopwatch.GetTimestamp();
        if (_tileDirtyOverflow)
            throw new InvalidDataException("Diagnostic dirty tile-chunk budget exceeded.");
        List<object> grids = new();
        List<int> deleted = new();
        List<TileChunkRecord> chunks = new();
        List<(EntityUid Uid, MapGridComponent Grid, int Id)> ordered = new();
        _activeTileGrids.Clear();
        var query = _entities.EntityQueryEnumerator<MapGridComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out var grid, out var metadata))
        {
            if (metadata.EntityLifeStage < EntityLifeStage.Initialized
                || metadata.EntityLifeStage >= EntityLifeStage.Terminating)
                continue;
            var id = metadata.NetEntity.Id;
            if (metadata.NetEntity.IsClientSide() && !_fingerprints.ContainsKey(id))
                continue;
            if (!metadata.NetEntity.Valid || !_fingerprints.ContainsKey(id))
                throw new InvalidDataException($"Native tile grid {id} is absent from the scene graph.");
            if (ordered.Count >= MaxTileGrids)
                throw new InvalidDataException("Diagnostic tile-grid budget exceeded.");
            ordered.Add((uid, grid, id));
        }
        ordered.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (var (uid, native, id) in ordered)
        {
            _activeTileGrids.Add(id);
            if (native.TileSize == 0)
                throw new InvalidDataException("Native tile size is zero.");
            if (!_tileGrids.TryGetValue(id, out var projected))
            {
                _tileGrids[id] = projected = new(native.TileSize);
                grids.Add(new { id, tileSize = native.TileSize });
                if (sequence > 0) _tileGridCreations++;
                foreach (var tile in _tileMap.GetAllTiles(uid, native))
                {
                    var key = ExportChunk(tile.GridIndices);
                    if (!projected.Chunks.TryGetValue(key, out var data))
                    {
                        if (++_tileChunkCount > MaxTileChunks)
                            throw new InvalidDataException("Diagnostic retained tile-chunk budget exceeded.");
                        projected.Chunks[key] = data = new Tile[ExportChunkSize * ExportChunkSize];
                    }
                    data[TileIndex(tile.GridIndices, key)] = tile.Tile;
                    projected.FilledTiles++;
                    if (++_tileCount > MaxNonemptyTiles)
                        throw new InvalidDataException("Diagnostic nonempty tile budget exceeded.");
                }
                foreach (var (key, data) in projected.Chunks.OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Y))
                {
                    chunks.Add(ChunkRecord(id, key, data));
                    if (sequence > 0) _tileDeltaReplacements++;
                }
            }
            else
            {
                if (native.TileSize != projected.TileSize)
                {
                    projected.TileSize = native.TileSize;
                    grids.Add(new { id, tileSize = native.TileSize });
                }
                foreach (var change in _dirtyTileChunks.Where(change => change.Grid == uid)
                             .OrderBy(change => change.Chunk.X).ThenBy(change => change.Chunk.Y))
                {
                    var key = change.Chunk;
                    var next = new Tile[ExportChunkSize * ExportChunkSize];
                    var filled = 0;
                    for (var x = 0; x < ExportChunkSize; x++)
                    for (var y = 0; y < ExportChunkSize; y++)
                    {
                        _tileMap.TryGetTile(native,
                            new Vector2i(checked(key.X * ExportChunkSize + x), checked(key.Y * ExportChunkSize + y)), out var tile);
                        next[x * ExportChunkSize + y] = tile;
                        if (!tile.IsEmpty) filled++;
                    }
                    projected.Chunks.TryGetValue(key, out var previous);
                    if (previous != null && previous.AsSpan().SequenceEqual(next)) continue;
                    if (previous == null && filled == 0) continue;
                    var previousFilled = previous?.Count(tile => !tile.IsEmpty) ?? 0;
                    projected.FilledTiles += filled - previousFilled;
                    _tileCount += filled - previousFilled;
                    if (_tileCount > MaxNonemptyTiles)
                        throw new InvalidDataException("Diagnostic nonempty tile budget exceeded.");
                    if (filled == 0)
                    {
                        projected.Chunks.Remove(key);
                        _tileChunkCount--;
                        chunks.Add(new(id, key.X, key.Y, "remove", new()));
                        _tileChunkRemovals++;
                    }
                    else
                    {
                        if (previous == null && ++_tileChunkCount > MaxTileChunks)
                            throw new InvalidDataException("Diagnostic retained tile-chunk budget exceeded.");
                        projected.Chunks[key] = next;
                        chunks.Add(ChunkRecord(id, key, next));
                        _tileDeltaReplacements++;
                    }
                }
            }
            if (projected.FilledTiles != _tileMap.GetFilledTileCount((uid, native)))
                throw new InvalidDataException($"Native/projected tile count differs on grid {id}.");
        }
        foreach (var id in _tileGrids.Keys.Where(id => !_activeTileGrids.Contains(id)).Order().ToArray())
        {
            var old = _tileGrids[id];
            _tileCount -= old.FilledTiles;
            _tileChunkCount -= old.Chunks.Count;
            _tileGrids.Remove(id);
            deleted.Add(id);
            _tileGridDeletions++;
        }
        _dirtyTileChunks.Clear();
        if (sequence == 0)
        {
            _initialTileGrids = _tileGrids.Count;
            _initialTileCount = _tileCount;
            _initialTileChunks = _tileChunkCount;
        }
        var chunkCount = Math.Max(1, (chunks.Count + 63) / 64);
        for (var chunk = 0; chunk < chunkCount; chunk++)
            WriteTiles(new { kind = "tile-frame", sequence, chunkIndex = chunk, chunkCount,
                snapshot = sequence == 0, sourceTick, sourceTime100ns,
                sourceServerTime100ns = checked(_sourceClockOrigin + sourceTime100ns),
                gridUpserts = chunk == 0 ? grids : [], gridDeletes = chunk == 0 ? deleted : [],
                chunks = chunks.Skip(chunk * 64).Take(64) });
        _tileFrames++;
        _tileCaptureMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private TileChunkRecord ChunkRecord(int gridId, Vector2i chunk, Tile[] data)
    {
        List<int[]> tiles = new();
        for (var x = 0; x < ExportChunkSize; x++)
        for (var y = 0; y < ExportChunkSize; y++)
        {
            var tile = data[x * ExportChunkSize + y];
            if (tile.IsEmpty) continue;
            var definition = _tileDefinitions[tile.TypeId];
            if (definition.Sprite == null)
                throw new InvalidDataException($"Nonempty tile {tile.TypeId} requires an engine fallback image outside this bounded bundle export.");
            if (tile.Variant >= definition.Variants || tile.RotationMirroring > 7)
                throw new InvalidDataException($"Native tile {tile.TypeId} has an unsupported variant or rotation/mirroring.");
            EnsureTileDefinition(tile.TypeId);
            tiles.Add([x, y, tile.TypeId, tile.Flags, tile.Variant, tile.RotationMirroring]);
        }
        return new(gridId, chunk.X, chunk.Y, "replace", tiles);
    }

    private TileImage ReadTileImage(string path)
    {
        if (_tileImages.TryGetValue(path, out var cached)) return cached;
        using var stream = _resources.ContentFileRead(path);
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadInt32BigEndian(header[8..12]) != 13
            || !header[12..16].SequenceEqual("IHDR"u8))
            throw new InvalidDataException($"Tile resource {path} is not the expected PNG.");
        var width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768)
            throw new InvalidDataException($"Tile resource {path} exceeds image dimensions.");
        var image = new TileImage(path, width, height);
        _tileImages.Add(path, image);
        return image;
    }

    private void EnsureTileDefinition(int typeId)
    {
        if (_tileDefinitionIds.Contains(typeId)) return;
        if (_tileDefinitionIds.Count >= MaxTileDefinitions
            || !_tileDefinitions.TryGetDefinition(typeId, out var definition) || definition.TileId != typeId)
            throw new InvalidDataException($"Native tile definition {typeId} is unavailable or exceeds the budget.");
        var image = definition.Sprite is { } sprite ? ReadTileImage(sprite.ToString()) : null;
        if (image != null && (image.Width != TilePixels * definition.Variants || image.Height != TilePixels))
            throw new InvalidDataException($"Native tile strip {image.Path} does not match its declared variants.");
        var edges = definition.EdgeSprites.OrderBy(pair => (int) pair.Key).Select(pair =>
        {
            var edge = ReadTileImage(pair.Value.ToString());
            if (edge.Width != TilePixels || edge.Height != TilePixels)
                throw new InvalidDataException($"Native tile edge {edge.Path} is not 32x32.");
            var rotation = pair.Key switch
            {
                Direction.NorthEast or Direction.East => -90,
                Direction.NorthWest or Direction.North => -180,
                Direction.SouthWest or Direction.West => -270,
                _ => 0
            };
            return new { direction = pair.Key.ToString(), image = edge, atlasRotationDegrees = rotation };
        }).ToArray();
        WriteTiles(new { kind = "tile-definition", typeId, prototype = definition.ID,
            variants = definition.Variants, allowRotationMirror = definition.AllowRotationMirror,
            image, variantPixelRects = image == null ? Array.Empty<int[]>()
                : Enumerable.Range(0, definition.Variants).Select(variant => new[] { variant * TilePixels, 0, TilePixels, TilePixels }).ToArray(),
            edgeSprites = edges, edgeSpritePriority = definition.EdgeSpritePriority });
        _tileDefinitionIds.Add(typeId);
    }

    private object FinishTiles()
    {
        if (_tileOutput == null) return new { enabled = false, schema = TileSchema, file = (string?) null };
        var start = Stopwatch.GetTimestamp();
        var checkedTiles = 0;
        var variants = new HashSet<byte>();
        var rotated = 0;
        var flagged = 0;
        foreach (var (id, projected) in _tileGrids)
        {
            if (!_entities.TryGetEntity(new NetEntity(id), out var uid)
                || !_entities.TryGetComponent<MapGridComponent>(uid, out var grid))
                throw new InvalidDataException($"Final native tile grid {id} is unavailable.");
            var count = 0;
            foreach (var tile in _tileMap.GetAllTiles(uid.Value, grid))
            {
                var key = ExportChunk(tile.GridIndices);
                if (!projected.Chunks.TryGetValue(key, out var data) || data[TileIndex(tile.GridIndices, key)] != tile.Tile)
                    throw new InvalidDataException($"Final native tile differs from projected grid {id}.");
                count++;
                variants.Add(tile.Tile.Variant);
                if (tile.Tile.RotationMirroring != 0) rotated++;
                if (tile.Tile.Flags != 0) flagged++;
            }
            if (count != projected.FilledTiles)
                throw new InvalidDataException($"Final native/projected tile count differs on grid {id}.");
            checkedTiles += count;
        }
        if (checkedTiles != _tileCount)
            throw new InvalidDataException("Final native/projected total tile count differs.");
        _tileValidationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _tileOutput.Flush();
        _tileObserver.TileChanged -= OnNativeTileChanged;
        return new { enabled = true, schema = TileSchema, file = "tiles.jsonl", sceneSchema = "ss14-diagnostic/0.2",
            frames = _tileFrames, initialGrids = _initialTileGrids, finalGrids = _tileGrids.Count,
            initialNonemptyTiles = _initialTileCount, finalNonemptyTiles = _tileCount,
            initialChunks = _initialTileChunks, finalChunks = _tileChunkCount,
            gridCreations = _tileGridCreations, gridDeletions = _tileGridDeletions,
            deltaChunkReplacements = _tileDeltaReplacements, chunkRemovals = _tileChunkRemovals,
            nativeTileChangeEntries = _nativeTileChanges, definitions = _tileDefinitionIds.Count, imageResources = _tileImages.Count,
            nativeFinalTilesChecked = checkedTiles, variantValues = variants.Select(value => (int) value).Order().ToArray(),
            finalRotatedOrMirroredTiles = rotated, finalFlaggedTiles = flagged,
            captureMilliseconds = _tileCaptureMs, outputMilliseconds = _tileOutputMs,
            finalValidationMilliseconds = _tileValidationMs, outputBytes = _tileOutput.Length };
    }

    private void WriteTiles<T>(T record)
    {
        var start = Stopwatch.GetTimestamp();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (bytes.Length > 64 * 1024 * 1024 || _tileOutput!.Position + bytes.Length + 1 > MaxTileOutputBytes)
            throw new InvalidDataException("Diagnostic tile output budget exceeded.");
        _tileOutput.Write(bytes);
        _tileOutput.WriteByte((byte) '\n');
        _tileOutputMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
}
