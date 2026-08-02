namespace DeepDungeon.Fsd.Core
{
    public readonly record struct HoardWorkInvalidationSnapshot
    {
        public bool NoHoardEvidenceActive { get; init; }
        public bool HasCachedHoardIndicator { get; init; }
        public bool ActiveWaypointPresent { get; init; }
        public bool ActiveWaypointIsTrap { get; init; }
        public bool CurrentPlanShouldProbeHoard { get; init; }
        public bool CurrentPlanShouldSearchChests { get; init; }
        public bool ActiveChestObjectivePresent { get; init; }
        public bool CurrentPlanShouldVisitForIntel { get; init; }
    }

    public readonly record struct HoardWorkInvalidationDecision
    {
        public bool ClearCachedIndicator { get; init; }
        public bool AbortActiveHoardWork { get; init; }
        public bool RequestPlanRefresh { get; init; }
        public ObjectiveOutcomeKind HoardOutcome { get; init; }
        public ObjectiveOutcomeKind ChestsOutcome { get; init; }
        public ObjectiveOutcomeKind IntelOutcome { get; init; }
    }

    public static class HoardWorkInvalidationPlanner
    {
        public static HoardWorkInvalidationDecision Decide(in HoardWorkInvalidationSnapshot snapshot)
        {
            if (!snapshot.NoHoardEvidenceActive)
            {
                return default;
            }

            bool clearCachedIndicator = snapshot.HasCachedHoardIndicator;
            bool abortActiveHoardWork =
                snapshot.ActiveWaypointIsTrap ||
                (!snapshot.ActiveWaypointPresent && snapshot.CurrentPlanShouldProbeHoard);
            bool requestPlanRefresh =
                clearCachedIndicator ||
                abortActiveHoardWork ||
                snapshot.CurrentPlanShouldProbeHoard;

            return new HoardWorkInvalidationDecision
            {
                ClearCachedIndicator = clearCachedIndicator,
                AbortActiveHoardWork = abortActiveHoardWork,
                RequestPlanRefresh = requestPlanRefresh,
                HoardOutcome = abortActiveHoardWork && snapshot.CurrentPlanShouldProbeHoard
                    ? ObjectiveOutcomeKind.Succeeded
                    : ObjectiveOutcomeKind.NotRequested,
                ChestsOutcome = abortActiveHoardWork &&
                                snapshot.CurrentPlanShouldSearchChests &&
                                snapshot.ActiveChestObjectivePresent
                    ? ObjectiveOutcomeKind.Preempted
                    : ObjectiveOutcomeKind.NotRequested,
                IntelOutcome = abortActiveHoardWork && snapshot.CurrentPlanShouldVisitForIntel
                    ? ObjectiveOutcomeKind.Succeeded
                    : ObjectiveOutcomeKind.NotRequested
            };
        }
    }
}
