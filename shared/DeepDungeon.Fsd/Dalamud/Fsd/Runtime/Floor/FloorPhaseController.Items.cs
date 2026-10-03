using System;
using DeepDungeon.Fsd.Core;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

public sealed partial class FloorPhaseController
{
    private FarmingItemPolicy ItemPolicy => FarmingItemPolicy.For(_ctx?.FarmingPlan?.Mode);
    private bool EntryIncenseWindowOpen(FloorRuntime runtime) =>
        runtime.Kind == FloorRuntimeKind.Normal && ItemPolicy.AllowsEntryIncense(
            _phase == FloorPhase.FloorSetup, (DateTime.UtcNow - runtime.ReadyAtUtc).TotalSeconds);

    private bool CombatBuffUsable(uint slot, bool overcapRelief = false) =>
        IsPomanderAvailableForFloorUse(slot) && ItemPolicy.AllowsCombatBuff(
            _pomanderManager.GetCount(slot), _ctx?.Duty.IsBossFloor == true, overcapRelief);

    private static float LocalStatusRemaining(uint statusId, bool zeroMeansPermanent = false)
    {
        var player = Service.LocalPlayer;
        if (player != null)
            foreach (var status in player.StatusList)
                if (status.StatusId == statusId)
                    return zeroMeansPermanent && status.RemainingTime == 0 ? float.PositiveInfinity : Math.Abs(status.RemainingTime);
        return 0;
    }

    private unsafe void TryMaintainBossBuffs(InstanceContentDeepDungeon* dd)
    {
        if (_ctx?.ControlledPtSurvey != null || !CanAttemptPomanderUse(allowCombat: true)) return;
        // Defence first. Refresh shortly before expiry rather than repeatedly overwriting active buffs.
        if (CombatBuffUsable(FloorInitPlanner.SteelPomanderSlotIndex) &&
            FarmingItemPolicy.NeedsBossRefresh(LocalStatusRemaining(SteelStatusId)) &&
            TryUsePomander(FloorInitPlanner.SteelPomanderSlotIndex, dd, "boss steel refresh")) return;
        if (CombatBuffUsable(FloorInitPlanner.StrengthPomanderSlotIndex) &&
            FarmingItemPolicy.NeedsBossRefresh(LocalStatusRemaining(StrengthStatusId)))
            if (TryUsePomander(FloorInitPlanner.StrengthPomanderSlotIndex, dd, "boss strength refresh")) return;
        if (dd->DeepDungeonId == 4 && CombatBuffUsable(FloorInitPlanner.HastePomanderSlotIndex) &&
            FarmingItemPolicy.NeedsBossRefresh(LocalStatusRemaining(PtHasteStatusId)))
            if (TryUsePomander(FloorInitPlanner.HastePomanderSlotIndex, dd, "boss haste refresh")) return;
        if (dd->DeepDungeonId == 4 && _floorRuntime is { } runtime)
        {
            // Demiclone row 5/native stone 3, not the floor-effect removal pomander.
            float remaining = LocalStatusRemaining(4584, zeroMeansPermanent: true); // Tranquil Moonlight: maximum HP.
            if (!FarmingItemPolicy.NeedsBossRefresh(remaining)) runtime.BossSerenityAwaitingBuff = false;
            if (ItemPolicy.AllowsBossSerenity(GetStoneCountAvailableForFloorUse(3), _ctx?.Duty.IsBossFloor == true,
                remaining, runtime.BossSerenityAwaitingBuff))
                TryDispatchFloorStone(3, dd, "boss serenity incense", FloorItemUsePurpose.BossSerenity);
        }
    }
}
