namespace Content.Replay.Diagnostic;

// Fixed-size aggregates: no frame history, entity IDs, or per-layer timers.
internal sealed class FrameDiagnostics
{
    public FrameAggregate Baseline { get; } = new();
    public FrameAggregate FirstTransition { get; } = new();
    public FrameAggregate RemainingFrames { get; } = new();
    public string TimingMeaning => "Frame 0, frame 1, and frames 2+ are separate. Values are elapsed milliseconds; "
        + "apply, projection and scene output are within loop; lighting/roof/fused are within projection; "
        + "occluders are within lighting, refresh/resolve within roof. Nested intervals must not be added. "
        + "Scene output excludes final Flush and optional tile output; loop includes both frame streams. "
        + "Allocated bytes are process allocation volume sampled at frame boundaries, not retained memory. "
        + "Dirty reasons count requests before coalescing; affectedGridAdmissions counts unique queued grids.";

    public void Add(int sequence, in FrameSample sample)
    {
        if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        (sequence == 0 ? Baseline : sequence == 1 ? FirstTransition : RemainingFrames).Add(sample);
    }
}

internal readonly record struct FrameSample(double Loop, double Apply, double Projection,
    double Lighting, double Occluders, double Roof, double RoofRefresh, double RoofResolve,
    double FusedInspection, double SceneOutput, long AllocatedBytes, int Gen0, int Gen1, int Gen2,
    RoofInvalidationCounts RoofInvalidations);

internal sealed class TimingAggregate
{
    public double SumMilliseconds { get; private set; }
    public double MaximumMilliseconds { get; private set; }
    internal void Add(double milliseconds)
    {
        SumMilliseconds += milliseconds;
        MaximumMilliseconds = Math.Max(MaximumMilliseconds, milliseconds);
    }
}

internal sealed class FrameAggregate
{
    public int Frames { get; private set; }
    public TimingAggregate Loop { get; } = new();
    public TimingAggregate Apply { get; } = new();
    public TimingAggregate Projection { get; } = new();
    public TimingAggregate Lighting { get; } = new();
    public TimingAggregate Occluders { get; } = new();
    public TimingAggregate Roof { get; } = new();
    public TimingAggregate RoofRefresh { get; } = new();
    public TimingAggregate RoofResolve { get; } = new();
    public TimingAggregate FusedInspection { get; } = new();
    public TimingAggregate SceneOutput { get; } = new();
    public long AllocatedBytes { get; private set; }
    public int Gen0Collections { get; private set; }
    public int Gen1Collections { get; private set; }
    public int Gen2Collections { get; private set; }
    public RoofInvalidationCounts RoofInvalidations { get; private set; }

    internal void Add(in FrameSample sample)
    {
        Frames++;
        Loop.Add(sample.Loop); Apply.Add(sample.Apply); Projection.Add(sample.Projection);
        Lighting.Add(sample.Lighting); Occluders.Add(sample.Occluders); Roof.Add(sample.Roof);
        RoofRefresh.Add(sample.RoofRefresh); RoofResolve.Add(sample.RoofResolve);
        FusedInspection.Add(sample.FusedInspection); SceneOutput.Add(sample.SceneOutput);
        AllocatedBytes += sample.AllocatedBytes;
        Gen0Collections += sample.Gen0; Gen1Collections += sample.Gen1; Gen2Collections += sample.Gen2;
        RoofInvalidations += sample.RoofInvalidations;
    }
}
