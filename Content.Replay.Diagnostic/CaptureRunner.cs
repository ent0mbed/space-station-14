using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Robust.Client.GameObjects;
using Robust.Client.GameStates;
using Robust.Client.Graphics;
using Robust.Client.Replays.Loading;
using Robust.Client.Replays.Playback;
using Robust.Client.ResourceManagement;
using Robust.Client.Timing;
using Robust.Shared;
using Robust.Shared.Audio.Components;
using Robust.Shared.Configuration;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Map.Components;
using Robust.Shared.Replays;

namespace Content.Replay.Diagnostic;

/// <summary>Temporary diagnostic seam. This JSON is deliberately not a published transport format.</summary>
public sealed partial class CaptureRunner
{
    [Dependency] private IReplayLoadManager _loader = default!;
    [Dependency] private IReplayPlaybackManager _playback = default!;
    [Dependency] private IClientEntityManager _entities = default!;
    [Dependency] private IClientGameStateManager _states = default!;
    [Dependency] private IClientGameTiming _timing = default!;
    [Dependency] private IResourceCache _resources = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IComponentFactory _factory = default!;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<int, string> _fingerprints = new();
    private readonly Dictionary<int, string> _audio = new();
    private readonly Dictionary<Texture, string> _texturePaths = new();
    private readonly HashSet<string> _rsiPaths = new();
    private readonly HashSet<string> _soundPaths = new();
    private readonly HashSet<string> _shaderPrototypes = new();
    private readonly Dictionary<string, int> _messageTypes = new();
    private readonly Dictionary<string, int> _audioStateTypes = new();
    private FileStream _output = default!;
    private double _projectionMs;
    private double _outputMs;
    private int _upserts;
    private int _layers;
    private int _sprites;
    private int _shaderLayers;
    private int _initialAudio;
    private int _initialEntities;
    private int _audioStarts;
    private int _audioChanges;
    private int _audioRemovals;
    private long _sourceClockOrigin;
    private long _projectionAllocatedBytes;

