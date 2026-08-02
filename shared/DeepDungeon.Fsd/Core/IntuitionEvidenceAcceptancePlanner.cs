namespace DeepDungeon.Fsd.Core
{
    public enum IntuitionEvidenceExpectationKind
    {
        None,
        FloorResult,
        BandedOpen,
        InheritedFloorResult
    }

    public enum IntuitionEvidenceMessageKind
    {
        HoardPresent,
        NoHoard,
        HoardCofferFound
    }

    public readonly record struct IntuitionEvidenceAcceptanceSnapshot(
        IntuitionEvidenceExpectationKind ExpectationKind,
        long AttemptId,
        IntuitionEvidenceMessageKind MessageKind);

    public readonly record struct IntuitionEvidenceAcceptanceDecision(bool Accepted, string Reason);

    public static class IntuitionEvidenceAcceptancePlanner
    {
        public static IntuitionEvidenceAcceptanceDecision Decide(in IntuitionEvidenceAcceptanceSnapshot snapshot)
        {
            bool intuitionResult = snapshot.MessageKind is
                IntuitionEvidenceMessageKind.HoardPresent or
                IntuitionEvidenceMessageKind.NoHoard;
            bool accepted = snapshot.AttemptId > 0 &&
                ((intuitionResult &&
                  snapshot.ExpectationKind is IntuitionEvidenceExpectationKind.FloorResult or
                      IntuitionEvidenceExpectationKind.InheritedFloorResult) ||
                 (snapshot.MessageKind == IntuitionEvidenceMessageKind.HoardCofferFound &&
                  snapshot.ExpectationKind == IntuitionEvidenceExpectationKind.BandedOpen));
            return new IntuitionEvidenceAcceptanceDecision(
                accepted,
                accepted ? "matched-expected-evidence" : "unmatched-or-stale-evidence");
        }
    }
}
