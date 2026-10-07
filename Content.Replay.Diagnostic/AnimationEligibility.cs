namespace Content.Replay.Diagnostic;

internal static class AnimationEligibility
{
    public static void RequireOwnerThread(int threadId)
    {
        if (Environment.CurrentManagedThreadId != threadId)
            throw new InvalidOperationException("Replay inspection must stay on its owning engine thread.");
    }
}

// Owned scalars only. No native component, RSI, layer, query or layer array survives
// a pass. The containing presentation-owner registry supplies the lifetime limit.
internal readonly record struct AnimationEligibilityStamp(long Instance, long Revision, long ResourceEpoch, int LayerCount);

internal readonly record struct AnimationEligibilityEntry(AnimationEligibilityStamp Stamp, bool Eligible, bool Valid)
{
    public bool TryGet(AnimationEligibilityStamp stamp, out bool eligible)
    {
        eligible = Eligible;
        return Valid && Stamp == stamp;
    }

    public static AnimationEligibilityEntry AfterScan(AnimationEligibilityStamp before,
        AnimationEligibilityStamp after, bool eligible)
        => before == after ? new(before, eligible, true) : default;
}

// Ordinary queue eligibility excludes hidden/manual layers; phase presence does
// not. Publish only a complete negative phase scan, never a cached timer value.
internal readonly record struct NegativePhaseEntry(AnimationEligibilityStamp Stamp, bool Valid)
{
    public bool Matches(AnimationEligibilityStamp stamp) => Valid && Stamp == stamp;

    public static NegativePhaseEntry AfterScan(AnimationEligibilityStamp before,
        AnimationEligibilityStamp after, int phaseCount)
        => phaseCount == 0 && before == after ? new(before, true) : default;
}
