using Content.Replay.Diagnostic;
using System.Numerics;
using System.Text.Json;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

var exact = PresentationPolicy.Parse(null);
var visual = PresentationPolicy.Parse("visual");
Check(exact == PresentationPolicy.Exact && PresentationPolicy.Parse("exact") == exact, "Exact must be the default.");
Check(exact.SceneSchema == "ss14-diagnostic/0.8" && exact.SummarySchema == "ss14-diagnostic-summary/0.8", "Exact schema changed.");
Check(exact.Capability == "native-sprite-presentation-samples/1", "Exact capability changed.");
Check(visual.SceneSchema == "ss14-diagnostic/0.9" && visual.SummarySchema == "ss14-diagnostic-summary/0.9", "Visual schema mismatch.");
Check(visual.Capability == "native-sprite-visual-presentation-samples/1" && visual.Capability != exact.Capability, "Presentation capabilities must be exclusive.");
try
{
    PresentationPolicy.Parse("minute-preview");
    throw new InvalidOperationException("Duration profiles must not select presentation policy.");
}
catch (ArgumentException) { checks++; }

LayerPhaseValue[] original = [new(2, 0, 0.2f, true, false)];
LayerPhaseValue[] countdown = [original[0] with { AnimationTimeLeft = 0.1666667f }];
Check(!exact.LayersEqual(original, countdown), "Exact must retain countdown changes.");
Check(visual.LayersEqual(original, countdown), "Visual must suppress countdown-only updates/reset.");
Check(exact.LayersEqual(original, original) && visual.LayersEqual(original, original), "Identical values changed.");
foreach (var changed in new[] {
    original[0] with { Index = 3 }, original[0] with { AnimationFrame = 1 },
    original[0] with { AutoAnimated = false }, original[0] with { Reversed = true } })
{
    Check(!visual.LayersEqual(original, [changed]), "Visual controls/frame/membership must dirty a sample.");
    Check(!exact.LayersEqual(original, [changed]), "Exact controls/frame/membership must dirty a sample.");
}
Check(!visual.LayersEqual(original, []), "Removed phase membership must dirty a sample.");
Check(visual.LayersEqual([], []) && exact.LayersEqual([], []), "Empty phase membership must remain valid.");
Check(!visual.LayersEqual([new(2, 0, 0.2f, true, false), new(4, 0, 0.1f, true, false)],
    [new(4, 0, 0.1f, true, false), new(2, 0, 0.2f, true, false)]), "Layer order must remain significant.");

Check(PresentationPolicy.RequiresBaseline(false, 0, 7), "Initial/new/returning exported sprite owner needs a baseline.");
Check(PresentationPolicy.RequiresBaseline(true, 0, 7), "Exported sprite loss followed by return needs a baseline.");
Check(PresentationPolicy.RequiresBaseline(true, 7, 8), "Changed sprite ID needs a baseline.");
Check(!PresentationPolicy.RequiresBaseline(true, 7, 7), "Same wire IDs must not exempt PVS/instance/countdown-only resets.");
Check(!PresentationPolicy.RequiresBaseline(true, 7, 0), "Sprite loss clears state instead of emitting a baseline.");
Check(!PresentationPolicy.RequiresBaseline(false, 0, 0), "An owner without a sprite has no presentation baseline.");
Console.WriteLine($"Presentation policy/baseline checks passed: {checks}. No engine or replay started.");

void Reject(Action action, string message)
{
    try { action(); }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or FormatException) { checks++; return; }
    throw new InvalidOperationException(message);
}
Check(!LightingObservationPolicy.Parse(exact, null) && !LightingObservationPolicy.Parse(visual, "false"), "Lighting must remain opt-in.");
Check(LightingObservationPolicy.SceneSchema(exact, false) == exact.SceneSchema
    && LightingObservationPolicy.SceneSchema(visual, false) == visual.SceneSchema, "Old profile schemas changed.");
Check(LightingObservationPolicy.Parse(exact, "true")
    && LightingObservationPolicy.SceneSchema(exact, true) == "ss14-diagnostic/0.10"
    && LightingObservationPolicy.SummarySchema(exact, true) == "ss14-diagnostic-summary/0.10", "Lighting profile mismatch.");
