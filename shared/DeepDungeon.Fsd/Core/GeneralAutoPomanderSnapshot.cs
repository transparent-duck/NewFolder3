namespace DeepDungeon.Fsd.Core
{
    public readonly record struct GeneralAutoPomanderSnapshot
    {
        public bool CanAttemptPomanderUse { get; init; }
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
        public bool AllowStatusOverlap { get; init; }
    }

    public readonly record struct GeneralAutoPomanderDecision
    {
        public uint? SlotIndex { get; init; }
        public string? Reason { get; init; }

        public bool ShouldUse => SlotIndex.HasValue;
    }
}
