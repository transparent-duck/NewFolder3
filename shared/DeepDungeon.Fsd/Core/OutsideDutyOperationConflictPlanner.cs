namespace DeepDungeon.Fsd.Core
{
    public enum OutsideDutyOperation
    {
        None,
        StartOrEnter,
        LeaveDuty,
        DeleteSave
    }

    public enum OutsideDutyOperationConflict
    {
        None,
        InvalidRequest,
        StartOrEnterActive,
        LeaveDutyActive,
        DeleteSaveActive,
        MultipleOperationsActive
    }

    public readonly record struct OutsideDutyOperationSnapshot
    {
        public bool StartOrEnterActive { get; init; }
        public bool LeaveDutyActive { get; init; }
        public bool DeleteSaveActive { get; init; }
    }

    public readonly record struct OutsideDutyOperationDecision
    {
        public bool Allowed { get; init; }
        public OutsideDutyOperationConflict Conflict { get; init; }
    }

    public static class OutsideDutyOperationConflictPlanner
    {
        public static OutsideDutyOperationDecision Decide(
            in OutsideDutyOperationSnapshot snapshot,
            OutsideDutyOperation requestedOperation)
        {
            if (requestedOperation == OutsideDutyOperation.None)
            {
                return new OutsideDutyOperationDecision
                {
                    Allowed = false,
                    Conflict = OutsideDutyOperationConflict.InvalidRequest
                };
            }

            int activeCount = (snapshot.StartOrEnterActive ? 1 : 0)
                              + (snapshot.LeaveDutyActive ? 1 : 0)
                              + (snapshot.DeleteSaveActive ? 1 : 0);
            if (activeCount == 0)
            {
                return new OutsideDutyOperationDecision
                {
                    Allowed = true,
                    Conflict = OutsideDutyOperationConflict.None
                };
            }

            if (activeCount > 1)
            {
                return new OutsideDutyOperationDecision
                {
                    Allowed = false,
                    Conflict = OutsideDutyOperationConflict.MultipleOperationsActive
                };
            }

            return new OutsideDutyOperationDecision
            {
                Allowed = false,
                Conflict = snapshot.StartOrEnterActive
                    ? OutsideDutyOperationConflict.StartOrEnterActive
                    : snapshot.LeaveDutyActive
                        ? OutsideDutyOperationConflict.LeaveDutyActive
                        : OutsideDutyOperationConflict.DeleteSaveActive
            };
        }
    }
}
