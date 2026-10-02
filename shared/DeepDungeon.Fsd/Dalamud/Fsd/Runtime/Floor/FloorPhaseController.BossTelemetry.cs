using System;
using System.Linq;
using System.Numerics;
using global::Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;
using DeepDungeon.Fsd.Dalamud.Actions;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

public sealed partial class FloorPhaseController
{
    private ICallGateSubscriber<Vector3?>? _bossAiTargetIpc;
    private ICallGateSubscriber<Vector3, Vector3, bool>? _bossAiPathSafeIpc;
    private ICallGateSubscriber<int>? _bossForbiddenZonesIpc;

    private object ReadBossMovementDiagnostics(Vector3 playerPosition)
    {
        Vector3? target = null;
        bool? pathSafe = null;
        int? forbiddenZones = null;
        try
        {
            _bossAiTargetIpc ??= Service.PluginInterface.GetIpcSubscriber<Vector3?>("BossMod.AI.NaviTargetPos");
            _bossAiPathSafeIpc ??= Service.PluginInterface.GetIpcSubscriber<Vector3, Vector3, bool>("BossMod.Hints.IsDashSafe");
            _bossForbiddenZonesIpc ??= Service.PluginInterface.GetIpcSubscriber<int>("BossMod.Hints.ForbiddenZonesCount");
            if (_bossAiTargetIpc.HasFunction) target = _bossAiTargetIpc.InvokeFunc();
            if (_bossAiPathSafeIpc.HasFunction && target.HasValue)
                pathSafe = _bossAiPathSafeIpc.InvokeFunc(playerPosition, target.Value);
            if (_bossForbiddenZonesIpc.HasFunction) forbiddenZones = _bossForbiddenZonesIpc.InvokeFunc();
        }
        catch { /* Optional diagnostic IPC must not interrupt a fight. */ }
        return new { target = target.HasValue ? new { target.Value.X, target.Value.Y, target.Value.Z } : null, pathSafe, forbiddenZones };
    }

    private unsafe void RecordBossCombatSnapshot()
    {
        var runtime = _floorRuntime;
        var player = Service.LocalPlayer;
        var now = DateTime.UtcNow;
        if (runtime == null || player == null || now < runtime.NextBossDiagnosticAtUtc) return;
        runtime.NextBossDiagnosticAtUtc = now.AddSeconds(1);
        var actionManager = ActionManager.Instance();
        bool potionKnown = DeepDungeonHelper.TryGetRecoveryPotionForCurrentDungeon(out uint potionId, out _);
        RecordReplayEvent("boss-combat-snapshot", new
        {
            floor = runtime.Floor,
            player = new { hp = player.CurrentHp, maxHp = player.MaxHp, job = player.ClassJob.RowId,
                position = new { player.Position.X, player.Position.Y, player.Position.Z },
                player.IsDead, player.IsCasting, player.CastActionId,
                statuses = player.StatusList.Select(s => new { s.StatusId, s.Param, s.RemainingTime }).ToArray() },
            inCombat = Service.Condition[ConditionFlag.InCombat],
            animationLock = actionManager != null ? (float?)actionManager->AnimationLock : null,
            itemDispatchReady = PomanderManager.CanDispatchItemRequest(),
            movement = ReadBossMovementDiagnostics(player.Position),
            targetId = Service.TargetManager.Target?.GameObjectId ?? 0,
            mechanicsActive = _ctx?.Configuration.BossMechanicsActive == true,
            pt30OrbitActive = _pt30DivineFavorFlashHelper?.IsDivineFavorMovementActive == true,
            pt50RecoveryActive = _pt50QuicksandRecoveryHelper?.IsActive == true,
            pendingItem = runtime.PendingFloorItemUse?.Key,
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
            roots = runtime.Floor == 20 ? Service.GameObjects.Where(e => e.BaseId == 0x1EBE4B)
                .Select(e => new { id = e.GameObjectId, e.BaseId, e.Rotation,
                    position = new { e.Position.X, e.Position.Y, e.Position.Z } }).ToArray() : null
        });
    }
}
