namespace DeepDungeon.Fsd.Core
{
    public enum NativeTreasureCompletionStatus
    {
        NoInteraction,
        Missing,
        WrongEntity,
        Unavailable,
        Stale,
        Targetable,
        Unopened,
        Accepted
    }

    public enum NativeTreasureCompletionKind
    {
        Unavailable,
        TreasureState,
        EventObjectTargetable
    }

    public readonly record struct NativeTreasureCompletionSnapshot(
        bool InteractionActive,
        uint ExpectedEntityId,
        long EvidenceSequenceAtStart,
        bool EvidencePresent,
        uint EvidenceEntityId,
        long EvidenceSequence,
        bool NativeStateAvailable,
        NativeTreasureCompletionKind CompletionKind,
        bool IsTargetable,
        byte State,
        byte Flags,
        bool RetryTargetableInteraction = false,
        double InteractionElapsedSeconds = 0,
        double RetryAfterSeconds = 0);

    public readonly record struct NativeTreasureCompletionDecision(
        NativeTreasureCompletionStatus Status,
        bool Complete,
        bool RetryInteraction = false);

    public static class NativeTreasureCompletionPlanner
    {
        private const byte UnopenedState = 0;
        private const byte OpenedFlag = 1;

        public static NativeTreasureCompletionDecision Decide(in NativeTreasureCompletionSnapshot snapshot)
        {
            if (!snapshot.InteractionActive)
                return new NativeTreasureCompletionDecision(NativeTreasureCompletionStatus.NoInteraction, false);
            if (!snapshot.EvidencePresent)
                return new NativeTreasureCompletionDecision(NativeTreasureCompletionStatus.Missing, false);
            if (snapshot.EvidenceEntityId != snapshot.ExpectedEntityId)
                return new NativeTreasureCompletionDecision(NativeTreasureCompletionStatus.WrongEntity, false);
            if (!snapshot.NativeStateAvailable)
                return new NativeTreasureCompletionDecision(NativeTreasureCompletionStatus.Unavailable, false);
            if (snapshot.EvidenceSequence <= snapshot.EvidenceSequenceAtStart)
                return new NativeTreasureCompletionDecision(NativeTreasureCompletionStatus.Stale, false);

            return snapshot.CompletionKind switch
            {
                NativeTreasureCompletionKind.TreasureState =>
                    snapshot.State != UnopenedState || (snapshot.Flags & OpenedFlag) != 0
                        ? new NativeTreasureCompletionDecision(NativeTreasureCompletionStatus.Accepted, true)
                        : new NativeTreasureCompletionDecision(NativeTreasureCompletionStatus.Unopened, false),
                NativeTreasureCompletionKind.EventObjectTargetable =>
                    !snapshot.IsTargetable
                        ? new NativeTreasureCompletionDecision(NativeTreasureCompletionStatus.Accepted, true)
                        : new NativeTreasureCompletionDecision(
                            NativeTreasureCompletionStatus.Targetable,
                            false,
                            snapshot.RetryTargetableInteraction &&
                            snapshot.RetryAfterSeconds > 0 &&
                            snapshot.InteractionElapsedSeconds >= snapshot.RetryAfterSeconds),
                _ => new NativeTreasureCompletionDecision(NativeTreasureCompletionStatus.Unavailable, false)
            };
        }
    }
}
