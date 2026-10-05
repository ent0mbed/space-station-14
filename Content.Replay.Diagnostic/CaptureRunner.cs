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

    public async Task RunAsync()
    {
        var startupMs = Program.Total.Elapsed.TotalMilliseconds;
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
        if (!Convert.ToHexString(_factory.GetHash(true)).Equals(componentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Matching reader network component hash mismatch.");
        var data = await ((ReplayLoadManager) _loader).LoadReplayClipAsync(reader,
            (_, _, _, _) => Task.CompletedTask, TimeSpan.FromSeconds(Program.Seconds), native);
        var loadMs = loadTimer.Elapsed.TotalMilliseconds;
        var startTimer = Stopwatch.StartNew();
        await _loader.StartReplayAsync(data, (_, _, _, _) => Task.CompletedTask);
        var initializeMs = startTimer.Elapsed.TotalMilliseconds;

        var resourceTimer = Stopwatch.StartNew();
        foreach (var (path, texture) in _resources.GetAllResources<TextureResource>())
            _texturePaths.TryAdd(texture.Texture, path.ToString());
        var resourceIndexMs = resourceTimer.Elapsed.TotalMilliseconds;

        using var output = File.Create(Path.Combine(Program.Output, "scene.jsonl"));
        _output = output;
        Write(new { kind = "diagnostic-header", schema = "ss14-diagnostic/0.1", gameBuild = Program.GameBuild,
            engineVersion = Program.EngineVersion, frameCount = data.Count,
            sourceStartTick = data.TickOffset.Value, timeUnit = "100ns", finalizedTransport = false,
            loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic && assembly.GetName().Name is { } name
                    && (name.StartsWith("Content.", StringComparison.Ordinal)
                        || name.StartsWith("Robust.", StringComparison.Ordinal)))
                .OrderBy(assembly => assembly.GetName().Name)
                .Select(assembly => new { name = assembly.GetName().Name,
                    version = assembly.GetName().Version?.ToString(),
                    moduleVersionId = assembly.ManifestModule.ModuleVersionId }).ToArray() });

        var simulation = Stopwatch.StartNew();
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
            List<object> upserts = new();
            List<int> deletes = new();
            List<object> audioEvents = new();
            if (index == 0)
            {
                var query = _entities.EntityQueryEnumerator<TransformComponent, MetaDataComponent>();
                while (query.MoveNext(out var uid, out var transform, out var metadata))
                {
                    if (metadata.NetEntity.IsClientSide())
                        continue;
                    if (_fingerprints.Count >= 250_000)
                        throw new InvalidDataException("Diagnostic entity budget exceeded.");
                    Capture(uid, transform, metadata, upserts, audioEvents, true);
                }
                _initialEntities = upserts.Count;
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
                    Capture(uid.Value, transform, metadata, upserts, audioEvents, false);
                }
                foreach (var deleted in state.EntityDeletions.Value)
                {
                    if (_fingerprints.Remove(deleted.Id))
                        deletes.Add(deleted.Id);
                    if (_audio.Remove(deleted.Id))
                    {
                        _audioRemovals++;
                        audioEvents.Add(new { kind = "remove", id = deleted.Id });
                    }
                }
            }
            _projectionMs += Stopwatch.GetElapsedTime(projectionStart).TotalMilliseconds;
            _upserts += upserts.Count;
            // The initial native world is large. Keep JSONL records bounded without dropping entities.
            var chunkCount = index == 0 ? (upserts.Count + 999) / 1000 : 1;
            for (var chunk = 0; chunk < chunkCount; chunk++)
                Write(new { kind = index == 0 ? "snapshot" : "delta", sequence = index,
                    chunkIndex = chunk, chunkCount,
                    sourceTick = state.ToSequence.Value, sourceTime100ns = data.ReplayTime[index].Ticks,
                    upserts = index == 0 ? upserts.Skip(chunk * 1000).Take(1000) : upserts,
                    deletes, audioEvents = chunk == 0 ? audioEvents : [] });
        }
        simulation.Stop();

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
        Write(inventory);
        output.Flush();

        var summary = new { schema = "ss14-diagnostic-summary/0.1", gameBuild = Program.GameBuild,
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
            managedBytes = GC.GetTotalMemory(false), peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            outputBytes = output.Length, diagnosticOnly = true };
        File.WriteAllBytes(Path.Combine(Program.Output, "summary.json"), JsonSerializer.SerializeToUtf8Bytes(summary, Json));
        Console.WriteLine(JsonSerializer.Serialize(summary, Json));
        _resources.OnRawTextureLoaded -= OnTexture;
        _playback.StopReplay();
    }

    private void Capture(EntityUid uid, TransformComponent transform, MetaDataComponent metadata,
        List<object> upserts, List<object> audioEvents, bool initial)
    {
        var id = metadata.NetEntity.Id;
        object? sprite = null;
        if (_entities.TryGetComponent<SpriteComponent>(uid, out var component))
        {
            var layers = component.AllLayers.Cast<SpriteComponent.Layer>().Select((layer, index) =>
            {
                var rsi = layer.RSI ?? component.BaseRSI;
                if (rsi != null)
                    _rsiPaths.Add(rsi.Path.ToString());
                var shader = layer.ShaderPrototype?.ToString();
                if (shader != null)
                    _shaderPrototypes.Add(shader);
                if (initial && (shader != null || layer.Shader != null))
                    _shaderLayers++;
                return new { index, layer.Visible, color = Color(layer.Color), scale = Vector(layer.Scale),
                    offset = Vector(layer.Offset), rotation = layer.Rotation.Theta,
                    rsiPath = rsi?.Path.ToString(), rsiState = layer.State.Name,
                    texturePath = ResolveTexture(layer.Texture), layer.AnimationFrame, layer.AnimationTimeLeft,
                    layer.AutoAnimated, layer.Loop, layer.Cycle, layer.Reversed,
                    directionOffset = layer.DirOffset.ToString(), renderingStrategy = layer.RenderingStrategy.ToString(),
                    shaderPrototype = shader, hasShader = layer.Shader != null,
                    materialMutable = layer.Shader?.Mutable, shaderParametersUnavailable = layer.Shader != null,
                    copyToShader = layer.CopyToShaderParameters != null };
            }).ToArray();
            if (layers.Length > 256)
                throw new InvalidDataException("Diagnostic sprite layer budget exceeded.");
            if (initial)
            {
                _sprites++;
                _layers += layers.Length;
            }
            sprite = new { component.Visible, component.ContainerOccluded, component.DrawDepth, component.RenderOrder,
                color = Color(component.Color), scale = Vector(component.Scale), offset = Vector(component.Offset),
                rotation = component.Rotation.Theta, component.NoRotation, component.SnapCardinals,
                component.EnableDirectionOverride, directionOverride = component.DirectionOverride.ToString(),
                component.GranularLayersRendering, layers,
                postShaders = _entities.System<SpriteSystem>().GetPostShaders(component).Select(post => new {
                    post.Id, hasShader = post.Shader != null, post.GetScreenTexture, post.RaiseShaderEvent,
                    shaderParametersUnavailable = true }).ToArray() };
        }
        var record = new { id, parentId = transform.ParentUid == EntityUid.Invalid ? (int?) null
                : _entities.GetNetEntity(transform.ParentUid).Id,
            prototype = metadata.EntityPrototype?.ID,
            transform = new { x = transform.LocalPosition.X, y = transform.LocalPosition.Y, rotation = transform.LocalRotation.Theta },
            transform.Anchored, transform.NoLocalRotation,
            isMap = _entities.HasComponent<MapComponent>(uid), isGrid = _entities.HasComponent<MapGridComponent>(uid), sprite };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(record, Json)));
        if (!_fingerprints.TryGetValue(id, out var previous) || previous != fingerprint)
        {
            _fingerprints[id] = fingerprint;
            upserts.Add(record);
        }
        if (_entities.TryGetComponent<AudioComponent>(uid, out var audio))
        {
            var audioState = audio.State;
            _soundPaths.Add(audio.FileName);
            var value = new { id, audio.FileName, startTime100ns = audio.AudioStart.Ticks,
                state = audioState.ToString(), audio.Global, flags = (byte) audio.Flags,
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
        if (bytes.Length > 64 * 1024 * 1024 || _output.Position + bytes.Length > 1024L * 1024 * 1024)
            throw new InvalidDataException($"Diagnostic output budget exceeded ({bytes.Length} byte record).");
        _output.Write(bytes);
        _output.WriteByte((byte) '\n');
        _outputMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
}
