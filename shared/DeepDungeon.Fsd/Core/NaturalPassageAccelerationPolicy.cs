namespace DeepDungeon.Fsd.Core;

public readonly record struct NaturalPassageAccelerationSnapshot(
    bool ControlledSurveyActive,
    FloorObjectiveKind PrimaryObjective,
    bool ActivePairCapture,
    bool JointScanComplete,
    bool PassageOpen,
    int PoisonfruitStock,
    bool PoisonfruitAttemptedThisFloor,
    int MazerootStock,
    bool MazerootAttemptedOrAdopted,
    bool CanDispatch,
    bool PassageDispatchSafe,
    bool PtStoneSupported,
    bool PtStoneUsableThisFloor);

public enum NaturalPassageAccelerationAction
{
    None,
    DispatchPoisonfruit,
    DispatchMazeroot
}

public static class NaturalPassageAccelerationPolicy
{
    public static NaturalPassageAccelerationAction Decide(
        in NaturalPassageAccelerationSnapshot snapshot)
    {
        if (snapshot.ControlledSurveyActive ||
            snapshot.PrimaryObjective != FloorObjectiveKind.ActivatePassage ||
            snapshot.ActivePairCapture && !snapshot.JointScanComplete ||
            snapshot.PassageOpen ||
            !snapshot.CanDispatch ||
            !snapshot.PassageDispatchSafe ||
            !snapshot.PtStoneSupported ||
            !snapshot.PtStoneUsableThisFloor)
        {
            return NaturalPassageAccelerationAction.None;
        }

        // A Mazeroot request can open the passage asynchronously.  Do not
        // spend either stone again while that request is still settling.
        // This also covers Mazeroot used/adopted by the normal H/research
        // path, which may not have made the passage observable open yet.
        if (snapshot.MazerootAttemptedOrAdopted)
            return NaturalPassageAccelerationAction.None;

        // Keep Poisonfruit as the first choice.  Once the native request has
        // been attempted on this floor, do not spend Mazeroot as a fallback;
        // the passage can still open asynchronously after the request.
        if (snapshot.PoisonfruitStock > 0 &&
            !snapshot.PoisonfruitAttemptedThisFloor)
        {
            return NaturalPassageAccelerationAction.DispatchPoisonfruit;
        }

        if (snapshot.PoisonfruitAttemptedThisFloor)
            return NaturalPassageAccelerationAction.None;

        if (snapshot.MazerootStock > 0)
        {
            return NaturalPassageAccelerationAction.DispatchMazeroot;
        }

        return NaturalPassageAccelerationAction.None;
    }
}
