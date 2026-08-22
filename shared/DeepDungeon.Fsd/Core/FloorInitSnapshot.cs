namespace DeepDungeon.Fsd.Core
{
    public readonly record struct FloorInitSnapshot
    {
        public bool CanAttemptPomanderUse { get; init; }
        public bool BandedEnabled { get; init; }
        public bool HasOpenedHoardThisFloor { get; init; }
        public FloorsetHoardOpportunity HoardOpportunity { get; init; }
        public bool IntuitionActive { get; init; }
        public bool UsedIntuitionThisFloor { get; init; }
        public bool SightUseBlocked { get; init; }
        public bool IntuitionUsable { get; init; }
        public bool SightUsable { get; init; }
        public bool AffluenceUsable { get; init; }
        public bool StrengthUsable { get; init; }
        public bool SteelUsable { get; init; }
        public bool PurityUsable { get; init; }
        public bool SerenityUsable { get; init; }
        public bool RaisingUsable { get; init; }
        public bool AffluenceActive { get; init; }
        public bool RaisingActive { get; init; }
        public bool HasStrengthStatus { get; init; }
        public bool HasSteelStatus { get; init; }
        public bool HasCurseStatus { get; init; }
        public bool HasHarmfulFloorEffect { get; init; }
    }

    public readonly record struct FloorInitDecision
    {
        public uint? SlotIndex { get; init; }
        public string? Reason { get; init; }

        public bool ShouldUse => SlotIndex.HasValue;
    }
}
