using System;
using System.Linq;
using DeepDungeon.Fsd.Dalamud.Actions;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

public sealed partial class FloorPhaseController
{
    private void RecordBossCombatSnapshot()
    {
        var runtime = _floorRuntime;
        var player = Service.LocalPlayer;
        var now = DateTime.UtcNow;
        if (runtime == null || player == null || now < runtime.NextBossDiagnosticAtUtc) return;
        runtime.NextBossDiagnosticAtUtc = now.AddSeconds(1);
        bool potionKnown = DeepDungeonHelper.TryGetRecoveryPotionForCurrentDungeon(out uint potionId, out _);
        RecordReplayEvent("boss-combat-snapshot", new
        {
            floor = runtime.Floor,
            player = new { hp = player.CurrentHp, maxHp = player.MaxHp, job = player.ClassJob.RowId,
                position = new { player.Position.X, player.Position.Y, player.Position.Z },
                player.IsDead, player.IsCasting, player.CastActionId,
                statuses = player.StatusList.Select(s => new { s.StatusId, s.Param, s.RemainingTime }).ToArray() },
            inCombat = Service.Condition[ConditionFlag.InCombat],
            targetId = Service.TargetManager.Target?.GameObjectId ?? 0,
            mechanicsActive = _ctx?.Configuration.BossMechanicsActive == true,
            pendingItem = runtime.PendingFloorItemUse?.Key,
            strengthStock = _pomanderManager.GetCount(2), steelStock = _pomanderManager.GetCount(3), hasteStock = _pomanderManager.GetCount(11),
            strengthUsable = _pomanderManager.IsUsable(2), steelUsable = _pomanderManager.IsUsable(3),
            recoveryPotion = potionKnown ? (uint?)potionId : null,
            recoveryPotionReady = potionKnown && FsdItemExecutor.IsItemReady(potionId),
            enemies = Service.GameObjects.OfType<IBattleNpc>()
                .Where(e => (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)e.SubKind ==
                    global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant)
                .Select(e => new { id = e.GameObjectId, e.BaseId, hp = e.CurrentHp, maxHp = e.MaxHp,
                    e.IsTargetable, e.IsDead, e.IsCasting, e.CastActionId,
                    castRemaining = e.TotalCastTime - e.CurrentCastTime,
                    position = new { e.Position.X, e.Position.Y, e.Position.Z } }).ToArray()
        });
    }
}
