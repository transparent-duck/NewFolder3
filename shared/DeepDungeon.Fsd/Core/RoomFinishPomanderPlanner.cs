namespace DeepDungeon.Fsd.Core
{
    public static class RoomFinishPomanderPlanner
    {
        private const int MaxHoardsPerFloorset = 5;

        public static RoomFinishPomanderDecision Decide(in RoomFinishPomanderSnapshot snapshot)
        {
            if (!snapshot.BandedEnabled ||
                snapshot.HasOpenedHoardThisFloor ||
                snapshot.FloorsetBandedCount >= MaxHoardsPerFloorset ||
                !FloorsetHoardDistributionPolicy.AllowsHoardPomander(snapshot.HoardOpportunity) ||
                snapshot.IntuitionActive)
            {
                return new RoomFinishPomanderDecision
                {
                    Kind = RoomFinishPomanderDecisionKind.NotNeeded
                };
            }

            if (!snapshot.CanAttemptPomanderUse)
            {
                return new RoomFinishPomanderDecision
                {
                    Kind = RoomFinishPomanderDecisionKind.PendingRetry
                };
            }

            if (snapshot.IntuitionCount >= snapshot.RemainingMobFloors && snapshot.IntuitionUsable)
            {
                return new RoomFinishPomanderDecision
                {
                    Kind = RoomFinishPomanderDecisionKind.Use,
                    SlotIndex = FloorInitPlanner.IntuitionPomanderSlotIndex,
                    Reason = "S2 intuition (ceiling)"
                };
            }

            if (snapshot.SightUseBlocked)
            {
                return new RoomFinishPomanderDecision
                {
                    Kind = RoomFinishPomanderDecisionKind.NotNeeded
                };
            }

            if (snapshot.IntuitionUsable)
            {
                return new RoomFinishPomanderDecision
                {
                    Kind = RoomFinishPomanderDecisionKind.Use,
                    SlotIndex = FloorInitPlanner.IntuitionPomanderSlotIndex,
                    Reason = "S2 intuition"
                };
            }

            if (snapshot.SightUsable)
            {
                return new RoomFinishPomanderDecision
                {
                    Kind = RoomFinishPomanderDecisionKind.Use,
                    SlotIndex = FloorInitPlanner.SightPomanderSlotIndex,
                    Reason = "S2 sight"
                };
            }

            return new RoomFinishPomanderDecision
            {
                Kind = RoomFinishPomanderDecisionKind.PendingRetry
            };
        }
    }
}
