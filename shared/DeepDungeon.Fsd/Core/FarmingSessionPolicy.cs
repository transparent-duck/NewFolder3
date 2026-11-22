namespace DeepDungeon.Fsd.Core;

public readonly record struct FarmingAttemptResult(int EntryFloor, bool DutyCompleted,
    bool DutyFailed, bool HarvestCompleted, bool Aborted);

public readonly record struct FarmingSessionDecision(bool CountCycle, int NextFloor,
    bool DeleteOwnedSave, bool RecoverFailure, string Error);

public static class FarmingSessionPolicy
{
    public static bool ReachedStopBoundary(FarmingPlan plan, in FarmingAttemptResult result) =>
        plan.Mode == FarmingMode.DeepProgression && plan.StopAfterFloor is { } limit &&
        result.DutyCompleted && !result.DutyFailed && !result.Aborted && result.EntryFloor + 9 >= limit;

    public static FarmingSessionDecision Decide(FarmingPlan plan, in FarmingAttemptResult result)
    {
        bool ownsSave = plan.SaveUse == SaveUse.Create;
        if (plan.HoldOnFailure && (result.DutyFailed || result.Aborted))
            return new(false, result.EntryFloor, false, false, "診斷已停止；保留副本與存檔，等待人工處理。");
        if (result.Aborted)
            return new(false, plan.StartFloor, ownsSave, false, "本次入本已中止，未計完成次數。");
        if (result.DutyFailed)
            return plan.Mode == FarmingMode.DeepProgression
                ? new(false, plan.StartFloor, true, true, string.Empty)
                : new(false, plan.StartFloor, ownsSave, false, "本次攻略失敗，未計完成次數。");
        if (plan.ReusesSave)
            return new(result.HarvestCompleted, 0, false, false,
                result.HarvestCompleted ? string.Empty : "採集未正常收尾，未計完成次數。");
        if (!result.DutyCompleted)
            return new(false, result.EntryFloor, false, false, "未觀測到通關或敗北；保留存檔，停止接續。");
        if (plan.Mode == FarmingMode.DeepProgression)
        {
            int terminalEntry = plan.DungeonId == 1 ? 191 : 91;
            if (result.EntryFloor < 1 || result.EntryFloor > terminalEntry || result.EntryFloor % 10 != 1)
                return new(false, result.EntryFloor, false, false, "攻略起點無效，停止接續。");
            return result.EntryFloor >= terminalEntry
                ? new(true, plan.StartFloor, true, false, string.Empty)
                : new(false, result.EntryFloor + 10, false, false, string.Empty);
        }
        return new(true, plan.StartFloor, ownsSave, false, string.Empty);
    }
}
