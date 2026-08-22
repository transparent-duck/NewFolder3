namespace DeepDungeon.Fsd.Core
{
    public static class FloorInitPlanner
    {
        public const uint SightPomanderSlotIndex = 1;
        public const uint StrengthPomanderSlotIndex = 2;
        public const uint SteelPomanderSlotIndex = 3;
        public const uint AffluencePomanderSlotIndex = 4;
        public const uint PurityPomanderSlotIndex = 7;
        public const uint SerenityPomanderSlotIndex = 10;
        public const uint IntuitionPomanderSlotIndex = 13;
        public const uint RaisingPomanderSlotIndex = 14;

        public static FloorInitDecision Decide(in FloorInitSnapshot snapshot)
        {
            if (!snapshot.CanAttemptPomanderUse)
            {
                return default;
            }

            if (snapshot.BandedEnabled &&
                !snapshot.HasOpenedHoardThisFloor &&
                FloorsetHoardDistributionPolicy.AllowsHoardPomander(snapshot.HoardOpportunity) &&
                !snapshot.IntuitionActive &&
                !snapshot.UsedIntuitionThisFloor)
            {
                if (snapshot.IntuitionUsable)
                {
                    return new FloorInitDecision
                    {
                        SlotIndex = IntuitionPomanderSlotIndex,
                        Reason = "S1 intuition"
                    };
                }

                if (!snapshot.SightUseBlocked && snapshot.SightUsable)
                {
                    return new FloorInitDecision
                    {
                        SlotIndex = SightPomanderSlotIndex,
                        Reason = "S1 sight"
                    };
                }
            }

            if (snapshot.HasCurseStatus && snapshot.PurityUsable)
            {
                return new FloorInitDecision
                {
                    SlotIndex = PurityPomanderSlotIndex,
                    Reason = "auto purity"
                };
            }

            if (snapshot.HasHarmfulFloorEffect && snapshot.SerenityUsable)
            {
                return new FloorInitDecision
                {
                    SlotIndex = SerenityPomanderSlotIndex,
                    Reason = "auto serenity"
                };
            }

            if (snapshot.AffluenceUsable && !snapshot.AffluenceActive)
            {
                return new FloorInitDecision
                {
                    SlotIndex = AffluencePomanderSlotIndex,
                    Reason = "auto affluence"
                };
            }

            if (snapshot.StrengthUsable && !snapshot.HasStrengthStatus)
            {
                return new FloorInitDecision
                {
                    SlotIndex = StrengthPomanderSlotIndex,
                    Reason = "auto strength"
                };
            }

            if (snapshot.SteelUsable && !snapshot.HasSteelStatus)
            {
                return new FloorInitDecision
                {
                    SlotIndex = SteelPomanderSlotIndex,
                    Reason = "auto steel"
                };
            }

            if (snapshot.RaisingUsable && !snapshot.RaisingActive)
            {
                return new FloorInitDecision
                {
                    SlotIndex = RaisingPomanderSlotIndex,
                    Reason = "auto raising"
                };
            }

            return default;
        }
    }
}
