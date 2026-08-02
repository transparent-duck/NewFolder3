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
    bool MazerootUsableThisFloor);

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
    public const int PolicyVersion = 2;

    public static SightResearchDecision Decide(in SightResearchSnapshot snapshot)
    {
        if (!snapshot.StableFloor)
            return new SightResearchDecision(SightResearchDecisionKind.Pending, "floor-not-stable");

        if (snapshot.IntuitionResolution == IntuitionFloorResolution.Negative)
            return new SightResearchDecision(SightResearchDecisionKind.Ineligible, "intuition-negative");

        if (snapshot.IntuitionResolution != IntuitionFloorResolution.Positive)
            return new SightResearchDecision(SightResearchDecisionKind.Pending, "intuition-unresolved");

        if (!snapshot.ExactHoardIndicatorAvailable)
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
