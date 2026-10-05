using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Robust.Client;
using Robust.Shared;

namespace Content.Replay.Diagnostic;

internal static class Program
{
    public const string GameBuild = "94087a918a2fae4571f5a529fe14ef7f5dce29a3";
    public const string EngineVersion = "289.0.3";
    public static readonly Stopwatch Total = Stopwatch.StartNew();
    public static string Input = "";
    public static string Output = "";
    public static double Seconds = 10;
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
        try
        {
            var values = new Dictionary<string, string>();
            for (var i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException("Arguments require --input, --resources, --output and optional --seconds.");
                values.Add(args[i], args[i + 1]);
            }
            Input = Path.GetFullPath(values["--input"]);
            Output = Path.GetFullPath(values["--output"]);
            var resources = Path.GetFullPath(values["--resources"]);
            if (values.TryGetValue("--seconds", out var seconds))
                Seconds = double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture);
            if (!double.IsFinite(Seconds) || Seconds <= 0 || Seconds > 10)
                throw new ArgumentOutOfRangeException(nameof(Seconds), "The diagnostic is capped at ten simulated seconds.");
            if (values.TryGetValue("--max-sprite-definitions", out var definitions))
                MaxSpriteDefinitions = int.Parse(definitions);
            if (values.TryGetValue("--max-sprite-definition-bytes", out var bytes))
                MaxSpriteDefinitionBytes = long.Parse(bytes);
            if (values.TryGetValue("--max-resource-definitions", out var resourcesLimit))
                MaxResourceDefinitions = int.Parse(resourcesLimit);
            if (MaxSpriteDefinitions is <= 0 or > 100_000 || MaxResourceDefinitions is <= 0 or > 25_000
                || MaxSpriteDefinitionBytes is <= 0 or > 512L * 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(MaxSpriteDefinitions), "Invalid bounded dictionary limits.");

            using var zip = ZipFile.OpenRead(Input);
            using var bundle = JsonDocument.Parse(zip.GetEntry("rt_content_bundle.json")!.Open());
            var engine = bundle.RootElement.GetProperty("engine_version").GetString();
            if (engine != EngineVersion)
            {
                var direction = Version.Parse(engine!) > Version.Parse(EngineVersion) ? "future" : "older";
                throw new InvalidDataException($"Unsupported {direction} engine {engine}; this explicit reader supports only {EngineVersion}.");
            }
            var build = bundle.RootElement.GetProperty("base_build");
            if (build.GetProperty("version").GetString() != GameBuild)
                throw new InvalidDataException("Unsupported game build; select its matching reader and resources.");
            var expectedHash = build.GetProperty("hash").GetString()!;
            ResourceBundleSha256 = expectedHash.ToLowerInvariant();
            ForkId = build.GetProperty("fork_id").GetString()!;
            var timer = Stopwatch.StartNew();
            var info = new FileInfo(resources);
            var stamp = resources + ".verified";
            var identity = $"{expectedHash}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            if (!File.Exists(stamp) || File.ReadAllText(stamp) != identity)
            {
                using var resourceFile = File.OpenRead(resources);
                if (!Convert.ToHexString(SHA256.HashData(resourceFile)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Matching resource archive SHA-256 mismatch.");
                File.WriteAllText(stamp, identity);
            }
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
