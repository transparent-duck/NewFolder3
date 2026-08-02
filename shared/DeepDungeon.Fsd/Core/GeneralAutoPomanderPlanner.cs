namespace DeepDungeon.Fsd.Core
{
    public static class GeneralAutoPomanderPlanner
    {
        public static GeneralAutoPomanderDecision Decide(in GeneralAutoPomanderSnapshot snapshot)
        {
            if (!snapshot.CanAttemptPomanderUse)
            {
                return default;
            }

            if (ShouldUseSlot(snapshot, FloorInitPlanner.PurityPomanderSlotIndex))
            {
                return new GeneralAutoPomanderDecision
                {
                    SlotIndex = FloorInitPlanner.PurityPomanderSlotIndex,
                    Reason = "auto purity"
                };
            }

            if (ShouldUseSlot(snapshot, FloorInitPlanner.SerenityPomanderSlotIndex))
            {
                return new GeneralAutoPomanderDecision
                {
                    SlotIndex = FloorInitPlanner.SerenityPomanderSlotIndex,
                    Reason = "auto serenity"
                };
            }

            if (ShouldUseSlot(snapshot, FloorInitPlanner.AffluencePomanderSlotIndex))
            {
                return new GeneralAutoPomanderDecision
                {
                    SlotIndex = FloorInitPlanner.AffluencePomanderSlotIndex,
                    Reason = "auto affluence"
                };
            }

            if (ShouldUseSlot(snapshot, FloorInitPlanner.StrengthPomanderSlotIndex))
            {
                return new GeneralAutoPomanderDecision
                {
                    SlotIndex = FloorInitPlanner.StrengthPomanderSlotIndex,
                    Reason = "auto strength"
                };
            }

            if (ShouldUseSlot(snapshot, FloorInitPlanner.SteelPomanderSlotIndex))
            {
                return new GeneralAutoPomanderDecision
                {
                    SlotIndex = FloorInitPlanner.SteelPomanderSlotIndex,
                    Reason = "auto steel"
                };
            }

            if (ShouldUseSlot(snapshot, FloorInitPlanner.RaisingPomanderSlotIndex))
            {
                return new GeneralAutoPomanderDecision
                {
                    SlotIndex = FloorInitPlanner.RaisingPomanderSlotIndex,
                    Reason = "auto raising"
                };
            }

            return default;
        }

        public static bool ShouldUseSlot(in GeneralAutoPomanderSnapshot snapshot, uint slotIndex)
        {
            if (!snapshot.CanAttemptPomanderUse)
            {
                return false;
            }

            return slotIndex switch
            {
                FloorInitPlanner.PurityPomanderSlotIndex => snapshot.PurityUsable && snapshot.HasCurseStatus,
                FloorInitPlanner.SerenityPomanderSlotIndex => snapshot.SerenityUsable && snapshot.HasHarmfulFloorEffect,
                FloorInitPlanner.AffluencePomanderSlotIndex => snapshot.AffluenceUsable && !snapshot.AffluenceActive,
                FloorInitPlanner.StrengthPomanderSlotIndex => snapshot.StrengthUsable && (snapshot.AllowStatusOverlap || !snapshot.HasStrengthStatus),
                FloorInitPlanner.SteelPomanderSlotIndex => snapshot.SteelUsable && (snapshot.AllowStatusOverlap || !snapshot.HasSteelStatus),
                FloorInitPlanner.RaisingPomanderSlotIndex => snapshot.RaisingUsable && !snapshot.RaisingActive,
                _ => false
            };
        }
    }
}
