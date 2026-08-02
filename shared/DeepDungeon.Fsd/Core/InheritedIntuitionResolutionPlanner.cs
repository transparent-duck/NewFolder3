namespace DeepDungeon.Fsd.Core;

public enum InheritedIntuitionEvidenceKind
{
    None,
    HoardPresent,
    NoHoard,
    Rejected
}

public enum InheritedIntuitionResolutionSource
{
    None,
    HoardPresent,
    NoHoardInferred,
    InvalidNoHoardMessage,
    RejectedEvidence
}

public readonly record struct InheritedIntuitionResolutionDecision(
    bool Terminal,
    bool HoardPresent,
    bool NoHoard,
    bool IsError,
    InheritedIntuitionResolutionSource Source,
    int ElapsedMilliseconds);

public static class InheritedIntuitionResolutionPlanner
{
    public static InheritedIntuitionResolutionDecision Decide(
        InheritedIntuitionEvidenceKind evidence,
        int elapsedMilliseconds,
        int resolutionWindowMilliseconds)
    {
        int elapsed = Math.Max(0, elapsedMilliseconds);
        int window = Math.Max(0, resolutionWindowMilliseconds);
        return evidence switch
        {
            InheritedIntuitionEvidenceKind.HoardPresent => new(
                true,
                true,
                false,
                false,
                InheritedIntuitionResolutionSource.HoardPresent,
                elapsed),
            InheritedIntuitionEvidenceKind.NoHoard => new(
                true,
                false,
                false,
                true,
                InheritedIntuitionResolutionSource.InvalidNoHoardMessage,
                elapsed),
            InheritedIntuitionEvidenceKind.Rejected => new(
                true,
                false,
                false,
                true,
                InheritedIntuitionResolutionSource.RejectedEvidence,
                elapsed),
            _ when elapsed >= window => new(
                true,
                false,
                true,
                false,
                InheritedIntuitionResolutionSource.NoHoardInferred,
                elapsed),
            _ => new(
                false,
                false,
                false,
                false,
                InheritedIntuitionResolutionSource.None,
                elapsed)
        };
    }
}
