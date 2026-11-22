namespace DeepDungeon.Fsd.Core;

public enum ExitUiKind { None, Incomplete, Unknown, DutyConfirmation, RestConfirmation, Result }
public enum ExitAction { Wait, RequestDutyExit, ConfirmDutyExit, ApproachRestExit, ConfirmRestExit, Complete, LeaveResult, ApproachDutyExit }
public readonly record struct ExitFacts(bool Transitioning, bool InDuty, bool AtEntrance,
    bool RestExitPresent, bool InCombat, ExitUiKind Ui, bool DutyExitPresent = false);

public static class ExitUiPolicy
{
    public static ExitAction Decide(in ExitFacts facts, bool returnToEntrance)
    {
        if (facts.Transitioning) return ExitAction.Wait;
        if (!facts.InDuty && (!returnToEntrance || facts.AtEntrance)) return ExitAction.Complete;
        if (facts.Ui is ExitUiKind.Incomplete or ExitUiKind.Unknown) return ExitAction.Wait;
        if (facts.Ui == ExitUiKind.Result) return ExitAction.LeaveResult;
        if (facts.InDuty)
        {
            if (facts.InCombat) return ExitAction.Wait;
            return facts.Ui switch
            {
                ExitUiKind.DutyConfirmation => ExitAction.ConfirmDutyExit,
                ExitUiKind.None => facts.DutyExitPresent ? ExitAction.ApproachDutyExit : ExitAction.RequestDutyExit,
                _ => ExitAction.Wait
            };
        }
        if (!facts.RestExitPresent) return ExitAction.Wait;
        return facts.Ui switch
        {
            ExitUiKind.RestConfirmation => ExitAction.ConfirmRestExit,
            ExitUiKind.None => ExitAction.ApproachRestExit,
            _ => ExitAction.Wait
        };
    }
}
