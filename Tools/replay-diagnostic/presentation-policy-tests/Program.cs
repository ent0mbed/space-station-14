using Content.Replay.Diagnostic;

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
