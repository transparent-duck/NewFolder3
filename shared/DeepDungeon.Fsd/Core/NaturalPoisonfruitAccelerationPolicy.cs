namespace DeepDungeon.Fsd.Core;

public readonly record struct NaturalPoisonfruitAccelerationSnapshot(
    bool ControlledSurveyActive,
    FloorObjectiveKind PrimaryObjective,
    bool ActivePairCapture,
    bool JointScanComplete,
    bool PassageOpen,
    int PoisonfruitStock,
    bool AlreadyDispatched,
    bool CanDispatch,
    bool PassageDispatchSafe,
    bool PtStoneSupported,
    bool PtStoneUsableThisFloor);

public enum NaturalPoisonfruitAccelerationAction
{
    None,
    Dispatch
}

public static class NaturalPoisonfruitAccelerationPolicy
{
    public static NaturalPoisonfruitAccelerationAction Decide(
        in NaturalPoisonfruitAccelerationSnapshot snapshot)
    {
        if (snapshot.ControlledSurveyActive ||
            snapshot.PrimaryObjective != FloorObjectiveKind.ActivatePassage ||
            snapshot.ActivePairCapture && !snapshot.JointScanComplete ||
            snapshot.PassageOpen ||
            snapshot.PoisonfruitStock <= 0 ||
            snapshot.AlreadyDispatched ||
            !snapshot.CanDispatch ||
            !snapshot.PassageDispatchSafe ||
            !snapshot.PtStoneSupported ||
            !snapshot.PtStoneUsableThisFloor)
        {
            return NaturalPoisonfruitAccelerationAction.None;
        }

        return NaturalPoisonfruitAccelerationAction.Dispatch;
    }
}
