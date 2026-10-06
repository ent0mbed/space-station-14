using System.Text.Json;
using Content.Replay.Diagnostic;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

void Throws<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { checks++; return; }
    throw new InvalidOperationException(message);
}

var ids = new DefinitionIdAllocator();
var firstStore = new DefinitionStore<Definition>(ids);
var firstId = firstStore.Add("first", static (id, value) => new(id, value));
var secondId = firstStore.Add("second", static (id, value) => new(id, value));
Check(firstId == 1 && secondId == 2, "Fresh IDs must preserve ordinary contiguous output.");
Check(firstStore.Count == 2, "Resident count must count values.");
Check(firstStore.Get(firstId).Value == "first", "Later insertion changed an existing ID lookup.");
var original = firstStore.Get(secondId);
Check(ReferenceEquals(original, firstStore.Get(secondId)), "Lookup must retain the original owned value.");
Throws<KeyNotFoundException>(() => firstStore.Get(0), "Zero is reserved, never a stored definition.");
Throws<KeyNotFoundException>(() => firstStore.Get(3), "Missing IDs must fail rather than alias an index.");

var emitted = new List<Definition>();
firstStore.WritePending(emitted.Add);
Check(emitted.Select(value => value.Id).SequenceEqual([1, 2]), "Emission changed creation order.");
firstStore.WritePending(emitted.Add);
Check(emitted.Count == 2, "Draining a second time emitted old definitions again.");
Check(ReferenceEquals(original, firstStore.Get(secondId)), "Emission changed resident ownership.");
var thirdId = firstStore.Add("third", static (id, value) => new(id, value));
firstStore.WritePending(emitted.Add);
Check(thirdId == 3 && emitted.Select(value => value.Id).SequenceEqual([1, 2, 3]),
    "Emission incorrectly reset allocation or included old definitions.");

// A new resident store can share the ID lifetime without inheriting old values or pending indices.
var laterStore = new DefinitionStore<Definition>(ids);
var fourthId = laterStore.Add("fourth", static (id, value) => new(id, value));
Check(fourthId == 4 && laterStore.Count == 1, "ID allocation still depends on resident count.");
Check(laterStore.Get(4).Value == "fourth", "Sparse lookup incorrectly uses ID minus one.");
Throws<KeyNotFoundException>(() => laterStore.Get(1), "The new store unexpectedly owns another store's values.");
laterStore.WritePending(value => Check(value.Id == 4, "Sparse pending emission used a resident position."));
Check(firstStore.Get(1).Value == "first", "A second store altered the original store.");

var separateKind = new DefinitionStore<Definition>(new());
Check(separateKind.Add("resource", static (id, value) => new(id, value)) == 1, "Definition kinds must have independent ID spaces.");
Throws<ArgumentOutOfRangeException>(() => new DefinitionIdAllocator(-1), "Negative allocator state was accepted.");

var nearLimit = new DefinitionIdAllocator(int.MaxValue - 1);
var limited = new DefinitionStore<Definition>(nearLimit);
Check(limited.Add("last", static (id, value) => new(id, value)) == int.MaxValue, "The last positive ID must remain usable.");
bool[] factoryCalled = [false];
Throws<OverflowException>(() => limited.Add(factoryCalled, static (id, called) => { called[0] = true; return new(id, "overflow"); }),
    "Definition allocation wrapped or reused an ID.");
Check(!factoryCalled[0] && limited.Count == 1, "Overflow changed residence or invoked the factory.");
Throws<OverflowException>(() => nearLimit.Allocate(), "Overflow changed allocator state and permitted reuse.");
Check(limited.Get(int.MaxValue).Value == "last", "Overflow corrupted the existing sparse value.");

var pendingFailure = new DefinitionStore<Definition>(new());
pendingFailure.Add("retry", static (id, value) => new(id, value));
Throws<IOException>(() => pendingFailure.WritePending(_ => throw new IOException()),
    "Writer failure must propagate.");
var resumed = new List<Definition>();
pendingFailure.WritePending(resumed.Add);
pendingFailure.WritePending(resumed.Add);
Check(resumed.Count == 1 && resumed[0].Id == 1, "An unacknowledged pending value was lost or duplicated.");

var factoryFailure = new DefinitionStore<Definition>(new());
Throws<IOException>(() => factoryFailure.Add("unused", static (_, _) => throw new IOException()),
    "Factory failure must propagate before committing residence or pending emission.");
Check(factoryFailure.Count == 0, "Factory failure committed a resident value.");
Check(factoryFailure.Add("after-failure", static (id, value) => new(id, value)) == 2,
    "Factory failure reused the allocated ID.");
var afterFailure = new List<Definition>();
factoryFailure.WritePending(afterFailure.Add);
Check(afterFailure.Count == 1 && afterFailure[0].Id == 2 && afterFailure[0].Value == "after-failure",
    "Explicit factory state or failure changed pending emission.");

if (args.Length > 0)
{
    if (args.Length != 2 || args[0] != "--scene-fixture")
        throw new ArgumentException("Optional input: --scene-fixture /path/to/existing/scene.jsonl");
    CheckFixture(args[1]);
}

Console.WriteLine($"Definition identity/store checks passed: {checks}. No engine or replay started.");

void CheckFixture(string path)
{
    string[] order = ["resource-definition", "shader-source-definition", "material-definition", "sprite-definition"];
    var idProperties = new Dictionary<string, string>
    {
        [order[0]] = "resourceId", [order[1]] = "shaderSourceId",
        [order[2]] = "materialId", [order[3]] = "spriteId"
    };
    var stores = order.ToDictionary(kind => kind, _ => new DefinitionStore<Definition>(new()));
    var expected = new List<string>();
    var actual = new List<string>();
    var frames = 0;
    foreach (var line in File.ReadLines(path))
    {
        using var document = JsonDocument.Parse(line);
        var record = document.RootElement;
        var kind = record.GetProperty("kind").GetString()!;
        if (stores.TryGetValue(kind, out var store))
        {
            var recordedId = record.GetProperty(idProperties[kind]).GetInt32();
            var allocatedId = store.Add(line, static (id, value) => new(id, value));
            Check(allocatedId == recordedId, $"Fixture {kind} ID changed.");
            Check(store.Get(recordedId).Value == line, $"Fixture {kind} lookup changed its value.");
            expected.Add(line);
        }
        else if (kind is "snapshot" or "delta" or "resources")
        {
            foreach (var definitionKind in order)
                stores[definitionKind].WritePending(value => actual.Add(value.Value));
            Check(actual.SequenceEqual(expected), "Fixture definition emission order or bytes changed.");
            expected.Clear();
            actual.Clear();
            if (kind == "resources") continue;
            foreach (var entity in record.GetProperty("upserts").EnumerateArray())
            {
                var sprite = entity.GetProperty("spriteId");
                if (sprite.ValueKind == JsonValueKind.Number)
                    stores["sprite-definition"].Get(sprite.GetInt32());
            }
            if (record.GetProperty("chunkIndex").GetInt32() == 0) frames++;
        }
    }
    Check(expected.Count == 0 && frames > 0, "Fixture ends without a committed definition batch/frame.");
    Console.WriteLine($"Existing JSONL fixture: {frames} frames; " +
        string.Join(", ", order.Select(kind => $"{kind}={stores[kind].Count}")) + ".");
}

sealed record Definition(int Id, string Value);
