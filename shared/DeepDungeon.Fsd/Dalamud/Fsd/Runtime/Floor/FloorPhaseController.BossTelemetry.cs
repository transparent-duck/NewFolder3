using System;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using DeepDungeon.Fsd.Dalamud.Actions;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using global::Dalamud.Game.ClientState.Objects.SubKinds;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

public sealed partial class FloorPhaseController
{
    private unsafe void RecordBossCombatSnapshot(byte floor, IPlayerCharacter player)
    {
        var actionManager = ActionManager.Instance();
        bool potionKnown = DeepDungeonHelper.TryGetRecoveryPotionForCurrentDungeon(out uint potionId, out _);
        RecordReplayEvent("boss-combat-snapshot", new
        {
            floor = floor,
            player = new { hp = player.CurrentHp, maxHp = player.MaxHp, job = player.ClassJob.RowId,
                position = new { player.Position.X, player.Position.Y, player.Position.Z },
                player.Rotation, player.IsDead, player.IsCasting, player.CastActionId,
                statuses = player.StatusList.Select(s => new { s.StatusId, s.Param, s.RemainingTime }).ToArray() },
            inCombat = Service.Condition[ConditionFlag.InCombat],
            animationLock = actionManager != null ? (float?)actionManager->AnimationLock : null,
            itemDispatchReady = PomanderManager.CanDispatchItemRequest(),
            targetId = Service.TargetManager.Target?.GameObjectId ?? 0,
            targetProvider = _ctx?.CombatAssist.TargetSource,
            selectedTargetId = _ctx?.CombatAssist.SelectedId,
            pendingItem = _floorRuntime!.ItemUse.Pending?.Key,
            strengthStock = _pomanderManager.GetCount(2), steelStock = _pomanderManager.GetCount(3), hasteStock = _pomanderManager.GetCount(11),
            strengthUsable = _pomanderManager.IsUsable(2), steelUsable = _pomanderManager.IsUsable(3),
            recoveryPotion = potionKnown ? (uint?)potionId : null,
            recoveryPotionReady = potionKnown && FsdItemExecutor.IsItemReady(potionId),
            enemies = Service.GameObjects.OfType<IBattleNpc>()
                .Select(e => new { id = e.GameObjectId, e.BaseId, e.SubKind, hp = e.CurrentHp, maxHp = e.MaxHp,
                    e.IsTargetable, e.IsDead, e.IsCasting, e.CastActionId,
                    castRemaining = e.TotalCastTime - e.CurrentCastTime,
                    statuses = e.StatusList.Select(s => new { s.StatusId, s.Param, s.RemainingTime }).ToArray(),
                    position = new { e.Position.X, e.Position.Y, e.Position.Z } }).ToArray(),
            // PT20 roots are event objects, and their helpers are non-combatant battle NPCs.
            // Keeping only combatants hid the actual persistent hazard in the first wipe trace.
            roots = floor == 20 ? Service.GameObjects.Where(e => e.BaseId == 0x1EBE4B)
                .Select(e => new { id = e.GameObjectId, e.BaseId, e.Rotation,
                    position = new { e.Position.X, e.Position.Y, e.Position.Z } }).ToArray() : null
        });
    }
}
