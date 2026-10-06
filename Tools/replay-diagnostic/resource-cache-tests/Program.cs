using System.Text.Json;
using System.Text.Json.Nodes;
using Content.Replay.Diagnostic;
using ReaderPins = Content.Replay.Diagnostic.Program;

// Exercise the production resource resolver without loading the game or engine.
if (args.Length == 6 && args[0] == "--fetch" && args[2] == "--resources" && args[4] == "--cache")
{
    var replay = ReplayIdentity.Read(args[1]);
    var result = ReplayResources.Resolve(replay, args[3], args[5], completeCache: true);
    Console.WriteLine(JsonSerializer.Serialize(new { result.ClientZip, result.Folder, nativeInvocations = 0 }));
    return;
}
if (args.Length != 2 || args[0] != "--cache")
    throw new ArgumentException("--fetch REPLAY.zip --resources SS14.Client.zip --cache NEW_DIRECTORY, or --cache VERIFIED_DIRECTORY");

var cache = Path.GetFullPath(args[1]);
var identity = new ReplayIdentity(ReaderPins.EngineVersion, ReaderPins.GameBuild, "wizards",
    "cb7f1c2e9d2397717cdff6401f1f50085c43cac9234408cdc6d101a71e87d857", null);
var directory = Path.Combine(cache, identity.GameBuild, identity.ResourceSha256, identity.EngineVersion, ReaderPins.EngineCommit);
var manifestPath = Path.Combine(directory, "resource-manifest.json");
var original = File.ReadAllBytes(manifestPath);
var manifest = JsonNode.Parse(original)!.AsObject();
var checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    checks++;
}
void Reject(Action action, string message)
{
    try { action(); }
    catch (InvalidDataException) { checks++; return; }
    throw new InvalidOperationException(message);
}
void RejectManifest(Action<JsonObject> change, string message)
{
    try
    {
        var changed = manifest.DeepClone().AsObject();
        change(changed);
        File.WriteAllText(manifestPath, changed.ToJsonString());
        Reject(() => ReplayResources.Resolve(identity, null, cache), message);
    }
    finally { File.WriteAllBytes(manifestPath, original); }
}

var resolved = ReplayResources.Resolve(identity, null, cache);
Check(resolved.Folder == directory, "Verified cache was not reused.");
Check(manifest["engineFiles"]!.AsArray().Count == 11, "Expected the exact eleven-role pilot closure.");
RejectManifest(value => value["engineFiles"]!.AsArray().RemoveAt(4), "Missing required lighting role was accepted.");
RejectManifest(value => value["engineFiles"]!.AsArray().Add(value["engineFiles"]![4]!.DeepClone()), "Duplicate engine role was accepted.");
RejectManifest(value => value["engineFiles"]![4]!["path"] = "/Shaders/Internal/light-hard.swsl", "Unrequested shader role was accepted.");
RejectManifest(value => value["engineFiles"]![4]!["sourceURL"] = "https://example.invalid/shader", "Unpinned provenance was accepted.");
RejectManifest(value => value["engineFiles"]![4]!["sha256"] = new string('0',64), "Changed pinned descriptor hash was accepted.");
Reject(() => ReplayResources.Resolve(identity with { EngineVersion = "unknown" }, null, cache), "Unsupported native replay version was accepted.");

var lightPath = Path.Combine(directory, "engine", "light-soft.swsl");
var body = File.ReadAllBytes(lightPath);
try
{
    var changed = (byte[]) body.Clone();
    changed[0] ^= 1;
    File.WriteAllBytes(lightPath, changed);
    Reject(() => ReplayResources.Resolve(identity, null, cache), "Wrong lighting body hash was accepted.");
    File.WriteAllBytes(lightPath, body[..^1]);
    Reject(() => ReplayResources.Resolve(identity, null, cache), "Truncated lighting body was accepted.");
    File.Delete(lightPath);
    var missingRejected = false;
    try { ReplayResources.Resolve(identity, null, cache); }
    catch (FileNotFoundException) { missingRejected = true; }
    Check(missingRejected, "Missing lighting body was accepted.");
}
finally { File.WriteAllBytes(lightPath, body); }
Check(ReplayResources.Resolve(identity, null, cache).Folder == directory, "Failed validation altered cache reuse.");
Console.WriteLine($"Pinned shader cache checks passed: {checks}. No native replay or engine started.");

namespace Content.Replay.Diagnostic
{
    internal static class Program
    {
        public const string GameBuild = "94087a918a2fae4571f5a529fe14ef7f5dce29a3";
        public const string EngineVersion = "289.0.3";
        public const string EngineCommit = "36905986f6809420dbc78168fc494f91723d356b";
        public const int MaxShaderSourceFileBytes = 1024 * 1024;
    }
}
