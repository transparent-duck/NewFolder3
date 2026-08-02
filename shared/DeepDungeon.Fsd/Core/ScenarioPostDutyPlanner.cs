namespace DeepDungeon.Fsd.Core
{
    public enum ScenarioPostDutyAction
    {
        ContinueEntry,
        WaitForDutyExit,
        WaitForTransition,
        RunCleanup,
        CompleteBeforeEntryError
    }

    public enum ScenarioRunOutcome
    {
        Completed,
        Incomplete,
        Failed
    }

    public readonly record struct ScenarioPostDutySnapshot
    {
        public bool WasEverInDuty { get; init; }
        public bool IsInDuty { get; init; }
        public bool IsTransitioning { get; init; }
        public bool StatusIsError { get; init; }
        public bool DutyCompletionObserved { get; init; }
        public bool DutyFailureObserved { get; init; }
    }

    public readonly record struct ScenarioPostDutyDecision
    {
        public ScenarioPostDutyAction Action { get; init; }
        public ScenarioRunOutcome Outcome { get; init; }
    }

    public static class ScenarioPostDutyPlanner
    {
        public static ScenarioPostDutyDecision Decide(in ScenarioPostDutySnapshot snapshot)
        {
            var outcome = ResolveOutcome(snapshot);

            if (!snapshot.WasEverInDuty && snapshot.StatusIsError && !snapshot.IsInDuty)
            {
                return new ScenarioPostDutyDecision
                {
                    Action = ScenarioPostDutyAction.CompleteBeforeEntryError,
                    Outcome = outcome
                };
            }

            if (snapshot.IsInDuty)
            {
                return new ScenarioPostDutyDecision
                {
                    Action = ScenarioPostDutyAction.WaitForDutyExit,
                    Outcome = outcome
                };
            }

            if (!snapshot.WasEverInDuty)
            {
                return new ScenarioPostDutyDecision
                {
                    Action = ScenarioPostDutyAction.ContinueEntry,
                    Outcome = outcome
                };
            }

            if (snapshot.IsTransitioning)
            {
                return new ScenarioPostDutyDecision
                {
                    Action = ScenarioPostDutyAction.WaitForTransition,
                    Outcome = outcome
                };
            }

            return new ScenarioPostDutyDecision
            {
                Action = ScenarioPostDutyAction.RunCleanup,
                Outcome = outcome
            };
        }

        private static ScenarioRunOutcome ResolveOutcome(in ScenarioPostDutySnapshot snapshot)
        {
            if (snapshot.DutyFailureObserved)
            {
                return ScenarioRunOutcome.Failed;
            }

            return snapshot.DutyCompletionObserved
                ? ScenarioRunOutcome.Completed
                : ScenarioRunOutcome.Incomplete;
        }
    }
}
