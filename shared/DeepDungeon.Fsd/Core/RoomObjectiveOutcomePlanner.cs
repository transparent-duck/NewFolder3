using System;

namespace DeepDungeon.Fsd.Core
{
    public enum ObjectiveOutcomeKind
    {
        NotRequested,
        Pending,
        Succeeded,
        Failed,
        TimedOut,
        Invalidated,
        Deferred,
        Skipped,
        Preempted
    }

    public readonly record struct ObjectiveCategoryProgress(
        bool Requested,
        int RequiredCount,
        int CompletedCount,
        int SkippedCount,
        bool AuthoritativelyResolved,
        bool Failed,
        bool Deferred,
        bool Preempted);

    public readonly record struct RoomObjectiveOutcomeSnapshot(
        ObjectiveCategoryProgress Hoard,
        ObjectiveCategoryProgress Chests,
        ObjectiveCategoryProgress Intel);

    public readonly record struct RoomObjectiveOutcomeResult(
        ObjectiveOutcomeKind Hoard,
        ObjectiveOutcomeKind Chests,
        ObjectiveOutcomeKind Intel)
    {
        public bool MarkHoardSearched => Hoard == ObjectiveOutcomeKind.Succeeded;
        public bool MarkChestsSearched => Chests is ObjectiveOutcomeKind.Succeeded or ObjectiveOutcomeKind.Skipped;
        public bool MarkIntelVisited => Intel == ObjectiveOutcomeKind.Succeeded;
    }

    public readonly record struct RoomObjectiveRetrySnapshot(
        bool HoardRequested,
        ObjectiveOutcomeKind HoardOutcome,
        bool IntelRequested,
        ObjectiveOutcomeKind IntelOutcome,
        bool ChestsRequested,
        ObjectiveOutcomeKind ChestsOutcome,
        int PreviousMandatoryFailureCount);

    public readonly record struct RoomObjectiveRetryDecision(
        int MandatoryFailureCount,
        bool RetryMandatory,
        bool BlockMandatory,
        bool SkipOptionalChests);

    public static class RoomObjectiveOutcomePlanner
    {
        public const int MandatoryFailureLimit = 3;
        public const int RetryBackoffMilliseconds = 2000;

        public static RoomObjectiveOutcomeResult Decide(in RoomObjectiveOutcomeSnapshot snapshot)
        {
            return new RoomObjectiveOutcomeResult(
                DecideCategory(snapshot.Hoard, emptyIsResolved: false, allowSkipped: false),
                DecideCategory(snapshot.Chests, emptyIsResolved: true, allowSkipped: true),
                DecideCategory(snapshot.Intel, emptyIsResolved: false, allowSkipped: false));
        }

        public static RoomObjectiveOutcomeResult RequireAuthoritativeDirectHoard(
            in RoomObjectiveOutcomeResult outcome,
            bool intuitionDirect,
            bool authoritativeHoardResolved)
        {
            return intuitionDirect && !authoritativeHoardResolved && outcome.Hoard == ObjectiveOutcomeKind.Succeeded
                ? outcome with { Hoard = ObjectiveOutcomeKind.Deferred }
                : outcome;
        }

        public static RoomObjectiveRetryDecision DecideRetry(in RoomObjectiveRetrySnapshot snapshot)
        {
            bool mandatoryFailed =
                (snapshot.HoardRequested && IsRetryableFailure(snapshot.HoardOutcome)) ||
                (snapshot.IntelRequested && IsRetryableFailure(snapshot.IntelOutcome));
            bool mandatoryPreempted =
                (snapshot.HoardRequested && snapshot.HoardOutcome == ObjectiveOutcomeKind.Preempted) ||
                (snapshot.IntelRequested && snapshot.IntelOutcome == ObjectiveOutcomeKind.Preempted);
            int failureCount = mandatoryFailed
                ? Math.Max(0, snapshot.PreviousMandatoryFailureCount) + 1
                : mandatoryPreempted
                    ? Math.Max(0, snapshot.PreviousMandatoryFailureCount)
                    : 0;
            bool blockMandatory = mandatoryFailed && failureCount >= MandatoryFailureLimit;
            bool skipOptionalChests = snapshot.ChestsRequested && IsRetryableFailure(snapshot.ChestsOutcome);
            return new RoomObjectiveRetryDecision(
                failureCount,
                mandatoryFailed && !blockMandatory,
                blockMandatory,
                skipOptionalChests);
        }

        private static ObjectiveOutcomeKind DecideCategory(
            in ObjectiveCategoryProgress progress,
            bool emptyIsResolved,
            bool allowSkipped)
        {
            if (!progress.Requested)
                return ObjectiveOutcomeKind.NotRequested;
            if (progress.AuthoritativelyResolved ||
                (emptyIsResolved && progress.RequiredCount == 0 &&
                 !progress.Failed && !progress.Deferred && !progress.Preempted) ||
                (progress.RequiredCount > 0 && progress.CompletedCount >= progress.RequiredCount &&
                 !progress.Failed && !progress.Deferred && !progress.Preempted))
            {
                return ObjectiveOutcomeKind.Succeeded;
            }
            if (allowSkipped &&
                progress.RequiredCount > 0 &&
                progress.CompletedCount + progress.SkippedCount >= progress.RequiredCount &&
                !progress.Failed && !progress.Deferred && !progress.Preempted)
            {
                return ObjectiveOutcomeKind.Skipped;
            }
            if (progress.Failed)
                return ObjectiveOutcomeKind.Failed;
            if (progress.Deferred)
                return ObjectiveOutcomeKind.Deferred;
            if (progress.Preempted)
                return ObjectiveOutcomeKind.Preempted;
            if (progress.RequiredCount == 0)
                return ObjectiveOutcomeKind.Deferred;
            return ObjectiveOutcomeKind.Pending;
        }

        public static bool IsRetryableFailure(ObjectiveOutcomeKind outcome)
        {
            return outcome is ObjectiveOutcomeKind.Failed or
                ObjectiveOutcomeKind.TimedOut or
                ObjectiveOutcomeKind.Invalidated or
                ObjectiveOutcomeKind.Deferred;
        }
    }
}
