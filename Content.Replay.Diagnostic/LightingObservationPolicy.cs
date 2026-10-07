namespace Content.Replay.Diagnostic;

internal static class LightingObservationPolicy
{
    public const string Capability = "native-point-map-lighting-observations/1";
    public const string OccluderCapability = "native-light-occluder-observations/1";
    public const int MaxOccluderVertices = 8;
    public const int MaxOwners = 250_000;
    public const long MaxRetainedBytes = 64L * 1024 * 1024;

    public static bool Parse(PresentationPolicy presentation, string? value)
    {
        var enabled = value != null && bool.Parse(value);
        if (enabled && presentation != PresentationPolicy.Exact)
            throw new ArgumentException("Lighting observations require exact presentation; visual plus lighting is unsupported.");
        return enabled;
    }

    public static string SceneSchema(PresentationPolicy presentation, bool enabled)
        => enabled ? "ss14-diagnostic/0.10" : presentation.SceneSchema;

    public static string SummarySchema(PresentationPolicy presentation, bool enabled)
        => enabled ? "ss14-diagnostic-summary/0.10" : presentation.SummarySchema;
}
