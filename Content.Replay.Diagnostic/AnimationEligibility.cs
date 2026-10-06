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
