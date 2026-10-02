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

    private static float LocalStatusRemaining(uint statusId)
    {
        var player = Service.LocalPlayer;
        if (player != null)
            foreach (var status in player.StatusList)
                if (status.StatusId == statusId) return Math.Abs(status.RemainingTime);
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
            TryUsePomander(FloorInitPlanner.StrengthPomanderSlotIndex, dd, "boss strength refresh");
    }
}