    public async Task RunAsync()
    {
        var startupMs = Program.Total.Elapsed.TotalMilliseconds;
        var startupAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - Program.AllocatedAtStart;
        _resources.OnRawTextureLoaded += OnTexture;
        _configuration.SetCVar(CVars.ReplayIgnoreErrors, false);
        _configuration.SetCVar(CVars.ReplayLoadedBlockWindow, 2);
        // A forward-only clip needs its initial checkpoint, never periodic scrubbing checkpoints.
        _configuration.SetCVar(CVars.CheckpointMinInterval, int.MaxValue);
        var native = new ReplayClipDiagnostics();
        var loadTimer = Stopwatch.StartNew();
        var reader = new ReplayFileReaderZip(ZipFile.OpenRead(Program.Input), ReplayConstants.ReplayZipFolder);
        var headerMetadata = _loader.LoadYamlMetadata(reader)!;
        var componentHash = ((Robust.Shared.Serialization.Markdown.Value.ValueDataNode)
            headerMetadata[ReplayConstants.MetaKeyComponentHash]).Value;
        int? roundId = headerMetadata.TryGet("roundId", out Robust.Shared.Serialization.Markdown.Value.ValueDataNode? round)
            && int.TryParse(round.Value, out var recordedRound) ? recordedRound : null;
        if (!Convert.ToHexString(_factory.GetHash(true)).Equals(componentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Matching reader network component hash mismatch.");
        var data = await ((ReplayLoadManager) _loader).LoadReplayClipAsync(reader,
            (_, _, _, _) => Task.CompletedTask, TimeSpan.FromSeconds(Program.Seconds), native);
        var loadMs = loadTimer.Elapsed.TotalMilliseconds;
        var loadedAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);
        var startTimer = Stopwatch.StartNew();
        await _loader.StartReplayAsync(data, (_, _, _, _) => Task.CompletedTask);
        var initializeMs = startTimer.Elapsed.TotalMilliseconds;
        var transformSystem = _entities.System<SharedTransformSystem>();
        transformSystem.OnGlobalMoveEvent += OnNativeMove;
        _entities.EntityDeleted += OnNativeDelete;
        var initializedAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);
        var checkpoint = data.Checkpoints[0];
        var sourceTickRate = (int) checkpoint.Cvars[CVars.NetTickrate.Name];
        if (sourceTickRate <= 0)
            throw new InvalidDataException("Invalid recorded clock tick rate.");
        // The same recorded checkpoint timebase and integer tick period used by native GetTime().
        _sourceClockOrigin = checked(checkpoint.TimeBase.Item1.Ticks
            + ((long) checkpoint.Tick.Value - checkpoint.TimeBase.Item2.Value)
            * (TimeSpan.TicksPerSecond / sourceTickRate));

        var resourceTimer = Stopwatch.StartNew();
        foreach (var (path, texture) in _resources.GetAllResources<TextureResource>())
            _texturePaths.TryAdd(texture.Texture, path.ToString());
        var resourceIndexMs = resourceTimer.Elapsed.TotalMilliseconds;

        using var output = File.Create(Path.Combine(Program.Output, "scene.jsonl"));
        _output = output;
        Write(new { kind = "diagnostic-header", schema = "ss14-diagnostic/0.2", gameBuild = Program.GameBuild,
            engineVersion = Program.EngineVersion, frameCount = data.Count,
            sourceStartTick = data.TickOffset.Value, timeUnit = "100ns", finalizedTransport = false,
            spriteRepresentation = "interned-definitions", sourceClockOrigin100ns = _sourceClockOrigin,
            sourceClock = new { timeBaseTime100ns = checkpoint.TimeBase.Item1.Ticks,
                timeBaseTick = checkpoint.TimeBase.Item2.Value, tickRate = sourceTickRate,
                tickPeriod100ns = TimeSpan.TicksPerSecond / sourceTickRate },
            scope = new { gameBuild = Program.GameBuild, forkId = Program.ForkId,
                bundleSha256 = Program.ResourceBundleSha256, roundId, sourceStartTick = data.TickOffset.Value },
            dictionaryLimits = new { spriteDefinitions = Program.MaxSpriteDefinitions,
                spriteDefinitionBytes = Program.MaxSpriteDefinitionBytes, resourceDefinitions = Program.MaxResourceDefinitions },
            loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic && assembly.GetName().Name is { } name
                    && (name.StartsWith("Content.", StringComparison.Ordinal)
                        || name.StartsWith("Robust.", StringComparison.Ordinal)))
                .OrderBy(assembly => assembly.GetName().Name)
                .Select(assembly => new { name = assembly.GetName().Name,
                    version = assembly.GetName().Version?.ToString(),
                    moduleVersionId = assembly.ManifestModule.ModuleVersionId }).ToArray() });

