using Content.Replay.Diagnostic;
using System.Text.Json;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
void Reject(Action operation, string message)
{
    try { operation(); }
    catch (InvalidDataException) { checks++; return; }
    throw new Exception(message);
}

Check(RoofObservationInventory.Key(7, new(-1, -8)) == new RoofChunkKey(7, -1, -1), "Negative chunk floor.");
Check(RoofObservationInventory.Key(7, new(-9, 8)) == new RoofChunkKey(7, -2, 1), "Boundary chunk floor.");
LightingColor first = new(0.75f, 0.125f, 0.5f, 0.25f), second = new(0, 0, 0, 1);
RoofSample[] native = [new(new(1, 1), first), new(new(0, 0), second), new(new(2, 1), first), new(new(-1, 0), second)];
var inventory = new RoofObservationInventory();
inventory.Begin(); inventory.ReplaceGrid(7, 2, native);
var baseline = inventory.Finish();
Check(baseline.GridReplacements.Count == 1 && baseline.ChunkReplacements.Count == 2 && inventory.TileCount == 4, "Full baseline.");
var key = new RoofChunkKey(7, 0, 0); var original = inventory.GetChunk(key);
Check(original.Layers[0].ColorSrgb == first && original.Layers[1].ColorSrgb == second, "Preserve first-encounter layer order and alpha.");
Check(original.Layers[0].Tiles.SequenceEqual(new RoofTile[] { new(1, 1), new(2, 1) }), "Preserve native tile order.");
native[0] = new(new(7, 7), second);
Check(original.Layers[0].Tiles[0] == new RoofTile(1, 1), "Never retain mutable native storage.");
try { ((IList<RoofTile>) original.Layers[0].Tiles)[0] = new(7, 7); throw new Exception("Published geometry is mutable."); }
catch (NotSupportedException) { checks++; }
native[0] = new(new(1, 1), first);
inventory.Begin(); inventory.ReplaceGrid(7, 2, native);
var stable = inventory.Finish();
Check(stable.GridReplacements.Count == 0 && stable.ChunkReplacements.Count == 0, "Stable native input emits no change.");
Check(ReferenceEquals(original, inventory.GetChunk(key)), "Stable chunks share owned immutable geometry.");
inventory.Begin(); inventory.ReplaceGrid(7, 2, native, true);
Check(inventory.Finish().GridReplacements.Single().Implicit && inventory.GetChunk(key) == original,
    "Native pass classification changes without replacing geometry.");
inventory.Begin(); inventory.ReplaceGrid(7, 2, native); inventory.Finish();

LightingColor changed = new(0.5f, 0.75f, 0.125f, 0.125f);
native[0] = native[0] with { ColorSrgb = changed }; native[2] = native[2] with { ColorSrgb = changed };
inventory.Begin(); inventory.ReplaceGrid(7, 2, native);
var colorChange = inventory.Finish();
Check(colorChange.ChunkReplacements.Count == 1 && colorChange.GridReplacements.Count == 0, "Color change is local.");
Check(ReferenceEquals(original.Layers[0].Tiles, inventory.GetChunk(key).Layers[0].Tiles), "Pure color changes reuse tile geometry.");
Check(inventory.GetChunk(key).Layers[0].ColorSrgb.A == 0.125f, "Preserve exact fractional alpha.");
inventory.Begin(); inventory.ReplaceGrid(7, 2, [native[1], native[0], native[2], native[3]]);
Check(inventory.Finish().ChunkReplacements.Count == 1 && inventory.GetChunk(key).Layers[0].ColorSrgb == second, "Ordered layer changes are retained.");

inventory.Begin(); inventory.ReplaceGrid(7, 3, []);
var empty = inventory.Finish();
Check(empty.ChunkDeletes.Count == 2 && empty.GridDeletes.Count == 0 && inventory.GridCount == 1 && inventory.TileCount == 0, "Observed empty coverage preserves grid membership.");
Check(empty.GridReplacements.Single().TileSize == 3, "Tile size changes are explicit.");
inventory.Begin(); inventory.ReplaceGrid(7, 3, [new(new(0, 0), second)]); inventory.Finish();
inventory.Begin(); inventory.DeleteGrid(7);
var deleted = inventory.Finish();
Check(deleted.GridDeletes.SequenceEqual(new[] { 7 }) && deleted.ChunkDeletes.Count == 0 && inventory.ChunkCount == 0, "Grid deletion cascades once.");
Check(inventory.RetainedBytes == 0, "Removal retires ownership.");

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
using var wire = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(baseline.Chunk(0, true), json));
Check(wire.RootElement.GetProperty("complete").GetBoolean() && wire.RootElement.GetProperty("gridDeletes").GetArrayLength() == 0
    && wire.RootElement.GetProperty("chunkDeletes").GetArrayLength() == 0, "Required complete/deletion wire fields.");
Check(!wire.RootElement.GetProperty("gridReplacements")[0].GetProperty("implicit").GetBoolean(), "Explicit false pass classification is present.");
Check(wire.RootElement.GetProperty("chunkReplacements")[1].GetProperty("layers")[0].GetProperty("colorSrgb").GetProperty("a").GetSingle() == first.A, "Wire retains color alpha.");
Reject(() => new RoofObservationInventory().ReplaceGrid(1, 1, [new(new(0, 0), first), new(new(0, 0), second)]), "Duplicate tile admitted.");
Reject(() => new RoofObservationInventory().ReplaceGrid(1, 1, [new(new(0, 0), first with { R = float.NaN })]), "Nonfinite color admitted.");
Reject(() => new RoofObservationInventory().ReplaceGrid(1, 0, []), "Invalid tile size admitted.");
Reject(() => new RoofObservationInventory(128).ReplaceGrid(1, 1, native), "Live/staged bound ignored.");
Reject(() => new RoofObservationInventory(128).SetOutsideBytes(129), "Invalidation residency ignored.");
inventory.Begin(); inventory.ReplaceGrid(9, 1, [new(new(1, 1), second)]); inventory.Reset();
Check(inventory.GridCount == 0 && inventory.ChunkCount == 0 && inventory.TileCount == 0 && inventory.RetainedBytes == 0, "Close clears owned inventory.");
Console.WriteLine($"{checks} focused roof ownership/order/wire/bound checks passed.");
