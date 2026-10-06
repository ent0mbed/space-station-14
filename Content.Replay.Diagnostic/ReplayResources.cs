using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Content.Replay.Diagnostic;

internal sealed record ReplayIdentity(string EngineVersion, string GameBuild, string ForkId,
    string ResourceSha256, string? DownloadUrl)
{
    private const int MaxMetadataBytes = 64 * 1024;

    public bool Supported => EngineVersion == Program.EngineVersion && GameBuild == Program.GameBuild
        && ForkId == "wizards";

    public static ReplayIdentity Read(string input)
    {
        using var zip = ZipFile.OpenRead(input);
        var entries = zip.Entries.Where(entry => entry.FullName == "rt_content_bundle.json").ToArray();
        if (entries.Length != 1 || entries[0].Length is <= 0 or > MaxMetadataBytes)
            throw new InvalidDataException("Replay requires one bounded rt_content_bundle.json metadata entry.");
        using var stream = entries[0].Open();
        using var bounded = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            if (bounded.Length + count > MaxMetadataBytes)
                throw new InvalidDataException("Replay metadata exceeds 64 KiB.");
            bounded.Write(buffer, 0, count);
        }
        using var document = JsonDocument.Parse(bounded.ToArray(), new JsonDocumentOptions { AllowDuplicateProperties = false });
        var bundle = document.RootElement;
        var build = bundle.GetProperty("base_build");
        var hash = Text(build, "hash", 64).ToLowerInvariant();
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Replay resource hash must be SHA-256 hexadecimal.");
        var url = build.TryGetProperty("download_url", out var value) && value.ValueKind != JsonValueKind.Null
            ? Text(build, "download_url", 2048) : null;
        return new ReplayIdentity(Text(bundle, "engine_version", 128), Text(build, "version", 128),
            Text(build, "fork_id", 128), hash, url);
    }

    public void RequireSupported()
    {
        if (!Supported)
            throw new InvalidDataException($"Unsupported replay: fork {ForkId}, game {GameBuild}, engine {EngineVersion}. "
                + $"This reader supports only fork wizards, game {Program.GameBuild}, engine {Program.EngineVersion}. "
                + "Downloading resources cannot make another game or engine compatible.");
    }

    public object Description() => new { engineVersion = EngineVersion, gameBuild = GameBuild,
        forkId = ForkId, resourceSha256 = ResourceSha256, downloadUrl = DownloadUrl, supported = Supported,
        reader = new { engineVersion = Program.EngineVersion, gameBuild = Program.GameBuild, forkId = "wizards" } };

    private static string Text(JsonElement parent, string property, int maximum)
    {
        var value = parent.GetProperty(property).GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new InvalidDataException($"Invalid replay metadata field: {property}.");
        return value;
    }
}

internal sealed record ResourceScope(string GameBuild, string BundleSha256, string EngineVersion, string EngineCommit);
internal sealed record CachedClientZip(string LocalPath, long SizeBytes, string Sha256);
internal sealed record CachedEngineFile(string Origin, string Path, string LocalPath, long SizeBytes, string Sha256,
    [property: System.Text.Json.Serialization.JsonPropertyName("sourceURL")] string SourceURL);
internal sealed record CachedEngineNotice(string LocalPath, long SizeBytes, string Sha256, string Copyright);
internal sealed record ResourceManifest(string Schema, ResourceScope Scope, CachedClientZip ClientZip,
    CachedEngineFile[] EngineFiles, CachedEngineNotice EngineNotice);
internal sealed record ResolvedResources(string ClientZip, string? Folder);

internal static class ReplayResources
{
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const int MaxMetadataBytes = 64 * 1024;
    private const string ManifestName = "resource-manifest.json";
    private const string NoticePath = "notices/RobustToolbox-MIT.txt";
    private const string NoticeHash = "85e422621460a175baed622744a56975062e23b1e3149cd621a690b1bc5888a6";
    private const string EngineCopyright = "2019 Space Station 14 Contributors";
    public const string StockShaderPath = "/Shaders/Internal/default-sprite.swsl";
    public const string StockShaderHash = "21941e32b407ddf3a9ce5d113d4d03067c13923a43a42f6160b8af1b2366ea99";
    public static readonly (string Name, string Hash)[] EngineInputs = [
        ("z-library.glsl", "ae0b33140c4d6e33af7943ef0432b9397d9996d39fda1c1e9d0ed3b7847e3e89"),
        ("base-default.frag", "c94e068b83eada24ba707f6528d04c6166cc745c28d7f0568b3167d26fbb25ff"),
        ("base-default.vert", "22c714895843b6e99b70216e5b8a1b346b12d7f47bce9687b6d2946c815ba95c")];
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static string DefaultCache => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ss14-replay", "cache");