        var simulation = Stopwatch.StartNew();
        var loopAllocatedStart = GC.GetTotalAllocatedBytes(precise: true);
        var loopCollectionsStart = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        double applyMs = 0;
        for (var index = 0; index < data.Count; index++)
        {
            var stepStart = Stopwatch.GetTimestamp();
            var state = data.GetState(index);
            var messages = data.GetMessages(index);
            foreach (var message in messages.Messages)
                Increment(_messageTypes, message.GetType().FullName!);
            foreach (var entity in state.EntityStates.Value)
                foreach (var change in entity.ComponentChanges.Value)
                {
                    var type = change.State?.GetType().FullName;
                    if (type?.Contains("AudioComponent", StringComparison.Ordinal) == true)
                        Increment(_audioStateTypes, type);
                }

            if (index > 0)
            {
                // One-step scrubbing preserves effect messages. No checkpoint jumps or wall-clock waits.
                _playback.SetIndex(index);
                if (data.CurrentIndex != index)
                    throw new InvalidOperationException("Native replay failed to advance exactly one state.");
                _states.MergeImplicitData();
                _timing.CurTick += 1;
                var delta = (float) (data.ReplayTime[index] - data.ReplayTime[index - 1]).TotalSeconds;
                _entities.TickUpdate(delta, noPredictions: true);
                _entities.FrameUpdate(delta);
            }
            else
                _entities.FrameUpdate(0);
            applyMs += Stopwatch.GetElapsedTime(stepStart).TotalMilliseconds;

            var projectionStart = Stopwatch.GetTimestamp();
            var projectionAllocatedStart = GC.GetTotalAllocatedBytes(precise: false);
            List<object> upserts = new();
            List<int> deletes = new();
            List<object> audioEvents = new();
            _captureSequence = index;
            BeginProjection(state, deletes, audioEvents);
            if (index == 0)
            {
                var ordered = new List<(EntityUid Uid, TransformComponent Transform, MetaDataComponent Metadata)>();
                var query = _entities.EntityQueryEnumerator<TransformComponent, MetaDataComponent>();
                while (query.MoveNext(out var uid, out var transform, out var metadata))
                {
                    if (metadata.NetEntity.IsClientSide())
                        continue;
                    if (ordered.Count >= 250_000)
                        throw new InvalidDataException("Diagnostic entity budget exceeded.");
                    ordered.Add((uid, transform, metadata));
                }
                ordered.Sort((a, b) => a.Metadata.NetEntity.Id.CompareTo(b.Metadata.NetEntity.Id));
                foreach (var entity in ordered)
                    ProjectNative(entity.Uid, upserts, audioEvents, true);
            }
            else
            {
                // Candidate-driven projection avoids serializing the entire world every tick.
                // This seam does not yet claim complete tracking of unrelated client-only sprite mutations.
                foreach (var entity in state.EntityStates.Value)
                {
                    if (!_entities.TryGetEntity(entity.NetEntity, out var uid)
                        || !_entities.TryGetComponent<TransformComponent>(uid, out var transform)
                        || !_entities.TryGetComponent<MetaDataComponent>(uid, out var metadata))
                        continue;
                    ProjectNative(uid.Value, upserts, audioEvents, false);
                }
            }
            ProjectMoved(upserts, audioEvents, index == 0);
            if (index == 0)
                _initialEntities = upserts.Count;
            _projectionMs += Stopwatch.GetElapsedTime(projectionStart).TotalMilliseconds;
            _projectionAllocatedBytes += GC.GetTotalAllocatedBytes(precise: false) - projectionAllocatedStart;
            _upserts += upserts.Count;
            WriteResourceDefinitions();
            WriteSpriteDefinitions();
            // The initial native world is large. Keep JSONL records bounded without dropping entities.
            var chunkCount = index == 0 ? (upserts.Count + 999) / 1000 : 1;
            for (var chunk = 0; chunk < chunkCount; chunk++)
                Write(new { kind = index == 0 ? "snapshot" : "delta", sequence = index,
                    chunkIndex = chunk, chunkCount,
                    sourceTick = state.ToSequence.Value, sourceTime100ns = data.ReplayTime[index].Ticks,
                    sourceServerTime100ns = checked(_sourceClockOrigin + data.ReplayTime[index].Ticks),
                    upserts = index == 0 ? upserts.Skip(chunk * 1000).Take(1000) : upserts,
                    deletes, audioEvents = chunk == 0 ? audioEvents : [] });
        }
        simulation.Stop();
        var loopAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - loopAllocatedStart;
        var loopCollections = Enumerable.Range(0, 3).Select(gen => GC.CollectionCount(gen) - loopCollectionsStart[gen]).ToArray();

        var inventoryStart = Stopwatch.GetTimestamp();
        var rsi = _resources.GetAllResources<RSIResource>()
            .Where(pair => _rsiPaths.Contains(pair.Key.ToString()))
            .Select(pair => new { path = pair.Key.ToString(), size = new[] { pair.Value.RSI.Size.X, pair.Value.RSI.Size.Y },
                states = pair.Value.RSI.Select(state => new { id = state.StateId.Name,
                    directions = state.RsiDirections switch
                    {
                        RsiDirectionType.Dir1 => 1,
                        RsiDirectionType.Dir4 => 4,
                        RsiDirectionType.Dir8 => 8,
                        _ => throw new InvalidDataException("Unsupported RSI direction count.")
                    }, delaysSeconds = state.GetDelays() }).ToArray() }).ToArray();
        var inventory = new { kind = "resources", rsi, images = _texturePaths.Values.Distinct().Order().ToArray(),
            sounds = _soundPaths.Order().ToArray(), shaderPrototypes = _shaderPrototypes.Order().ToArray(),
            missing = new[] { "runtime-shader-parameters", "shader-source-include-closure", "tile-chunks",
                "complete-client-only-sprite-dirty-tracking", "image-bodies-and-atlas-crops",
                "client-recording-audio-message-payloads" } };
        var inventoryMs = Stopwatch.GetElapsedTime(inventoryStart).TotalMilliseconds;
        foreach (var image in inventory.images)
            EnsureResource("image", image, BodyMetadata);
        WriteResourceDefinitions();
        Write(inventory);
        output.Flush();

