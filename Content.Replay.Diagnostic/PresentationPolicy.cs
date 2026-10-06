namespace Content.Replay.Diagnostic;

internal sealed class PresentationPolicy
{
    public static readonly PresentationPolicy Exact = new("exact", "0.8",
        "native-sprite-presentation-samples/1", true);
    public static readonly PresentationPolicy Visual = new("visual", "0.9",
        "native-sprite-visual-presentation-samples/1", false);

    public string Name { get; }
    public string SceneSchema { get; }
    public string SummarySchema { get; }
    public string Capability { get; }
    private readonly bool _compareCountdown;

    private PresentationPolicy(string name, string version, string capability, bool compareCountdown)
    {
        Name = name;
        SceneSchema = $"ss14-diagnostic/{version}";
        SummarySchema = $"ss14-diagnostic-summary/{version}";
        Capability = capability;
        _compareCountdown = compareCountdown;
    }

    public static PresentationPolicy Parse(string? value) => value switch
    {
        null or "exact" => Exact,
        "visual" => Visual,
        _ => throw new ArgumentException($"Unknown presentation policy: {value}. Expected exact or visual.")
    };

    // Only exported owner/sprite transitions grant a baseline. Native instance
    // recreation or PVS re-entry with the same wire IDs does not reset a sample.
    public static bool RequiresBaseline(bool ownerPreviouslyPresent, int previousSpriteId, int spriteId)
        => spriteId != 0 && (!ownerPreviouslyPresent || previousSpriteId != spriteId);

    public bool LayersEqual(ReadOnlySpan<LayerPhaseValue> previous, ReadOnlySpan<LayerPhaseValue> current)
    {
        if (_compareCountdown) return previous.SequenceEqual(current);
        if (previous.Length != current.Length) return false;
        for (var index = 0; index < previous.Length; index++)
        {
            var a = previous[index];
            var b = current[index];
            if (a.Index != b.Index || a.AnimationFrame != b.AnimationFrame
                || a.AutoAnimated != b.AutoAnimated || a.Reversed != b.Reversed) return false;
        }
        return true;
    }
}

internal readonly record struct LayerPhaseValue(int Index, int AnimationFrame, float AnimationTimeLeft,
    bool AutoAnimated, bool Reversed);
