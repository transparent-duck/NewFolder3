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

            if (ShouldUseSlot(snapshot, FloorInitPlanner.HastePomanderSlotIndex))
                return new GeneralAutoPomanderDecision { SlotIndex = FloorInitPlanner.HastePomanderSlotIndex, Reason = "auto haste" };
            if (ShouldUseSlot(snapshot, FloorInitPlanner.FlightPomanderSlotIndex))
                return new GeneralAutoPomanderDecision { SlotIndex = FloorInitPlanner.FlightPomanderSlotIndex, Reason = "auto flight" };
            if (ShouldUseSlot(snapshot, FloorInitPlanner.FortunePomanderSlotIndex))
                return new GeneralAutoPomanderDecision { SlotIndex = FloorInitPlanner.FortunePomanderSlotIndex, Reason = "auto fortune" };

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
                FloorInitPlanner.HastePomanderSlotIndex => snapshot.HasteUsable && !snapshot.HasHasteStatus,
                FloorInitPlanner.FlightPomanderSlotIndex => snapshot.FlightUsable && !snapshot.FlightActive && snapshot.NextFloorIsMob,
                FloorInitPlanner.FortunePomanderSlotIndex => snapshot.FortuneUsable && !snapshot.FortuneActive,
                _ => false
            };
        }
    }
}
