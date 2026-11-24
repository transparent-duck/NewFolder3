using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private bool _magiciteAwaitingRelease;

    internal void ObserveMagiciteUse(byte typeId)
    {
        if (DungeonId != 2 || IsDisposed || typeId is < 1 or > 4) return;
        _ctx?.Knowledge.Floor?.RecordConfirmedStoneUse(typeId);
        if (ItemUse.Pending is { Key.Kind: FloorItemUseKind.Stone } pending && pending.Key.ItemId == typeId)
            pending.ObserveAuthoritativeConfirmation();
        _magiciteAwaitingRelease = true;
        RecordReplayEvent("magicite-use-observed", new { floor = Floor, typeId });
    }

    private unsafe bool AwaitingFloorClearRelease()
    {
        if (_magiciteAwaitingRelease)
        {
            var actions = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
            if (ItemUse.Pending is { Key.Kind: FloorItemUseKind.Stone } ||
                Service.LocalPlayer?.IsCasting == true || actions != null && actions->AnimationLock > 0) return true;
            _magiciteAwaitingRelease = false;
        }
        return DungeonId == 2 && ItemUse.Pending is { Key.Kind: FloorItemUseKind.Stone,
            Purpose: FloorItemUsePurpose.FloorClear or FloorItemUsePurpose.SilverChestOvercap };
    }

    internal unsafe bool TryUseHohMagicite(InstanceContentDeepDungeon* dd)
    {
        if (dd->DeepDungeonId != 2 || Kind != FloorRuntimeKind.Normal || UsesProgressionItemPolicy) return false;
        if (AwaitingFloorClearRelease()) return true;
        if (dd->Floor <= 90) return false;
        var knowledge = _ctx?.Knowledge.Floor;
        bool used = knowledge != null && (knowledge.WasStoneUseConfirmed(1) || knowledge.WasStoneUseConfirmed(2) ||
            knowledge.WasStoneUseConfirmed(3) || knowledge.WasStoneUseConfirmed(4));
        if (!HohMagicitePolicy.ShouldUse(dd->Floor, overflow: false, alreadyUsedThisFloor: used)) return false;
        // Dispatch through the existing single item channel; consumption/log
        // confirmation, retries and deferral remain owned by FloorItemExecutor.
        byte typeId = FindAvailableHohMagicite(dd);
        if (typeId == 0) return false;
        if (!TryDispatchFloorStone(typeId, dd, "HoH floor clear after 90", FloorItemUsePurpose.FloorClear)) return false;
        _magiciteAwaitingRelease = true;
        return true;
    }

    private unsafe byte FindAvailableHohMagicite(InstanceContentDeepDungeon* dd)
    {
        if (!DeepDungeonFloorItemUsePolicy.CanUseStones(dd->DeepDungeonId, dd->DeepDungeonBanId)) return 0;
        foreach (byte typeId in dd->Magicite)
            if (typeId is >= 1 and <= 4 && !Items.IsStoneBlockedForFloor(typeId)) return typeId;
        return 0;
    }

    private unsafe byte SelectSilverOvercapStone(InstanceContentDeepDungeon* dd)
    {
        if (dd->DeepDungeonId == 2)
        {
            if (UsesProgressionItemPolicy && !CanSpendProgressionCurrentFloorItem()) return 0;
            return HohMagicitePolicy.ShouldUse(dd->Floor, overflow: true, alreadyUsedThisFloor: false)
                ? FindAvailableHohMagicite(dd) : (byte)0;
        }
        if (dd->DeepDungeonId != 4) return 0;

        var decision = SilverChestOvercapPolicy.Decide(Coord.SiteSearchBoundary.ReadStoneCount(dd),
            GetStoneCountAvailableForFloorUse(3) > 0,
            DeepDungeonFloorItemUsePolicy.CanUsePtIncense(dd->DeepDungeonBanId));
        return decision == SilverChestOvercapDecision.UseSerenityIncense ? (byte)3 : (byte)0;
    }
}
