namespace DeepDungeon.Fsd.Core
{
    public readonly record struct HoardIndicatorLifecycleSnapshot(
        bool HoardOpenedThisFloor,
        int FloorHoardBaseline,
        int ObservedHoardCount);

    public readonly record struct HoardIndicatorLifecycleDecision(
        bool MarkHoardOpened,
        bool AcceptIndicator);

    public static class HoardIndicatorLifecyclePlanner
    {
        public static HoardIndicatorLifecycleDecision Decide(in HoardIndicatorLifecycleSnapshot snapshot)
        {
            bool markOpened = !snapshot.HoardOpenedThisFloor &&
                              snapshot.ObservedHoardCount > snapshot.FloorHoardBaseline;
            return new HoardIndicatorLifecycleDecision(
                markOpened,
                !snapshot.HoardOpenedThisFloor && !markOpened);
        }
    }
}
