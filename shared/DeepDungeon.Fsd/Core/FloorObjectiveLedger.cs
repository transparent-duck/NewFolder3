using System.Collections.Generic;

namespace DeepDungeon.Fsd.Core
{
    public readonly record struct ObjectiveIdentity(long FloorGeneration, long ObjectiveId, int Attempt = 0);

    public readonly record struct ObjectiveRecord(
        ObjectiveIdentity Identity,
        FloorObjectiveKind Kind,
        bool Required,
        ObjectiveOutcomeKind Outcome,
        int FailureCount)
    {
        public bool IsResolved => Outcome is ObjectiveOutcomeKind.Succeeded or ObjectiveOutcomeKind.Skipped;
        public bool IsUnresolved => Outcome is ObjectiveOutcomeKind.Pending or ObjectiveOutcomeKind.Preempted;
    }

    public enum ObjectiveOutcomeApplyStatus
    {
        Accepted,
        StaleFloorGeneration,
        UnknownObjective,
        StaleAttempt,
        DuplicateTerminalOutcome,
        ObjectiveNotPending,
        InvalidOutcome
    }

    public readonly record struct ObjectiveOutcomeApplyResult(
        ObjectiveOutcomeApplyStatus Status,
        ObjectiveRecord Objective,
        bool RetryableFailure,
        int FailureCount);

    public enum ObjectiveRestartStatus
    {
        Restarted,
        StaleFloorGeneration,
        UnknownObjective,
        StaleAttempt,
        NotRestartable
    }

    public readonly record struct ObjectiveRestartResult(
        ObjectiveRestartStatus Status,
        ObjectiveRecord Objective,
        int FailureCount);

    public sealed class FloorObjectiveLedger
    {
        private readonly Dictionary<long, ObjectiveRecord> _objectives = new();

        public FloorObjectiveLedger(long floorGeneration)
        {
            FloorGeneration = floorGeneration;
        }

        public long FloorGeneration { get; }
        public long Version { get; private set; }

        public void AddObjective(long objectiveId, FloorObjectiveKind kind, bool required)
        {
            var identity = new ObjectiveIdentity(FloorGeneration, objectiveId);
            _objectives.Add(objectiveId, new ObjectiveRecord(identity, kind, required, ObjectiveOutcomeKind.Pending, 0));
            Version++;
        }

        public bool TryGetObjective(long objectiveId, out ObjectiveRecord objective)
        {
            return _objectives.TryGetValue(objectiveId, out objective);
        }

        public ObjectiveOutcomeApplyStatus ValidateOutcome(
            ObjectiveIdentity identity,
            ObjectiveOutcomeKind outcome,
            out ObjectiveRecord objective)
        {
            objective = default;
            if (identity.FloorGeneration != FloorGeneration)
                return ObjectiveOutcomeApplyStatus.StaleFloorGeneration;
            if (!_objectives.TryGetValue(identity.ObjectiveId, out objective))
                return ObjectiveOutcomeApplyStatus.UnknownObjective;
            if (identity.Attempt != objective.Identity.Attempt)
                return ObjectiveOutcomeApplyStatus.StaleAttempt;
            if (IsTerminal(objective.Outcome))
                return ObjectiveOutcomeApplyStatus.DuplicateTerminalOutcome;
            if (objective.Outcome != ObjectiveOutcomeKind.Pending)
                return ObjectiveOutcomeApplyStatus.ObjectiveNotPending;
            if (outcome is ObjectiveOutcomeKind.NotRequested or ObjectiveOutcomeKind.Pending)
                return ObjectiveOutcomeApplyStatus.InvalidOutcome;
            if (outcome == ObjectiveOutcomeKind.Skipped && objective.Required)
                return ObjectiveOutcomeApplyStatus.InvalidOutcome;
            return ObjectiveOutcomeApplyStatus.Accepted;
        }

        public ObjectiveOutcomeApplyResult ApplyOutcome(ObjectiveIdentity identity, ObjectiveOutcomeKind outcome)
        {
            var status = ValidateOutcome(identity, outcome, out var objective);
            if (status != ObjectiveOutcomeApplyStatus.Accepted)
                return new ObjectiveOutcomeApplyResult(
                    status,
                    objective,
                    false,
                    objective.FailureCount);

            bool retryable = RoomObjectiveOutcomePlanner.IsRetryableFailure(outcome);
            objective = objective with
            {
                Outcome = outcome,
                FailureCount = objective.FailureCount + (objective.Required && retryable ? 1 : 0)
            };
            _objectives[identity.ObjectiveId] = objective;
            Version++;

            return new ObjectiveOutcomeApplyResult(
                ObjectiveOutcomeApplyStatus.Accepted,
                objective,
                retryable,
                objective.FailureCount);
        }

        public ObjectiveRestartResult Restart(ObjectiveIdentity identity, bool resetFailureCount = false)
        {
            if (identity.FloorGeneration != FloorGeneration)
                return new ObjectiveRestartResult(ObjectiveRestartStatus.StaleFloorGeneration, default, 0);
            if (!_objectives.TryGetValue(identity.ObjectiveId, out var objective))
                return new ObjectiveRestartResult(ObjectiveRestartStatus.UnknownObjective, default, 0);
            if (identity.Attempt != objective.Identity.Attempt)
                return new ObjectiveRestartResult(ObjectiveRestartStatus.StaleAttempt, objective, objective.FailureCount);
            if (objective.Outcome is not (ObjectiveOutcomeKind.Failed or
                ObjectiveOutcomeKind.TimedOut or
                ObjectiveOutcomeKind.Invalidated or
                ObjectiveOutcomeKind.Deferred or
                ObjectiveOutcomeKind.Preempted))
                return new ObjectiveRestartResult(ObjectiveRestartStatus.NotRestartable, objective, objective.FailureCount);

            objective = objective with
            {
                Identity = objective.Identity with { Attempt = objective.Identity.Attempt + 1 },
                Outcome = ObjectiveOutcomeKind.Pending,
                FailureCount = resetFailureCount ? 0 : objective.FailureCount
            };
            _objectives[identity.ObjectiveId] = objective;
            Version++;
            return new ObjectiveRestartResult(ObjectiveRestartStatus.Restarted, objective, objective.FailureCount);
        }

        public bool ResetFailureCount(ObjectiveIdentity identity)
        {
            if (identity.FloorGeneration != FloorGeneration ||
                !_objectives.TryGetValue(identity.ObjectiveId, out var objective) ||
                identity.Attempt != objective.Identity.Attempt)
            {
                return false;
            }

            _objectives[identity.ObjectiveId] = objective with { FailureCount = 0 };
            Version++;
            return true;
        }

        private static bool IsTerminal(ObjectiveOutcomeKind outcome)
        {
            return outcome is ObjectiveOutcomeKind.Succeeded or
                ObjectiveOutcomeKind.Skipped or
                ObjectiveOutcomeKind.Failed or
                ObjectiveOutcomeKind.TimedOut or
                ObjectiveOutcomeKind.Invalidated or
                ObjectiveOutcomeKind.Deferred;
        }
    }
}
