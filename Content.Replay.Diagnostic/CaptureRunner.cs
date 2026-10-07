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
using Robust.Shared.Map;
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
    [Dependency] private ITileDefinitionManager _tileDefinitions = default!;

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
    private SharedTransformSystem? _captureTransformSystem;

    public async Task RunAsync()
    {
        _captureTransformSystem = null;
        ResetAnimationEligibility();
        _entities.BeforeEntityFlush += ResetAnimationEligibility;
        _playback.ReplayCheckpointReset += ResetAnimationEligibility;
        _resources.OnRawTextureLoaded += OnTexture;
        try
        {
            await CaptureAsync();
        }
        finally
        {
            _viewerMetadataSystem?.ClearRenamed();
            _viewerMetadataSystem = null;
            _resources.OnRawTextureLoaded -= OnTexture;
            _shaderBundle?.Dispose();
            if (_captureTransformSystem is { } transformSystem)
                transformSystem.OnGlobalMoveEvent -= OnNativeMove;
            _captureTransformSystem = null;
            _entities.EntityDeleted -= OnNativeDelete;
            _entities.BeforeEntityFlush -= ResetAnimationEligibility;
            _playback.ReplayCheckpointReset -= ResetAnimationEligibility;
            ResetAnimationEligibility();
            if (_playback.Replay != null)
                _playback.StopReplay();
        }
    }

    private async Task CaptureAsync()
    {
        // EntryPoint invokes us synchronously from FramePostEngine. Pinned RSI
        // preload workers have joined; live sprite/RSI mutation and all inspection
        // must remain serial on this thread. Atomic stamps are not synchronization.
        var ownerThread = Environment.CurrentManagedThreadId;
        var startupMs = Program.Total.Elapsed.TotalMilliseconds;
        var startupAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - Program.AllocatedAtStart;
        _configuration.SetCVar(CVars.ReplayIgnoreErrors, false);
        _configuration.SetCVar(CVars.ReplayLoadedBlockWindow, 2);
        // A forward-only clip needs its initial checkpoint, never periodic scrubbing checkpoints.
        _configuration.SetCVar(CVars.CheckpointMinInterval, int.MaxValue);
        var native = new ReplayClipDiagnostics(Program.Profile);
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
        AnimationEligibility.RequireOwnerThread(ownerThread);
        var loadMs = loadTimer.Elapsed.TotalMilliseconds;
        var loadedAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);
        var startTimer = Stopwatch.StartNew();
        await _loader.StartReplayAsync(data, (_, _, _, _) => Task.CompletedTask);
        AnimationEligibility.RequireOwnerThread(ownerThread);
        var initializeMs = startTimer.Elapsed.TotalMilliseconds;
        _captureTransformSystem = _entities.System<SharedTransformSystem>();
        _captureTransformSystem.OnGlobalMoveEvent += OnNativeMove;
        _entities.EntityDeleted += OnNativeDelete;
        // Native replay startup initializes the system collection; the observer
        // registered during that initialization can now be looked up safely.
        _viewerMetadataSystem = _entities.System<ViewerMetadataSystem>();
        _viewerMetadataSystem!.ClearRenamed();
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

        // A failed overwrite must not retain the previous run's success/capability metadata.
        File.Delete(Path.Combine(Program.Output, "summary.json"));
        using var output = File.Create(Path.Combine(Program.Output, "scene.jsonl"));
        using var tileCapture = Program.CaptureTiles
            ? new NativeTileCapture(_entities, _resources, _tileDefinitions, _configuration, _fingerprints.ContainsKey, _sourceClockOrigin)
            : null;
        _output = output;
        var header = new { kind = "diagnostic-header", schema = Program.SceneSchema,
            clipProfile = Program.Profile.Name, requestedSeconds = Program.Seconds, clipLimits = Program.ClipLimits,
            requiredCapabilities = Program.RequiredCapabilities, gameBuild = Program.GameBuild,
            engineVersion = Program.EngineVersion, frameCount = data.Count,
            sourceStartTick = data.TickOffset.Value, timeUnit = "100ns", finalizedTransport = false,
            spriteRepresentation = "interned-definitions", sourceClockOrigin100ns = _sourceClockOrigin,
            sourceClock = new { timeBaseTime100ns = checkpoint.TimeBase.Item1.Ticks,
                timeBaseTick = checkpoint.TimeBase.Item2.Value, tickRate = sourceTickRate,
                tickPeriod100ns = TimeSpan.TicksPerSecond / sourceTickRate },
            scope = new { gameBuild = Program.GameBuild, forkId = Program.ForkId,
                bundleSha256 = Program.ResourceBundleSha256, roundId, sourceStartTick = data.TickOffset.Value },
            presentationLimits = new { owners = Program.MaxPresentationOwners, layersPerOwner = Program.MaxPresentationLayersPerOwner, retainedBytes = Program.MaxPresentationRetainedBytes },
            viewerMetadataLimits = new { players = ViewerMetadataPolicy.MaxPlayers,
                stations = ViewerMetadataPolicy.MaxStations, gridMemberships = ViewerMetadataPolicy.MaxGridMemberships,
                nameCharacters = ViewerMetadataPolicy.MaxNamesCharacters, chatTextCharacters = ViewerMetadataPolicy.MaxChatTextCharacters,
                chatEventsPerFrame = ViewerMetadataPolicy.MaxChatEventsPerFrame, chatEvents = ViewerMetadataPolicy.MaxChatEvents,
                retainedBytes = ViewerMetadataPolicy.MaxRetainedBytes, frameBytes = ViewerMetadataPolicy.MaxFrameBytes },
            dictionaryLimits = new { spriteDefinitions = Program.MaxSpriteDefinitions,
                spriteDefinitionBytes = Program.MaxSpriteDefinitionBytes, resourceDefinitions = Program.MaxResourceDefinitions,
                shaderParameterNameCharacters = Program.MaxShaderParameterNameCharacters,
                materialDefinitions = Program.MaxMaterialDefinitions, materialDefinitionBytes = Program.MaxMaterialDefinitionBytes,
                shaderSourceDefinitions = Program.MaxShaderSourceDefinitions, shaderSourceDefinitionBytes = Program.MaxShaderSourceDefinitionBytes,
                shaderParameters = Program.MaxShaderParameters, shaderIncludes = Program.MaxShaderIncludes,
                shaderSourceFileBytes = Program.MaxShaderSourceFileBytes, shaderClosureBytes = Program.MaxShaderClosureBytes },
            loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic && assembly.GetName().Name is { } name
                    && (name.StartsWith("Content.", StringComparison.Ordinal)
                        || name.StartsWith("Robust.", StringComparison.Ordinal)))
                .OrderBy(assembly => assembly.GetName().Name)
                .Select(assembly => new { name = assembly.GetName().Name,
                    version = assembly.GetName().Version?.ToString(),
                    moduleVersionId = assembly.ManifestModule.ModuleVersionId }).ToArray() };
        Write(header);
        tileCapture?.Start(Program.Output, data.Count, data.TickOffset.Value, roundId);

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
                QueueOrdinaryPhases(state, false);
                _entities.FrameUpdate(delta);
            }
            else
            {
                QueueOrdinaryPhases(state, true);
                _entities.FrameUpdate(0);
            }
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
                // Project network candidates first. The independent scalar pass below detects
                // presentation changes on retained owners without serializing stable sprites.
                foreach (var entity in state.EntityStates.Value)
                {
                    if (!_entities.TryGetEntity(entity.NetEntity, out var uid)
                        || !_entities.TryGetComponent<TransformComponent>(uid, out var transform)
                        || !_entities.TryGetComponent<MetaDataComponent>(uid, out var metadata))
                        continue;
                    ProjectNative(uid.Value, upserts, audioEvents, false);
                }
            }
            var viewerMetadata = CaptureViewerMetadata(state, checkpoint.FullState, messages, upserts, audioEvents, index == 0);
            var lighting = Program.CaptureLighting ? CaptureLighting(upserts, audioEvents, index == 0) : null;
            InspectSpritePresentation(index > 0);
            if (index > 0) ProjectPresentationAppearance(upserts, audioEvents);
            ProjectMoved(upserts, audioEvents, index == 0);
            if (Program.CaptureLighting) ValidateLightingClosure();
            var spritePresentationReplacements = FinishSpritePresentation(data.ReplayTime[index].Ticks, index);
            if (index == 0)
                _initialEntities = upserts.Count;
            _projectionMs += Stopwatch.GetElapsedTime(projectionStart).TotalMilliseconds;
            _projectionAllocatedBytes += GC.GetTotalAllocatedBytes(precise: false) - projectionAllocatedStart;
            _upserts += upserts.Count;
            WriteResourceDefinitions();
            WriteShaderDefinitions();
            WriteSpriteDefinitions();
            // The initial native world is large. Keep JSONL records bounded without dropping entities.
            var chunkCount = index == 0 ? ViewerMetadataPolicy.InitialSnapshotChunks(upserts.Count, spritePresentationReplacements.Count) : 1;
            if (lighting != null && index == 0)
                chunkCount = Math.Max(1, Math.Max(chunkCount, Math.Max((lighting.PointReplacements.Count + 999) / 1000,
                    Math.Max((lighting.MapReplacements.Count + 999) / 1000,
                        (lighting.OccluderReplacements.Count + 999) / 1000))));
            for (var chunk = 0; chunk < chunkCount; chunk++)
            {
                var frame = new { kind = index == 0 ? "snapshot" : "delta", sequence = index,
                    chunkIndex = chunk, chunkCount,
                    sourceTick = state.ToSequence.Value, sourceTime100ns = data.ReplayTime[index].Ticks,
                    sourceServerTime100ns = checked(_sourceClockOrigin + data.ReplayTime[index].Ticks),
                    upserts = index == 0 ? upserts.Skip(chunk * 1000).Take(1000) : upserts,
                    deletes, audioEvents = chunk == 0 ? audioEvents : [],
                    spritePresentationReplacements = index == 0 ? spritePresentationReplacements.Skip(chunk * 1000).Take(1000) : spritePresentationReplacements,
                    stationUpserts = chunk == 0 ? viewerMetadata.StationUpserts : [],
                    stationDeletes = chunk == 0 ? viewerMetadata.StationDeletes : [],
                    playerUpserts = chunk == 0 ? viewerMetadata.PlayerUpserts : [],
                    playerDeletes = chunk == 0 ? viewerMetadata.PlayerDeletes : [] };
                if (lighting != null)
                    Write(new { frame.kind, frame.sequence, frame.chunkIndex, frame.chunkCount,
                        frame.sourceTick, frame.sourceTime100ns, frame.sourceServerTime100ns,
                        frame.upserts, frame.deletes, frame.audioEvents, frame.spritePresentationReplacements,
                        frame.stationUpserts, frame.stationDeletes, frame.playerUpserts, frame.playerDeletes,
                        lighting = lighting.Chunk(chunk, index == 0) });
                else
                    Write(frame);
            }
            // Independent event records retain initial-frame messages and permit a seekable
            // chat index without coalescing events into world state or replaying markup.
            if (viewerMetadata.ChatEvents.Count != 0)
                Write(new { kind = "chat-events", sequence = index, sourceTick = state.ToSequence.Value,
                    sourceTime100ns = data.ReplayTime[index].Ticks,
                    sourceServerTime100ns = checked(_sourceClockOrigin + data.ReplayTime[index].Ticks),
                    events = viewerMetadata.ChatEvents });
            tileCapture?.Capture(index, state.ToSequence.Value, data.ReplayTime[index].Ticks);
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
            missing = new[] { "mutable-shader-parameters", "shader-array-matrix-texture-values",
                "shader-render-only-inputs", "shader-hardware-defines", "tile-chunks",
                "complete-client-only-sprite-dirty-tracking", "image-bodies-and-atlas-crops",
                "client-recording-audio-message-payloads" } };
        var inventoryMs = Stopwatch.GetElapsedTime(inventoryStart).TotalMilliseconds;
        foreach (var image in inventory.images)
            EnsureResource("image", image, BodyMetadata);
        WriteResourceDefinitions();
        Write(inventory);
        output.Flush();
        var tileSummary = tileCapture?.Finish() ?? NativeTileCapture.DisabledSummary;

        var summary = new { schema = Program.SummarySchema,
            clipProfile = Program.Profile.Name, requestedSeconds = Program.Seconds, clipLimits = Program.ClipLimits,
            requiredCapabilities = Program.RequiredCapabilities, gameBuild = Program.GameBuild,
            engineVersion = Program.EngineVersion, frames = data.Count, blocksRead = native.BlocksRead,
            playbackBlocksRead = native.PlaybackBlocksRead,
            declaredDecodedBytes = native.DecodedBytes, simulatedSeconds = data.ReplayTime[^1].TotalSeconds,
            initialEntities = _initialEntities, entitiesAtEnd = _fingerprints.Count, initialSprites = _sprites, initialLayers = _layers,
            shaderLayers = _shaderLayers, totalUpserts = _upserts, audio = new { initial = _initialAudio,
                starts = _audioStarts, changes = _audioChanges, removals = _audioRemovals,
                soundMetadata = new { available = _soundMetadataAvailable, unavailable = _soundMetadataUnavailable },
                nativeStateTypes = _audioStateTypes, messageTypes = _messageTypes },
            materials = new { definitions = _materialDefinitions.Count, definitionBytes = _materialDefinitionBytes,
                sources = _shaderSourceDefinitions.Count, sourceDefinitionBytes = _shaderSourceDefinitionBytes,
                unavailable = _materialUnavailable },
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
            spritePresentation = new { replacements = _presentationReplacements, layerSamples = _presentationLayerSamples,
                scalarOwnerInspections = _presentationScalarInspections, forcedUpdates = _ordinaryForceUpdates,
                independentAppearanceCandidates = _independentAppearanceCandidates, retainedBytes = _presentationRetainedBytes,
                peakRetainedBytes = _presentationRetainedPeakBytes, maximumFramePresentationJSONBytes = _maximumFramePresentationJSONBytes,
                retainedOwners = _presentationStates.Count, assertion = _ordinaryPhaseProbe },
            presentationInspection = new {
                queue = new { ownerVisits = _queueOwnerVisits, layerVisits = _queueLayerVisits, milliseconds = _queueMs },
                fused = new { ownerVisits = _postOwnerVisits, layerVisits = _postLayerVisits, milliseconds = _fusedInspectionMs },
                appearance = new { layerComparisons = _appearanceLayerComparisons, projectionMilliseconds = _appearanceProjectionMs },
                phase = new { layerVisits = _phaseLayerVisits, baselineOwnerVisits = _baselineOwnerVisits,
                    baselineLayerVisits = _baselineLayerVisits, baselineDrainMilliseconds = _baselineDrainMs,
                    ownedArrayCopies = _presentationArraysAllocated },
                commitMillisecondsExcludingSerialization = _presentationCommitMs,
                serialization = new { values = _presentationSerializations, milliseconds = _presentationSerializationMs },
                combinedLiveStagedPeakBytes = _presentationCombinedPeakBytes,
                timingMeaning = "Queue, fused inspection, candidate projection, baseline drain, commit excluding serialization, and serialization are disjoint. Appearance comparisons and phase collection share the fused interval; do not count it twice. These intervals are nested in existing apply/projection totals." },
            parentClosure = new { ancestorCandidates = _ancestorCandidates, ancestorAdditions = _ancestorAdditions,
                clientAncestors = _clientAncestors, movedCandidates = _movedCandidates,
                observedNativeDeletions = _observedDeletions, maximumDepth = _maxParentDepth,
                firstTransitionAncestors = _firstTransitionAncestors },
            viewerMetadata = new { enabled = true, playersAtEnd = _viewerMetadata.PlayerCount,
                stationsAtEnd = _viewerMetadata.StationCount, chatEvents = _viewerMetadata.ChatCount,
                retainedBytes = _viewerMetadata.RetainedBytes,
                completeness = "observed-native-clip", chatClock = "containing-native-replay-frame",
                chatText = "native-post-accent-message" },
            tiles = tileSummary,
            allocationsBytes = new { startup = startupAllocatedBytes,
                loadAndCheckpoint = loadedAllocatedBytes - Program.AllocatedAtStart - startupAllocatedBytes,
                entityInitialization = initializedAllocatedBytes - loadedAllocatedBytes,
                clipLoop = loopAllocatedBytes, projection = _projectionAllocatedBytes,
                total = GC.GetTotalAllocatedBytes(precise: true) - Program.AllocatedAtStart },
            gcCollections = new { clipLoop = loopCollections,
                total = Enumerable.Range(0, 3).Select(gen => GC.CollectionCount(gen) - Program.CollectionsAtStart[gen]).ToArray() },
            managedBytes = GC.GetTotalMemory(false), peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            outputBytes = output.Length, diagnosticOnly = true };
        var success = Program.CaptureLighting ? (object) new {
            summary.schema, summary.clipProfile, summary.requestedSeconds, summary.clipLimits,
            summary.requiredCapabilities, summary.gameBuild, summary.engineVersion, summary.frames,
            summary.blocksRead, summary.playbackBlocksRead, summary.declaredDecodedBytes, summary.simulatedSeconds,
            summary.initialEntities, summary.entitiesAtEnd, summary.initialSprites, summary.initialLayers,
            summary.shaderLayers, summary.totalUpserts, summary.audio, summary.materials, summary.stagesMilliseconds,
            summary.clipLoopSpeed, summary.interning, summary.spritePresentation, summary.presentationInspection,
            summary.parentClosure, summary.viewerMetadata, summary.tiles, summary.allocationsBytes, summary.gcCollections,
            summary.managedBytes, summary.peakWorkingSetBytes, summary.outputBytes, summary.diagnosticOnly,
            lighting = LightingSummary()
        } : summary;
        File.WriteAllBytes(Path.Combine(Program.Output, "summary.json"), JsonSerializer.SerializeToUtf8Bytes(success, Json));
        Console.WriteLine(JsonSerializer.Serialize(success, Json));
    }

    private void Capture(EntityUid uid, TransformComponent transform, MetaDataComponent metadata,
        List<object> upserts, List<object> audioEvents, bool initial)
    {
        var id = metadata.NetEntity.Id;
        var ownerPreviouslyPresent = _fingerprints.ContainsKey(id);
        TrackPresentationOwner(id, uid);
        int? spriteId = null;
        var previousSpriteId = _previousSprites.GetValueOrDefault(id);
        if (_entities.TryGetComponent<SpriteComponent>(uid, out var component))
            spriteId = CaptureSprite(uid, id, component, initial);
        else
        {
            _previousSprites.Remove(id);
            RemovePresentationState(id);
        }
        if (PresentationPolicy.RequiresBaseline(ownerPreviouslyPresent, previousSpriteId, spriteId ?? 0))
            _presentationBaselineOwners.Add(id);
        var isMap = _entities.HasComponent<MapComponent>(uid);
        if (Program.CaptureLighting)
        {
            if (isMap) _lightingGraphMaps.Add(id); else _lightingGraphMaps.Remove(id);
        }
        var record = new { id, parentId = transform.ParentUid == EntityUid.Invalid ? (int?) null
                : _entities.GetNetEntity(transform.ParentUid).Id,
            prototype = metadata.EntityPrototype?.ID, name = NativeName(metadata.EntityName),
            transform = new { x = transform.LocalPosition.X, y = transform.LocalPosition.Y, rotation = transform.LocalRotation.Theta },
            transform.Anchored, transform.NoLocalRotation,
            isMap, isGrid = _entities.HasComponent<MapGridComponent>(uid), spriteId };
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
            var resourceId = EnsureSoundResource(audio.FileName);
            var value = new { id, audio.FileName, startTime100ns = audio.AudioStart.Ticks,
                startReplayTime100ns = checked(audio.AudioStart.Ticks - _sourceClockOrigin),
                pauseTime100ns = audio.PauseTime?.Ticks, resourceId,
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
