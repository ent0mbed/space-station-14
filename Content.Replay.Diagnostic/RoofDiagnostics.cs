namespace Content.Replay.Diagnostic;

internal enum RoofDirtyReason
{
    Membership, Tiles, Contributor, GridComponent, GridScalar, Unavailable, Prototype
}

internal readonly record struct RoofInvalidationCounts(long MembershipRequests = 0, long TileRequests = 0,
    long ContributorRequests = 0, long GridComponentRequests = 0, long GridScalarRequests = 0,
    long UnavailableRequests = 0, long PrototypeRequests = 0, long ImplicitContributorSkips = 0,
    long AffectedGridAdmissions = 0)
{
    public RoofInvalidationCounts Request(RoofDirtyReason reason) => reason switch
    {
        RoofDirtyReason.Membership => this with { MembershipRequests = MembershipRequests + 1 },
        RoofDirtyReason.Tiles => this with { TileRequests = TileRequests + 1 },
        RoofDirtyReason.Contributor => this with { ContributorRequests = ContributorRequests + 1 },
        RoofDirtyReason.GridComponent => this with { GridComponentRequests = GridComponentRequests + 1 },
        RoofDirtyReason.GridScalar => this with { GridScalarRequests = GridScalarRequests + 1 },
        RoofDirtyReason.Unavailable => this with { UnavailableRequests = UnavailableRequests + 1 },
        RoofDirtyReason.Prototype => this with { PrototypeRequests = PrototypeRequests + 1 },
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    public static RoofInvalidationCounts operator +(RoofInvalidationCounts a, RoofInvalidationCounts b)
        => new(a.MembershipRequests + b.MembershipRequests, a.TileRequests + b.TileRequests,
            a.ContributorRequests + b.ContributorRequests, a.GridComponentRequests + b.GridComponentRequests,
            a.GridScalarRequests + b.GridScalarRequests, a.UnavailableRequests + b.UnavailableRequests,
            a.PrototypeRequests + b.PrototypeRequests, a.ImplicitContributorSkips + b.ImplicitContributorSkips,
            a.AffectedGridAdmissions + b.AffectedGridAdmissions);

    public static RoofInvalidationCounts operator -(RoofInvalidationCounts a, RoofInvalidationCounts b)
        => new(a.MembershipRequests - b.MembershipRequests, a.TileRequests - b.TileRequests,
            a.ContributorRequests - b.ContributorRequests, a.GridComponentRequests - b.GridComponentRequests,
            a.GridScalarRequests - b.GridScalarRequests, a.UnavailableRequests - b.UnavailableRequests,
            a.PrototypeRequests - b.PrototypeRequests, a.ImplicitContributorSkips - b.ImplicitContributorSkips,
            a.AffectedGridAdmissions - b.AffectedGridAdmissions);
}
