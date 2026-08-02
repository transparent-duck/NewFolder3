using global::Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DeepDungeon.Fsd.Dalamud.Actions;

public static class FsdActionExecutor
{
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
