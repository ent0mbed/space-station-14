using System.Diagnostics;
using System.Text.Json;
using Robust.Client;
using Robust.Client.Replays.Loading;
using Robust.Shared;

namespace Content.Replay.Diagnostic;

internal static class Program
{
    public const string GameBuild = "94087a918a2fae4571f5a529fe14ef7f5dce29a3";
    public const string EngineVersion = "289.0.3";
    public const string EngineCommit = "36905986f6809420dbc78168fc494f91723d356b";
    public static PresentationPolicy Presentation = PresentationPolicy.Exact;
    public static string SceneSchema => Presentation.SceneSchema;
    public static string SummarySchema => Presentation.SummarySchema;
    public const string ShaderCopyCapability = "native-shader-copy-bindings/1";
    public const string AudioTimingCapability = "native-audio-timing-metadata/1";
    public const string SpriteBoundsCapability = "native-sprite-local-bounds/1";
    public const string TileEdgeCapability = "native-tile-edge-inputs/1";
    public const string FrozenMaterialCapability = "native-frozen-material-snapshots/1";
    public static string PresentationCapability => Presentation.Capability;
    // Every shared layer carries the native Blank boolean. Legacy absence means unknown.
    public const string LayerBlankCapability = "native-sprite-layer-blank/1";
    public const int MaxPresentationOwners = 250_000;
    public const int MaxPresentationLayersPerOwner = 256;
    public const long MaxPresentationRetainedBytes = 64L * 1024 * 1024;
    public static int? AssertPhaseEntity;
    public static int AssertPhaseLayer;
    public const int MaxShaderParameterNameCharacters = 256;
    public const int MaxShaderParameters = 256;
    public const int MaxMaterialDefinitions = 4096;
    public const int MaxMaterialDefinitionBytes = 16 * 1024 * 1024;
    public const int MaxShaderSourceDefinitions = 512;
    public const int MaxShaderSourceDefinitionBytes = 8 * 1024 * 1024;
    public const int MaxShaderIncludes = 32;
    public const int MaxShaderSourceFileBytes = 1024 * 1024;
    public const int MaxShaderClosureBytes = 8 * 1024 * 1024;
    public static readonly Stopwatch Total = Stopwatch.StartNew();
    public static string Input = "";
    public static string Resources = "";
    public static string Output = "";
    public static double Seconds = 10;
    public static ReplayClipProfile Profile = ReplayClipProfile.TenSecond;
    public static object ClipLimits => new { maxSeconds = Profile.MaxDuration.TotalSeconds,
        maxStates = Profile.MaxStates, maxNativeBlocks = Profile.MaxBlocks,
        requiredTickRate = Profile.RequiredTickRate, maxNativeBlockBytes = ReplayClipDiagnostics.MaxBlockBytes,
        maxNativeDecodedBytes = ReplayClipDiagnostics.MaxDecodedBytes };
    public static bool CaptureTiles;
    public static double ResourceVerificationMilliseconds;
    public static string ResourceBundleSha256 = "";
    public static string ForkId = "";
    public static int MaxSpriteDefinitions = 25_000;
    public static long MaxSpriteDefinitionBytes = 128L * 1024 * 1024;
    public static int MaxResourceDefinitions = 10_000;
    public static readonly long AllocatedAtStart = GC.GetTotalAllocatedBytes(precise: true);
    public static readonly int[] CollectionsAtStart = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: ss14-replay inspect|resources|export --input REPLAY.zip [options]. See --help.");
            return;
        }
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine("""
                SS14 replay reader (fork wizards, game 94087a91, engine 289.0.3).

                Usage:
                  inspect --input REPLAY.zip
                  resources --input REPLAY.zip [--cache DIRECTORY] [--resources SS14.Client.zip]
                  export --input REPLAY.zip --output DIRECTORY [--cache DIRECTORY]
                  [--resources SS14.Client.zip]
                  [--profile ten-second|minute-preview] [--seconds NUMBER] [--tiles true|false]
                  [--presentation exact|visual]
                  [--assert-ordinary-phase ENTITY_ID:LAYER_INDEX]
                  [--max-sprite-definitions NUMBER] [--max-sprite-definition-bytes NUMBER]
                  [--max-resource-definitions NUMBER]

                Default profile: ten-second; default duration: 10 simulated seconds; tiles: false.
                Presentation defaults to exact (schema 0.8). Visual (schema 0.9) holds countdown-only changes.
                Presentation policy is independent of the duration profile and requires matching consumers.
                Ordinary animation eligibility is cached on the pinned serial engine thread.
                Native shared-layer owners are classified on every pass.
                Export is the default command. Without --resources, resources are downloaded to the local cache.
                Resources are checked against the replay's SHA-256 on download and every cache reuse.
                Profiles allow at most 10 or 60 simulated seconds. Resources must match the replay build/hash.
                --help and -h print this usage without opening replay/resources or starting the engine.
                """);
            return;
        }

        try
        {
            var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "export";
            if (command is not ("inspect" or "resources" or "export"))
                throw new ArgumentException($"Unknown command: {command}. Expected inspect, resources, or export.");
            if (args.Length > 0 && args[0] == command) args = args[1..];
            var values = new Dictionary<string, string>();
            string[] exportOptions = ["--input", "--resources", "--cache", "--output", "--seconds", "--profile", "--tiles", "--presentation",
                "--max-sprite-definitions", "--max-sprite-definition-bytes", "--max-resource-definitions", "--assert-ordinary-phase"];
            string[] supportedOptions = command switch
            {
                "inspect" => ["--input"],
                "resources" => ["--input", "--resources", "--cache"],
                _ => exportOptions
            };
            for (var i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException("Each option requires a value. See --help.");
                if (!supportedOptions.Contains(args[i], StringComparer.Ordinal))
                    throw new ArgumentException($"Unknown option: {args[i]}.");
                values.Add(args[i], args[i + 1]);
            }
            Presentation = PresentationPolicy.Parse(values.GetValueOrDefault("--presentation"));
            Input = Path.GetFullPath(values["--input"]);
            var replay = ReplayIdentity.Read(Input);
            if (command == "inspect")
            {
                Console.WriteLine(JsonSerializer.Serialize(replay.Description()));
                return;
            }
            replay.RequireSupported();
            if (command == "resources")
            {
                var resolved = ReplayResources.Resolve(replay, values.GetValueOrDefault("--resources"),
                    values.GetValueOrDefault("--cache", ReplayResources.DefaultCache), completeCache: true);
                Console.WriteLine(JsonSerializer.Serialize(new { resources = resolved.Folder, clientZip = resolved.ClientZip,
                    manifest = Path.Combine(resolved.Folder!, "resource-manifest.json"), sha256 = replay.ResourceSha256,
                    gameBuild = replay.GameBuild, engineVersion = replay.EngineVersion, engineCommit = EngineCommit }));
                return;
            }
            Output = Path.GetFullPath(values["--output"]);
            if (values.TryGetValue("--assert-ordinary-phase", out var phaseProbe))
            {
                var parts = phaseProbe.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var entityId)
                    || entityId == 0 || !int.TryParse(parts[1], out var layerIndex)
                    || layerIndex < 0 || layerIndex >= MaxPresentationLayersPerOwner)
                    throw new ArgumentException("Expected --assert-ordinary-phase entityId:layerIndex.");
                AssertPhaseEntity = entityId;
                AssertPhaseLayer = layerIndex;
            }
            if (values.TryGetValue("--profile", out var profile))
                Profile = profile switch
                {
                    "ten-second" => ReplayClipProfile.TenSecond,
                    "minute-preview" => ReplayClipProfile.MinutePreview,
                    _ => throw new ArgumentException($"Unknown clip profile: {profile}.")
                };
            if (values.TryGetValue("--seconds", out var seconds))
                Seconds = double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture);
            if (values.TryGetValue("--tiles", out var tiles))
                CaptureTiles = bool.Parse(tiles);
            if (!double.IsFinite(Seconds) || Seconds <= 0 || Seconds > Profile.MaxDuration.TotalSeconds
                || TimeSpan.FromSeconds(Seconds) <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(Seconds), $"Invalid duration for {Profile.Name}; maximum {Profile.MaxDuration.TotalSeconds} simulated seconds.");
            if (values.TryGetValue("--max-sprite-definitions", out var definitions))
                MaxSpriteDefinitions = int.Parse(definitions);
            if (values.TryGetValue("--max-sprite-definition-bytes", out var bytes))
                MaxSpriteDefinitionBytes = long.Parse(bytes);
            if (values.TryGetValue("--max-resource-definitions", out var resourcesLimit))
                MaxResourceDefinitions = int.Parse(resourcesLimit);
            if (MaxSpriteDefinitions is <= 0 or > 100_000 || MaxResourceDefinitions is <= 0 or > 25_000
                || MaxSpriteDefinitionBytes is <= 0 or > 512L * 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(MaxSpriteDefinitions), "Invalid bounded dictionary limits.");

            ResourceBundleSha256 = replay.ResourceSha256;
            ForkId = replay.ForkId;
            var timer = Stopwatch.StartNew();
            var resources = Resources = ReplayResources.Resolve(replay, values.GetValueOrDefault("--resources"),
                values.GetValueOrDefault("--cache", ReplayResources.DefaultCache)).ClientZip;
            ResourceVerificationMilliseconds = timer.Elapsed.TotalMilliseconds;
            Directory.CreateDirectory(Output);

            // Mount this adapter and its matching assemblies separately from build resources.
            var moduleRoot = PrepareModuleRoot(Output, AppContext.BaseDirectory);
            ContentStart.StartLibrary(["--headless"], new GameControllerOptions
            {
                Sandboxing = false,
                LoadContentResources = false,
                LoadConfigAndUserData = false,
                ContentModulePrefix = "Content.",
                ContentBuildDirectory = "Content.Replay.Diagnostic",
                UserDataDirectoryName = "SS14 Replay Diagnostic",
                MountOptions = new MountOptions([resources], [moduleRoot])
            });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Environment.ExitCode = 1;
        }
    }

    internal static string PrepareModuleRoot(string output, string binaries)
    {
        // A previous checkout may have been removed, leaving dangling DLL links.
        // Each invocation owns fresh links so reusing Output cannot load an older adapter.
        var moduleRoot = Path.Combine(output, "runtime-modules", Guid.NewGuid().ToString("N"));
        var assemblies = Path.Combine(moduleRoot, "Assemblies");
        Directory.CreateDirectory(assemblies);
        foreach (var dll in Directory.EnumerateFiles(binaries, "Content.*.dll"))
            File.CreateSymbolicLink(Path.Combine(assemblies, Path.GetFileName(dll)), dll);
        return moduleRoot;
    }
}