Reject(() => LightingObservationPolicy.Parse(visual, "true"), "Visual plus lighting must fail before engine startup.");
Reject(() => LightingObservationPolicy.Parse(exact, "yes"), "Malformed opt-in must fail.");

const int clientOwnerId = (1 << 30) | 3;
var point = new PointLightObservation(clientOwnerId, 4, false, true, new(0, 0.5f, 1, 0), new(0, -1),
    0, 5, 1, 6.8f, 0, false, 0, false, "cone", new("none"));
var map = new MapLightingObservation(4, false, false, null);
var inventory = new LightingObservationInventory();
inventory.Begin(); inventory.Observe(point); inventory.Observe(map);
var baseline = inventory.Finish();
Check(baseline.Complete && baseline.PointReplacements.SequenceEqual([point])
    && baseline.MapReplacements.SequenceEqual([map]) && baseline.PointDeletes.Count == 0 && baseline.MapDeletes.Count == 0,
    "Initial native inventory must preserve disabled/client owners and absent ambient.");
inventory.Begin(); inventory.Observe(point); inventory.Observe(map);
var held = inventory.Finish();
Check(held.Complete && held.PointReplacements.Count == 0 && held.MapReplacements.Count == 0,
    "Stable scalar observations should emit an explicitly complete empty group.");
var image = point with { ActualMask = new("image", 9) };
inventory.Begin(); inventory.Observe(image); inventory.Observe(map with { AmbientPresent = true, AmbientLinear = new(0, 0, 0, 1) });
var lightingChanged = inventory.Finish();
Check(lightingChanged.PointReplacements.Single() == image && lightingChanged.MapReplacements.Single().AmbientPresent,
    "Resolved mask and ambient changes must dirty full observations.");
inventory.Begin(); inventory.Observe(point with { ActualMask = new("unavailable", Reason: "native generated texture") }); inventory.Observe(map);
var absent = inventory.Finish();
Check(absent.MapReplacements.Single() == map && absent.PointReplacements.Single().ActualMask.Kind == "unavailable",
    "Ambient removal and unavailable actual mask must remain distinct from zero/none.");
inventory.Begin();
var removed = inventory.Finish();
Check(removed.PointDeletes.SequenceEqual([clientOwnerId]) && removed.MapDeletes.SequenceEqual([4])
    && inventory.RetainedBytes == 0, "Component/owner disappearance needs explicit removals and released owned values.");
inventory.Begin(); inventory.Observe(point);
Check(inventory.Finish().PointReplacements.Single() == point, "A returning owner needs its full baseline.");
Reject(() => LightingObservationInventory.Validate(point with { Energy = float.NaN }), "Nonfinite native scalar was accepted.");
Reject(() => LightingObservationInventory.Validate(point with { ColorSrgb = new(0, 0, 0, float.PositiveInfinity) }), "Nonfinite color was accepted.");
Reject(() => LightingObservationInventory.Validate(point with { OwnerId = 0 }), "Zero entity identity was accepted.");
Reject(() => LightingObservationInventory.Validate(point with { OwnerId = -3 }), "Negative native network owner identity was accepted.");
Reject(() => LightingObservationInventory.Validate(point with { MapEntityId = -4 }), "Negative native network map reference was accepted.");
Reject(() => inventory.Observe(map with { OwnerId = -4 }), "Negative native network map identity was accepted.");
Reject(() => LightingObservationInventory.Validate(point with { ActualMask = new("none", 2) }), "Conflicting mask fields were accepted.");
Reject(() => LightingObservationInventory.Validate(point with { ActualMask = new("image", 0) }), "Invalid image identity was accepted.");
Reject(() => LightingObservationInventory.Validate(point with { ActualMask = new("unavailable", Reason: "") }), "Empty unavailable reason was accepted.");
Reject(() => LightingObservationInventory.Validate(point with { DeclaredMaskId = new string('é', 513) }), "UTF-8 mask identifier limit was bypassed.");
var boundedOwners = new LightingObservationInventory(maxOwners: 1);
boundedOwners.Begin(); boundedOwners.Observe(point);
Reject(() => boundedOwners.Observe(map), "Combined point/map owner budget was bypassed.");
var boundedBytes = new LightingObservationInventory(maxBytes: 100);
boundedBytes.Begin();
Reject(() => boundedBytes.Observe(point), "Lighting live/staged byte budget was bypassed.");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
using var pointJson = JsonDocument.Parse(JsonSerializer.Serialize(point, json));
Check(pointJson.RootElement.GetProperty("mapEntityId").GetInt32() == 4
    && !pointJson.RootElement.GetProperty("enabled").GetBoolean()
    && pointJson.RootElement.GetProperty("actualMask").EnumerateObject().Count() == 1,
    "False/zero/none source fields must remain explicit without irrelevant mask keys.");
