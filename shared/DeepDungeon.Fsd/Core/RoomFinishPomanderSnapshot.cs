namespace DeepDungeon.Fsd.Core
{
    public readonly record struct RoomFinishPomanderSnapshot
    {
        public bool CanAttemptPomanderUse { get; init; }
        public bool BandedEnabled { get; init; }
        public bool HasOpenedHoardThisFloor { get; init; }
        public int FloorsetBandedCount { get; init; }
        public FloorsetHoardOpportunity HoardOpportunity { get; init; }
        public bool IntuitionActive { get; init; }
        public bool UsedIntuitionThisFloor { get; init; }
        public bool SightUseBlocked { get; init; }
        public bool IntuitionUsable { get; init; }
        public bool SightUsable { get; init; }
        public int IntuitionCount { get; init; }
        public int RemainingMobFloors { get; init; }
    }

    public enum RoomFinishPomanderDecisionKind
    {
        NotNeeded,
        PendingRetry,
        Use
    }

    public readonly record struct RoomFinishPomanderDecision
    {
        public RoomFinishPomanderDecisionKind Kind { get; init; }
        public uint? SlotIndex { get; init; }
        public string? Reason { get; init; }
    }
}
