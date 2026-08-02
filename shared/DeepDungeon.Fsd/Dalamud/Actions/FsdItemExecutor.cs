using FFXIVClientStructs.FFXIV.Client.Game;

namespace DeepDungeon.Fsd.Dalamud.Actions;

public static class FsdItemExecutor
{
    public static unsafe bool IsItemReady(uint baseItemId, bool isHq = false)
    {
        var itemId = ResolveItemId(baseItemId, isHq);
        var manager = ActionManager.Instance();
        return itemId != 0 && manager != null &&
               manager->GetActionStatus(ActionType.Item, itemId) == Service.ActionStatus_Ready &&
               manager->IsActionOffCooldown(ActionType.Item, itemId);
    }

    public static bool UseItem(uint baseItemId, bool isHq = false)
    {
        var itemId = ResolveItemId(baseItemId, isHq);
        return itemId != 0 && FsdActionExecutor.TryUseAction(
            ActionType.Item, itemId, 0xE0000000UL, requireReady: true);
    }

    private static uint ResolveItemId(uint baseItemId, bool isHq)
        => baseItemId == 0 ? 0 : isHq ? baseItemId + 1_000_000u : baseItemId;
}