using var nullPoint = JsonDocument.Parse(JsonSerializer.Serialize(point with { MapEntityId = null, DeclaredMaskId = null }, json));
Check(nullPoint.RootElement.GetProperty("mapEntityId").ValueKind == JsonValueKind.Null
    && nullPoint.RootElement.GetProperty("declaredMaskId").ValueKind == JsonValueKind.Null, "Nullable source fields must remain explicit.");
using var mapJson = JsonDocument.Parse(JsonSerializer.Serialize(map, json));
Check(mapJson.RootElement.GetProperty("ambientLinear").ValueKind == JsonValueKind.Null
    && !mapJson.RootElement.GetProperty("ambientPresent").GetBoolean(), "Missing ambient must remain explicitly absent.");
var many = new LightingChanges(true, Enumerable.Range(1, 1001).Select(id => point with { OwnerId = id }).ToList(), [], [map], [], [], []);
Check(many.Chunk(0, true).PointReplacements.Count() == 1000 && many.Chunk(1, true).PointReplacements.Count() == 1
    && !many.Chunk(1, true).MapReplacements.Any() && many.Chunk(1, true).Complete, "Initial chunks must collectively preserve complete lighting membership.");
Console.WriteLine($"Presentation and lighting policy/lifetime/shape/budget checks passed: {checks}. No engine or replay started.");

var beforeOccluders = checks;
Vector2[] clockwise = [new(-0.5f, 0.5f), new(0.5f, 0.5f), new(0.5f, -0.5f), new(-0.5f, -0.5f)];
var occluders = new LightingObservationInventory();
occluders.Begin(); occluders.ObserveOccluder(clientOwnerId, false, clockwise, 5);
var occluderBaseline = occluders.Finish().OccluderReplacements.Single();
Check(!occluderBaseline.Enabled && occluderBaseline.SharedEdges == 5
    && occluderBaseline.LocalVertices.SequenceEqual(clockwise.Select(v => new LightingOffset(v.X, v.Y))),
    "Clockwise native geometry, edge indices and disabled client owner must remain exact.");
clockwise[0] = new(-1, 0.5f);
Check(occluderBaseline.LocalVertices[0] == new LightingOffset(-0.5f, 0.5f), "Borrowed native array mutated owned geometry.");
clockwise[0] = new(-0.5f, 0.5f);
occluders.Begin(); occluders.ObserveOccluder(clientOwnerId, false, clockwise, 5);
Check(occluders.Finish().OccluderReplacements.Count == 0, "Unchanged geometry allocated/emitted a replacement.");
occluders.Begin(); occluders.ObserveOccluder(clientOwnerId, true, clockwise, 10);
var scalarOccluder = occluders.Finish().OccluderReplacements.Single();
Check(scalarOccluder.Enabled && scalarOccluder.SharedEdges == 10
    && ReferenceEquals(occluderBaseline.LocalVertices, scalarOccluder.LocalVertices),
    "Enabled/shared-mask-only replacement must reuse immutable owned geometry.");
var counterclockwise = clockwise.Reverse().ToArray();
occluders.Begin(); occluders.ObserveOccluder(clientOwnerId, true, counterclockwise, 10);
Check(occluders.Finish().OccluderReplacements.Count == 0, "Native full reversal changed normalized geometry or remapped mask.");
counterclockwise[3] = new(-1, 0.5f);
occluders.Begin(); occluders.ObserveOccluder(clientOwnerId, false, counterclockwise, 1);
var geometryOccluder = occluders.Finish().OccluderReplacements.Single();
Check(geometryOccluder.LocalVertices[0] == new LightingOffset(-1, 0.5f) && geometryOccluder.SharedEdges == 1
    && !ReferenceEquals(scalarOccluder.LocalVertices, geometryOccluder.LocalVertices),
    "Changed counterclockwise geometry needs its own clockwise copy without edge-mask remapping.");
