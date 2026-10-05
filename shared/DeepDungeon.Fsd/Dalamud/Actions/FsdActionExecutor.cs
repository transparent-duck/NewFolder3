using global::Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DeepDungeon.Fsd.Dalamud.Actions;

public static class FsdActionExecutor
{
    public readonly record struct CastDiagnostic(bool Accepted, uint RequestedActionId, uint AdjustedActionId,
        uint? RawStatus, uint? AdjustedStatus, float? AnimationLock, string Result);

    public static unsafe CastDiagnostic CastWithDiagnostics(uint actionId, ulong target)
    {
        var player = Service.LocalPlayer;
        var manager = ActionManager.Instance();
        bool between = Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51];
        uint adjusted = manager != null && actionId != 0 ? manager->GetAdjustedActionId(actionId) : 0;
        uint? rawStatus = manager != null && actionId != 0 ? manager->GetActionStatus(ActionType.Action, actionId, target) : null;
        uint? adjustedStatus = manager != null && adjusted != 0 ? manager->GetActionStatus(ActionType.Action, adjusted, target) : null;
        float? animationLock = manager != null ? manager->AnimationLock : null;
        bool accepted = Cast(actionId, target);
        string result = accepted ? "native-call-accepted" :
            player == null || player.Address == 0 ? "player-unavailable" : player.IsDead ? "player-dead" :
            actionId == 0 ? "no-action" : between ? "transition" : manager == null ? "action-manager-unavailable" :
            animationLock > 0.05f ? "animation-lock" : "native-call-rejected";
        return new(accepted, actionId, adjusted, rawStatus, adjustedStatus, animationLock, result);
    }

    public static unsafe bool Cast(uint actionId, ulong target = 0xE0000000UL)
        => TryUseAction(ActionType.Action, actionId, target, requireReady: false);

    public static unsafe bool TryUseAction(ActionType actionType, uint actionId, ulong target, bool requireReady)
    {
        var player = Service.LocalPlayer;
        if (player == null || player.Address == 0 || player.IsDead || actionId == 0)
            return false;
        if (Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51])
            return false;
        var manager = ActionManager.Instance();
        if (manager == null || manager->AnimationLock > 0.05f)
            return false;
        if (requireReady && manager->GetActionStatus(actionType, actionId) != Service.ActionStatus_Ready)
            return false;
        return actionType == ActionType.Item
            ? manager->UseAction(actionType, actionId, target, 0xFFFF, 0, 0, null)
            : manager->UseAction(actionType, actionId, target);
    }
}
