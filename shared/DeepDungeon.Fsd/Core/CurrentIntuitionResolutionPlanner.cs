using System;

namespace DeepDungeon.Fsd.Core
{
    public readonly record struct CurrentIntuitionResolutionSnapshot
    {
        public bool UsedIntuitionThisFloor { get; init; }
        public bool ChatSaysHoard { get; init; }
        public bool ChatSaysNoHoard { get; init; }
        public int ElapsedMillisecondsSinceUse { get; init; }
        public int ResolutionWindowMilliseconds { get; init; }
    }

    public enum CurrentIntuitionResolutionKind
    {
        Proceed,
        Wait,
        AssumeNoHoard
    }

    public readonly record struct CurrentIntuitionResolutionDecision
    {
        public CurrentIntuitionResolutionKind Kind { get; init; }
        public int RemainingWaitMilliseconds { get; init; }
    }

    public static class CurrentIntuitionResolutionPlanner
    {
        public static CurrentIntuitionResolutionDecision Decide(in CurrentIntuitionResolutionSnapshot snapshot)
        {
            bool unresolved =
                snapshot.UsedIntuitionThisFloor &&
                !snapshot.ChatSaysHoard &&
                !snapshot.ChatSaysNoHoard;

            if (!unresolved)
            {
                return new CurrentIntuitionResolutionDecision
                {
                    Kind = CurrentIntuitionResolutionKind.Proceed
                };
            }

            int elapsed = Math.Max(0, snapshot.ElapsedMillisecondsSinceUse);
            int window = Math.Max(0, snapshot.ResolutionWindowMilliseconds);
            if (elapsed < window)
            {
                return new CurrentIntuitionResolutionDecision
                {
                    Kind = CurrentIntuitionResolutionKind.Wait,
                    RemainingWaitMilliseconds = window - elapsed
                };
            }

            return new CurrentIntuitionResolutionDecision
            {
                Kind = CurrentIntuitionResolutionKind.Wait,
                RemainingWaitMilliseconds = 0
            };
        }
    }
}
