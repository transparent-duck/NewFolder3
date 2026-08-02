namespace DeepDungeon.Fsd.Core
{
    public readonly record struct ReadyFloorIntuitionSnapshot(
        bool NativeStateAvailable,
        bool NativeIntuitionActive);

    public enum ReadyFloorIntuitionDecisionKind
    {
        Wait,
        Initialize
    }

    public readonly record struct ReadyFloorIntuitionDecision(
        ReadyFloorIntuitionDecisionKind Kind,
        bool IntuitionActive);

    public static class ReadyFloorIntuitionPlanner
    {
        public static ReadyFloorIntuitionDecision Decide(in ReadyFloorIntuitionSnapshot snapshot)
        {
            return snapshot.NativeStateAvailable
                ? new ReadyFloorIntuitionDecision(ReadyFloorIntuitionDecisionKind.Initialize, snapshot.NativeIntuitionActive)
                : new ReadyFloorIntuitionDecision(ReadyFloorIntuitionDecisionKind.Wait, false);
        }

        public static bool ShouldRequestPlanRefresh(bool? previousSameFloorState, bool currentState)
        {
            return previousSameFloorState.HasValue && previousSameFloorState.Value != currentState;
        }
    }
}