counterclockwise[3] = new(-2, 0.5f);
Check(geometryOccluder.LocalVertices[0] == new LightingOffset(-1, 0.5f), "Changed geometry borrowed its native array.");
occluders.Begin();
var occluderRemoval = occluders.Finish();
Check(occluderRemoval.OccluderDeletes.SequenceEqual([clientOwnerId]) && occluders.OccluderCount == 0
    && occluders.RetainedBytes == 0, "Component/owner removal must release geometry and emit a delete.");
var invalidOccluders = new LightingObservationInventory();
invalidOccluders.Begin();
Reject(() => invalidOccluders.ObserveOccluder(0, true, clockwise, 0), "Zero occluder identity accepted.");
Reject(() => invalidOccluders.ObserveOccluder(1, true, clockwise[..2], 0), "Too few vertices accepted.");
Reject(() => invalidOccluders.ObserveOccluder(1, true, Enumerable.Repeat(Vector2.One, 9).ToArray(), 0), "Too many vertices accepted.");
Reject(() => invalidOccluders.ObserveOccluder(1, true, clockwise, 16), "Out-of-range shared-edge bit accepted.");
Reject(() => invalidOccluders.ObserveOccluder(1, true, [new(float.NaN, 0), clockwise[1], clockwise[2]], 0), "Nonfinite geometry accepted.");
Reject(() => invalidOccluders.ObserveOccluder(1, true, [clockwise[0], clockwise[1], clockwise[0]], 0), "Duplicate vertex accepted.");
Reject(() => invalidOccluders.ObserveOccluder(1, true, [new(0, 0), new(1, 0), new(2, 0)], 0), "Degenerate polygon accepted.");
Reject(() => invalidOccluders.ObserveOccluder(1, true, [new(0, 0), new(2, 0), new(1, 0.5f), new(2, 2), new(0, 2)], 0), "Concave polygon accepted.");
var combinedOwners = new LightingObservationInventory(maxOwners: 2);
combinedOwners.Begin(); combinedOwners.Observe(point); combinedOwners.Observe(map);
Reject(() => combinedOwners.ObserveOccluder(5, true, clockwise, 0), "Occluders bypassed combined lighting owner budget.");
var occluderBytes = new LightingObservationInventory(maxBytes: 200);
occluderBytes.Begin();
Reject(() => occluderBytes.ObserveOccluder(1, true, clockwise, 0), "Occluders bypassed combined live/staged byte budget.");
using var occluderJson = JsonDocument.Parse(JsonSerializer.Serialize(occluderBaseline, json));
Check(occluderJson.RootElement.GetProperty("ownerId").GetInt32() == clientOwnerId
    && !occluderJson.RootElement.GetProperty("enabled").GetBoolean()
    && occluderJson.RootElement.GetProperty("sharedEdges").GetByte() == 5
    && occluderJson.RootElement.GetProperty("localVertices")[0].GetProperty("y").GetSingle() == 0.5f,
    "Occluder wire fields/values changed.");
var manyOccluders = many with { OccluderReplacements = Enumerable.Range(1, 1001)
    .Select(id => occluderBaseline with { OwnerId = id }).ToList() };
Check(manyOccluders.Chunk(0, true).OccluderReplacements.Count() == 1000
    && manyOccluders.Chunk(1, true).OccluderReplacements.Count() == 1,
    "Snapshot chunks lost complete occluder inventory.");
using var emptyLightingJson = JsonDocument.Parse(JsonSerializer.Serialize(new LightingChanges(true, [], [], [], [], [], []).Chunk(0, false), json));
Check(emptyLightingJson.RootElement.GetProperty("occluderReplacements").GetArrayLength() == 0
    && emptyLightingJson.RootElement.GetProperty("occluderDeletes").GetArrayLength() == 0,
    "Required empty occluder replacement/removal arrays were omitted.");
Check(LightingObservationPolicy.OccluderCapability == "native-light-occluder-observations/1"
    && LightingObservationPolicy.MaxOccluderVertices == 8, "Occluder capability/native hull bound changed.");
Console.WriteLine($"Focused native occluder winding/ownership/removal/budget checks passed: {checks - beforeOccluders}. No engine or replay started.");
