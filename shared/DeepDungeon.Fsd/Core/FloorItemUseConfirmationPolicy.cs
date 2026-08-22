namespace DeepDungeon.Fsd.Core
{
    public enum FloorItemUseConfirmationDecisionKind
    {
        PendingConfirmation,
        Confirmed,
        WaitingToRetry,
        RetryReady,
        Exhausted
    }

    public readonly record struct FloorItemUseConfirmationSnapshot(
        int CountBeforeDispatch,
        int CurrentCount,
        bool AuthoritativeConfirmationObserved,
        int AttemptNumber,
        int ElapsedMilliseconds);

    public static class FloorItemUseConfirmationPolicy
    {
        public const int ConfirmationWindowMilliseconds = 2000;
        public const int RetryDelayMilliseconds = 3000;
        public const int MaximumAttempts = 2;

        public static FloorItemUseConfirmationDecisionKind Decide(
            in FloorItemUseConfirmationSnapshot snapshot)
        {
            if (snapshot.CurrentCount < snapshot.CountBeforeDispatch ||
                snapshot.AuthoritativeConfirmationObserved)
            {
                return FloorItemUseConfirmationDecisionKind.Confirmed;
            }

            int elapsed = Math.Max(0, snapshot.ElapsedMilliseconds);
            if (elapsed < ConfirmationWindowMilliseconds)
                return FloorItemUseConfirmationDecisionKind.PendingConfirmation;

            if (snapshot.AttemptNumber >= MaximumAttempts)
                return FloorItemUseConfirmationDecisionKind.Exhausted;

            return elapsed < RetryDelayMilliseconds
                ? FloorItemUseConfirmationDecisionKind.WaitingToRetry
                : FloorItemUseConfirmationDecisionKind.RetryReady;
        }
    }
}