        var summary = new { schema = "ss14-diagnostic-summary/0.2", gameBuild = Program.GameBuild,
            engineVersion = Program.EngineVersion, frames = data.Count, blocksRead = native.BlocksRead,
            playbackBlocksRead = native.PlaybackBlocksRead,
            declaredDecodedBytes = native.DecodedBytes, simulatedSeconds = data.ReplayTime[^1].TotalSeconds,
            initialEntities = _initialEntities, entitiesAtEnd = _fingerprints.Count, initialSprites = _sprites, initialLayers = _layers,
            shaderLayers = _shaderLayers, totalUpserts = _upserts, audio = new { initial = _initialAudio,
                starts = _audioStarts, changes = _audioChanges, removals = _audioRemovals,
                nativeStateTypes = _audioStateTypes, messageTypes = _messageTypes },
            stagesMilliseconds = new { startup = startupMs, resourceVerification = Program.ResourceVerificationMilliseconds,
                loadAndCheckpoint = loadMs, zipZstdNativeRead = native.ReadMilliseconds,
                playbackZipZstdNativeRead = native.PlaybackReadMilliseconds,
                entityInitialization = initializeMs, resourceIndex = resourceIndexMs,
                stateMessagesTickAndPresentation = applyMs, projection = _projectionMs,
                resourceInventory = inventoryMs, output = _outputMs, clipLoop = simulation.Elapsed.TotalMilliseconds,
                total = Program.Total.Elapsed.TotalMilliseconds },
            clipLoopSpeed = data.ReplayTime[^1].TotalSeconds / simulation.Elapsed.TotalSeconds,
            interning = new { spriteCandidates = _spriteCandidates, spriteDefinitions = _spriteDefinitions.Count,
                spriteDefinitionBytes = _spriteDefinitionBytes, previousSpriteReuses = _previousSpriteReuses,
                sharedSpriteReuses = _sharedSpriteReuses, resourceDefinitions = _resourceDefinitions.Count,
                uniqueRsiObjectsReferenced = _rsiResourceIds.Count },
            parentClosure = new { ancestorCandidates = _ancestorCandidates, ancestorAdditions = _ancestorAdditions,
                clientAncestors = _clientAncestors, movedCandidates = _movedCandidates,
                observedNativeDeletions = _observedDeletions, maximumDepth = _maxParentDepth,
                firstTransitionAncestors = _firstTransitionAncestors },
            allocationsBytes = new { startup = startupAllocatedBytes,
                loadAndCheckpoint = loadedAllocatedBytes - Program.AllocatedAtStart - startupAllocatedBytes,
                entityInitialization = initializedAllocatedBytes - loadedAllocatedBytes,
                clipLoop = loopAllocatedBytes, projection = _projectionAllocatedBytes,
                total = GC.GetTotalAllocatedBytes(precise: true) - Program.AllocatedAtStart },
            gcCollections = new { clipLoop = loopCollections,
                total = Enumerable.Range(0, 3).Select(gen => GC.CollectionCount(gen) - Program.CollectionsAtStart[gen]).ToArray() },
            managedBytes = GC.GetTotalMemory(false), peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            outputBytes = output.Length, diagnosticOnly = true };
        File.WriteAllBytes(Path.Combine(Program.Output, "summary.json"), JsonSerializer.SerializeToUtf8Bytes(summary, Json));
        Console.WriteLine(JsonSerializer.Serialize(summary, Json));
        _resources.OnRawTextureLoaded -= OnTexture;
        transformSystem.OnGlobalMoveEvent -= OnNativeMove;
        _entities.EntityDeleted -= OnNativeDelete;
        _playback.StopReplay();
    }

    private void Capture(EntityUid uid, TransformComponent transform, MetaDataComponent metadata,
        List<object> upserts, List<object> audioEvents, bool initial)
    {
        var id = metadata.NetEntity.Id;
        int? spriteId = null;
        if (_entities.TryGetComponent<SpriteComponent>(uid, out var component))
            spriteId = CaptureSprite(id, component, initial);
        else
            _previousSprites.Remove(id);
        var record = new { id, parentId = transform.ParentUid == EntityUid.Invalid ? (int?) null
                : _entities.GetNetEntity(transform.ParentUid).Id,
            prototype = metadata.EntityPrototype?.ID,
            transform = new { x = transform.LocalPosition.X, y = transform.LocalPosition.Y, rotation = transform.LocalRotation.Theta },
            transform.Anchored, transform.NoLocalRotation,
            isMap = _entities.HasComponent<MapComponent>(uid), isGrid = _entities.HasComponent<MapGridComponent>(uid), spriteId };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(record, Json)));
        if (!_fingerprints.TryGetValue(id, out var previous) || previous != fingerprint)
        {
            if (!_fingerprints.ContainsKey(id) && _fingerprints.Count >= 250_000)
                throw new InvalidDataException("Diagnostic entity budget exceeded.");
            _fingerprints[id] = fingerprint;
            upserts.Add(record);
        }
        if (_entities.TryGetComponent<AudioComponent>(uid, out var audio))
        {
            var audioState = audio.State;
            _soundPaths.Add(audio.FileName);
            var resourceId = EnsureResource("sound", audio.FileName, BodyMetadata);
            var value = new { id, audio.FileName, startTime100ns = audio.AudioStart.Ticks,
                startReplayTime100ns = checked(audio.AudioStart.Ticks - _sourceClockOrigin), resourceId,
                state = EnumName(audioState), audio.Global, flags = (byte) audio.Flags,
                parameters = new { volumeDb = float.IsFinite(audio.Params.Volume) ? (float?) audio.Params.Volume : null,
                    muted = float.IsNegativeInfinity(audio.Params.Volume), audio.Params.Pitch, audio.Params.Loop,
                    audio.Params.MaxDistance, audio.Params.ReferenceDistance, audio.Params.RolloffFactor,
                    audio.Params.PlayOffsetSeconds, audio.Params.Variation } };
            var encoded = JsonSerializer.Serialize(value, Json);
            if (!_audio.TryGetValue(id, out var previousAudio))
            {
                if (initial) _initialAudio++; else _audioStarts++;
                audioEvents.Add(new { kind = initial ? "initial" : "start", value });
            }
            else if (previousAudio != encoded)
            {
                _audioChanges++;
                audioEvents.Add(new { kind = "change", value });
            }
            _audio[id] = encoded;
        }
        else if (_audio.Remove(id))
        {
            _audioRemovals++;
            audioEvents.Add(new { kind = "remove", id });
        }
    }

    private void OnTexture(TextureLoadedEventArgs args) => _texturePaths[args.Resource.Texture] = args.Path.ToString();
    private string? ResolveTexture(Texture? texture)
    {
        while (texture is AtlasTexture atlas)
            texture = atlas.SourceTexture;
        return texture != null && _texturePaths.TryGetValue(texture, out var path) ? path : null;
    }
    private static object Vector(Vector2 value) => new { value.X, value.Y };
    private static object Color(Robust.Shared.Maths.Color value) => new { value.R, value.G, value.B, value.A };
    private static void Increment(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
    private void Write<T>(T record)
    {
        var start = Stopwatch.GetTimestamp();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        CheckOutputBudget(bytes.Length);
        _output.Write(bytes);
        _output.WriteByte((byte) '\n');
        _outputMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private void CheckOutputBudget(int length)
    {
        if (length > 64 * 1024 * 1024 || _output.Position + length + 1 > 1024L * 1024 * 1024)
            throw new InvalidDataException($"Diagnostic output budget exceeded ({length} byte record).");
    }
}