    public static ResolvedResources Resolve(ReplayIdentity replay, string? supplied, string cache, bool completeCache = false)
    {
        replay.RequireSupported();
        if (supplied != null && !completeCache)
        {
            var path = Path.GetFullPath(supplied);
            Verify(path, replay.ResourceSha256);
            Console.Error.WriteLine($"Verified resources: {path}");
            return new ResolvedResources(path, null);
        }
        var root = Path.GetFullPath(cache);
        var scope = new ResourceScope(replay.GameBuild, replay.ResourceSha256, replay.EngineVersion, Program.EngineCommit);
        var directory = Path.Combine(root, scope.GameBuild, scope.BundleSha256, scope.EngineVersion, scope.EngineCommit);
        CheckCacheDirectory(root, directory, allowMissing: true);
        if (Directory.Exists(directory))
        {
            Validate(root, directory, scope);
            Console.Error.WriteLine($"Using verified cached resources: {directory}");
            return new ResolvedResources(Path.Combine(directory, "SS14.Client.zip"), directory);
        }
        ResolveEngineCommit(replay);
        Directory.CreateDirectory(root);
        var stage = Path.Combine(root, ".resources-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var client = Path.Combine(stage, "SS14.Client.zip");
            if (supplied != null)
            {
                Verify(Path.GetFullPath(supplied), replay.ResourceSha256);
                File.Copy(Path.GetFullPath(supplied), client);
            }
            else
            {
                var expectedUrl = $"https://wizards.cdn.spacestation14.com/fork/wizards/version/{replay.GameBuild}/file/SS14.Client.zip";
                if (replay.DownloadUrl != expectedUrl)
                    throw new InvalidDataException("Replay resource URL is outside this reader's supported official build. Supply --resources.");
                Download(new Uri(expectedUrl), client, replay.ResourceSha256, MaxArchiveBytes);
            }
            var files = EngineFiles();
            foreach (var file in files)
            {
                var path = Path.Combine(stage, file.LocalPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var downloadUrl = file.SourceURL.Replace("https://github.com/space-wizards/RobustToolbox/blob/",
                    "https://raw.githubusercontent.com/space-wizards/RobustToolbox/", StringComparison.Ordinal);
                Download(new Uri(downloadUrl), path, file.Sha256, Program.MaxShaderSourceFileBytes);
            }
            var notice = Path.Combine(stage, NoticePath);
            Directory.CreateDirectory(Path.GetDirectoryName(notice)!);
            Download(new Uri($"https://raw.githubusercontent.com/space-wizards/RobustToolbox/{Program.EngineCommit}/LICENSE-MIT.TXT"),
                notice, NoticeHash, MaxMetadataBytes);
            var manifest = new ResourceManifest("ss14-replay-resources/0.1", scope,
                new CachedClientZip("SS14.Client.zip", new FileInfo(client).Length, replay.ResourceSha256), files,
                new CachedEngineNotice(NoticePath, 1074, NoticeHash, EngineCopyright));
            File.WriteAllBytes(Path.Combine(stage, ManifestName), JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
            Validate(root, stage, scope);
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            CheckCacheDirectory(root, directory, allowMissing: true);
            try { Directory.Move(stage, directory); }
            catch (IOException) when (Directory.Exists(directory)) { Validate(root, directory, scope); }
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
        Console.Error.WriteLine($"Cached verified resources: {directory}");
        return new ResolvedResources(Path.Combine(directory, "SS14.Client.zip"), directory);
    }

    private static CachedEngineFile[] EngineFiles() => [
        new("engine-commit", StockShaderPath, "engine/default-sprite.swsl", 50, StockShaderHash,
            EngineURL("Resources/Shaders/Internal/default-sprite.swsl")),
        new("engine-assembly", "Robust.Client.Graphics.Clyde.Shaders.z-library.glsl", "engine/z-library.glsl", 5433,
            EngineInputs[0].Hash, EngineURL("Robust.Client/Graphics/Clyde/Shaders/z-library.glsl")),
        new("engine-assembly", "Robust.Client.Graphics.Clyde.Shaders.base-default.frag", "engine/base-default.frag", 2261,
            EngineInputs[1].Hash, EngineURL("Robust.Client/Graphics/Clyde/Shaders/base-default.frag")),
        new("engine-assembly", "Robust.Client.Graphics.Clyde.Shaders.base-default.vert", "engine/base-default.vert", 1567,
            EngineInputs[2].Hash, EngineURL("Robust.Client/Graphics/Clyde/Shaders/base-default.vert")),
        // The existing viewer's point/mask and wall passes use this exact raw-pass closure.
        // shadow_cast_shared is a required include, not a GPU shadow-depth program.
        new("engine-commit", "/Shaders/Internal/light-soft.swsl", "engine/light-soft.swsl", 3087,
            "213fa70ea54430d18bdb9eec2636d8591a74ceb2cad76cb803f9579c74b63311",
            EngineURL("Resources/Shaders/Internal/light-soft.swsl")),
        new("engine-commit", "/Shaders/Internal/light_shared.swsl", "engine/light_shared.swsl", 2014,
            "ed1614de12d11ec5c6baa571171fba45e1a634842a7049e5bda608c467e33fbc",
            EngineURL("Resources/Shaders/Internal/light_shared.swsl")),
        new("engine-commit", "/Shaders/Internal/shadow_cast_shared.swsl", "engine/shadow_cast_shared.swsl", 1251,
            "099ad0f35a57597bad5c3877e650ada34b53440484bdd20a29ca89d325ce8259",
            EngineURL("Resources/Shaders/Internal/shadow_cast_shared.swsl")),
        new("engine-commit", "/Shaders/Internal/wall-bleed-blur.swsl", "engine/wall-bleed-blur.swsl", 1065,
            "ee8bd766bbc32a309598909046315bcc160b81f7bf1dee231d0b9ad0e587c7f9",
            EngineURL("Resources/Shaders/Internal/wall-bleed-blur.swsl")),
        new("engine-commit", "/Shaders/Internal/wall-merge.swsl", "engine/wall-merge.swsl", 213,
            "9b630f527366f0bf00a90863a288d945166b1625d52722cacaf1eaebaf92f72d",
            EngineURL("Resources/Shaders/Internal/wall-merge.swsl")),
        new("engine-assembly", "Robust.Client.Graphics.Clyde.Shaders.base-raw.frag", "engine/base-raw.frag", 457,
            "d8d6dea3d79e6292a5c63f48a86a6e07517f88fcc00a8b562208dc1757aff555",
            EngineURL("Robust.Client/Graphics/Clyde/Shaders/base-raw.frag")),
        new("engine-assembly", "Robust.Client.Graphics.Clyde.Shaders.base-raw.vert", "engine/base-raw.vert", 1163,
            "6c5ce9a90ad808d396bf00de62524002ed33554efd3ea914db3ea7c49c31a0be",
            EngineURL("Robust.Client/Graphics/Clyde/Shaders/base-raw.vert"))];

    private static string EngineURL(string path) =>
        $"https://github.com/space-wizards/RobustToolbox/blob/{Program.EngineCommit}/{path}";

    private static void ResolveEngineCommit(ReplayIdentity replay)
    {
        var url = new Uri($"https://api.github.com/repos/space-wizards/space-station-14/git/trees/{replay.GameBuild}");
        Console.Error.WriteLine($"Resolving engine commit for game {replay.GameBuild}.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var client = Client();
        using var response = Response(client, url, deadline.Token);
        using var input = response.Content.ReadAsStream(deadline.Token);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = input.ReadAsync(buffer, deadline.Token).AsTask().GetAwaiter().GetResult()) != 0)
        {
            if (bytes.Length + count > MaxMetadataBytes) throw new InvalidDataException("Build tree metadata exceeds 64 KiB.");
            bytes.Write(buffer, 0, count);
        }
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { AllowDuplicateProperties = false });
        var tree = document.RootElement;
        if (tree.GetProperty("sha").GetString() != replay.GameBuild || tree.GetProperty("truncated").GetBoolean())
            throw new InvalidDataException("Authoritative game tree identity is incomplete or mismatched.");
        var gitlinks = tree.GetProperty("tree").EnumerateArray().Where(entry => entry.GetProperty("path").GetString() == "RobustToolbox").ToArray();
        if (gitlinks.Length != 1 || gitlinks[0].GetProperty("type").GetString() != "commit"
            || gitlinks[0].GetProperty("mode").GetString() != "160000"
            || gitlinks[0].GetProperty("sha").GetString() != Program.EngineCommit)
            throw new InvalidDataException("The game's authoritative engine commit does not match this compiled reader.");
    }

    private static void Validate(string root, string directory, ResourceScope scope)
    {
        var path = CacheFile(root, directory, ManifestName, MaxMetadataBytes);
        var manifest = JsonSerializer.Deserialize<ResourceManifest>(File.ReadAllBytes(path), Json);
        var notice = new CachedEngineNotice(NoticePath, 1074, NoticeHash, EngineCopyright);
        if (manifest == null || manifest.Schema != "ss14-replay-resources/0.1" || manifest.Scope != scope
            || manifest.ClientZip == null || manifest.ClientZip.LocalPath != "SS14.Client.zip"
            || manifest.ClientZip.Sha256 != scope.BundleSha256 || manifest.EngineFiles == null
            || !manifest.EngineFiles.SequenceEqual(EngineFiles()) || manifest.EngineNotice != notice)
            throw new InvalidDataException("Resource cache manifest does not match the complete supported build scope.");
        Verify(CacheFile(root, directory, "SS14.Client.zip", MaxArchiveBytes), scope.BundleSha256, manifest.ClientZip.SizeBytes);
        foreach (var file in manifest.EngineFiles)
            Verify(CacheFile(root, directory, file.LocalPath, Program.MaxShaderSourceFileBytes), file.Sha256, file.SizeBytes);
        Verify(CacheFile(root, directory, notice.LocalPath, MaxMetadataBytes), notice.Sha256, notice.SizeBytes);
    }

    private static void CheckCacheDirectory(string root, string directory, bool allowMissing = false)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(directory));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Resource cache path escapes its root.");
        var current = root;
        var components = relative == "." ? Array.Empty<string>()
            : relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i <= components.Length; i++)
        {
            if (i != 0) current = Path.Combine(current, components[i - 1]);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) when (allowMissing) { return; }
            catch (DirectoryNotFoundException) when (allowMissing) { return; }
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Resource cache requires ordinary directories without links: {current}.");
        }
    }

    private static string CacheFile(string root, string directory, string relative, long maximum)
    {
        var path = Path.GetFullPath(Path.Combine(directory, relative));
        CheckCacheDirectory(root, Path.GetDirectoryName(path)!);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException($"Resource cache requires regular files without links: {path}.");
        // Unix FIFOs, sockets and devices have zero stat size: reject them before opening could block.
        if (new FileInfo(path).Length is var length && (length <= 0 || length > maximum))
            throw new InvalidDataException($"Resource cache file size mismatch or bound exceeded: {path}.");
        return path;
    }

    private static void Verify(string path, string expectedHash, long? expectedBytes = null)
    {
        var length = new FileInfo(path).Length;
        if (length is <= 0 or > MaxArchiveBytes || expectedBytes != null && length != expectedBytes)
            throw new InvalidDataException($"Resource file size mismatch or bound exceeded: {path}.");
        using var input = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(input)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Resource SHA-256 mismatch: {path}. Supply matching resources or remove this invalid cache entry.");
    }

    private static HttpClient Client()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SS14-Replay-Reader/0.8");
        return client;
    }

    private static HttpResponseMessage Response(HttpClient client, Uri url, CancellationToken token)
    {
        var host = url.Host;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.OK) return response;
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                var next = location == null ? null : new Uri(url, location);
                response.Dispose();
                if (next == null || next.Scheme != Uri.UriSchemeHttps || next.Host != host
                    || next.Port != 443 || next.UserInfo != "")
                    throw new InvalidDataException("Resource redirect leaves its authoritative HTTPS host.");
                url = next;
                continue;
            }
            var status = (int) response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"Resource service returned {status}; the completed cache remains unchanged.");
        }
        throw new HttpRequestException("Resource redirect limit exceeded.");
    }

    private static void Download(Uri url, string destination, string expectedHash, long maximum)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var client = Client();
        Console.Error.WriteLine($"Downloading {url}");
        using var response = Response(client, url, deadline.Token);
        if (response.Content.Headers.ContentLength is long length && length > maximum)
            throw new InvalidDataException("Resource download exceeds its size bound.");
        using var input = response.Content.ReadAsStream(deadline.Token);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        int count;
        while ((count = input.ReadAsync(buffer, deadline.Token).AsTask().GetAwaiter().GetResult()) != 0)
        {
            total += count;
            if (total > maximum) throw new InvalidDataException("Resource download exceeds its size bound.");
            output.Write(buffer, 0, count);
            hash.AppendData(buffer, 0, count);
        }
        if (total == 0 || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Downloaded resource SHA-256 mismatch; nothing was published.");
        output.Flush(flushToDisk: true);
    }
}
