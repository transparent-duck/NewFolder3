namespace DeepDungeon.Fsd.Core;

public enum IntuitionFloorResolution
{
    Unresolved,
    Positive,
    Negative
}

public enum SightResearchDecisionKind
{
    Pending,
    Ineligible,
    AlreadySatisfied,
    UseSight,
    UseMazeroot
}

public enum SightResearchRevealResource
{
    None,
    Sight,
    Mazeroot
}

public readonly record struct SightResearchSnapshot(
    bool StableFloor,
    IntuitionFloorResolution IntuitionResolution,
    bool ExactHoardIndicatorAvailable,
    bool AcceptedIncomingEdgeKnown,
    bool SightUseBlocked,
    int SightStock,
    int MazerootStock,
    bool RevealDispatchedThisFloor,
    bool AuthoritativeRevealConfirmed,
    bool MazerootSupported,
    bool MazerootUsableThisFloor,
    bool BandedHoardEvidenceAvailable = false);

public readonly record struct SightResearchDecision(
    SightResearchDecisionKind Kind,
    string Reason)
{
    public bool ShouldUseSight => Kind == SightResearchDecisionKind.UseSight;
    public bool ShouldUseMazeroot => Kind == SightResearchDecisionKind.UseMazeroot;
    public bool ShouldUseReveal => ShouldUseSight || ShouldUseMazeroot;
    public bool ShouldCollectJointScan =>
        ShouldUseReveal ||
        Kind == SightResearchDecisionKind.AlreadySatisfied &&
        Reason == "authoritative-reveal-already-confirmed";
    public SightResearchRevealResource RevealResource => Kind switch
    {
        SightResearchDecisionKind.UseSight => SightResearchRevealResource.Sight,
        SightResearchDecisionKind.UseMazeroot => SightResearchRevealResource.Mazeroot,
        _ => SightResearchRevealResource.None
    };
}

public static class SightResearchPolicy
{
    public const int PolicyVersion = 3;

    public static SightResearchDecision Decide(in SightResearchSnapshot snapshot)
    {
        if (!snapshot.StableFloor)
            return new SightResearchDecision(SightResearchDecisionKind.Pending, "floor-not-stable");

        if (snapshot.IntuitionResolution == IntuitionFloorResolution.Negative)
            return new SightResearchDecision(SightResearchDecisionKind.Ineligible, "intuition-negative");

        // A visible, targetable Banded coffer is an authoritative terminal hoard
        // observation even when Intuition was not used (or its chat result was
        // not observed).  PalacePal matching is performed by the caller before
        // this bit is supplied; this policy must not create a new hoard-position
        // collection path.
        if (snapshot.IntuitionResolution != IntuitionFloorResolution.Positive &&
            !snapshot.BandedHoardEvidenceAvailable)
            return new SightResearchDecision(SightResearchDecisionKind.Pending, "intuition-unresolved");

        if (!snapshot.ExactHoardIndicatorAvailable &&
            !snapshot.BandedHoardEvidenceAvailable)
            return new SightResearchDecision(SightResearchDecisionKind.Pending, "exact-indicator-unresolved");

        if (snapshot.AuthoritativeRevealConfirmed)
            return new SightResearchDecision(SightResearchDecisionKind.AlreadySatisfied, "authoritative-reveal-already-confirmed");

        if (snapshot.AcceptedIncomingEdgeKnown)
            return new SightResearchDecision(SightResearchDecisionKind.AlreadySatisfied, "accepted-incoming-edge-known");

        if (snapshot.RevealDispatchedThisFloor)
            return new SightResearchDecision(SightResearchDecisionKind.Pending, "research-reveal-awaiting-confirmation");

        if (!snapshot.SightUseBlocked && snapshot.SightStock > 0)
            return new SightResearchDecision(SightResearchDecisionKind.UseSight, "unknown-incoming-edge");

        if (snapshot.MazerootSupported &&
            snapshot.MazerootUsableThisFloor &&
            snapshot.MazerootStock > 0)
            return new SightResearchDecision(SightResearchDecisionKind.UseMazeroot, "unknown-incoming-edge");

        return new SightResearchDecision(SightResearchDecisionKind.Ineligible, "reveal-resource-unavailable");
    }
}
