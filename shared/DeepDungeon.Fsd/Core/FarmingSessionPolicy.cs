namespace DeepDungeon.Fsd.Core;

public readonly record struct FarmingAttemptResult(int EntryFloor, bool DutyCompleted,
    bool DutyFailed, bool HarvestCompleted, bool Aborted);

public readonly record struct FarmingSessionDecision(bool CountCycle, int NextFloor,
    bool DeleteOwnedSave, bool RecoverFailure, string Error);

public static class FarmingSessionPolicy
{
    public static FarmingSessionDecision Decide(FarmingPlan plan, in FarmingAttemptResult result)
    {
        bool ownsSave = plan.SaveUse == SaveUse.Create;
        if (result.Aborted)
            return new(false, plan.StartFloor, ownsSave, false, "本次入本已中止，未計完成次數。");
        if (result.DutyFailed)
            return plan.Mode == FarmingMode.DeepProgression
                ? new(false, 1, true, true, string.Empty)
                : new(false, plan.StartFloor, ownsSave, false, "本次攻略失敗，未計完成次數。");
        if (plan.ReusesSave)
            return new(result.HarvestCompleted, 0, false, false,
                result.HarvestCompleted ? string.Empty : "採集未正常收尾，未計完成次數。");
        if (!result.DutyCompleted)
            return new(false, plan.StartFloor, ownsSave, false, "本次未通關，未計完成次數。");
        if (plan.Mode == FarmingMode.DeepProgression)
        {
            if (result.EntryFloor is < 1 or > 91 || result.EntryFloor % 10 != 1)
                return new(false, result.EntryFloor, false, false, "攻略起點無效，停止接續。");
            return result.EntryFloor == 91
                ? new(true, 1, true, false, string.Empty)
                : new(false, result.EntryFloor + 10, false, false, string.Empty);
        }
        return new(true, plan.StartFloor, ownsSave, false, string.Empty);
    }
}
