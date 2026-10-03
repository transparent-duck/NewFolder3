using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

public sealed partial class FloorPhaseController
{
    private bool YieldToMobMechanics()
    {
        var runtime = _floorRuntime;
        var player = Service.LocalPlayer;
        if (runtime == null || player == null) return false;
        var now = DateTime.UtcNow;
        bool enabled = _ctx?.Configuration.BossMechanicsActive == true &&
            _ctx.Configuration.BossMechanics.Provider == FsdBossMechanicsProvider.Bmr;
        if (!enabled || now >= runtime.NextMobMechanicsCheckUtc)
        {
            runtime.NextMobMechanicsCheckUtc = now.AddMilliseconds(100);
            IBattleNpc? caster = null;
            int zones = 0;
            if (enabled)
            {
                foreach (var obj in Service.GameObjects)
                {
                    if (obj is IBattleNpc npc && (BattleNpcSubKind)npc.SubKind == BattleNpcSubKind.Combatant && !npc.IsDead && npc.IsCasting &&
                        Vector3.DistanceSquared(player.Position, npc.Position) <= 40f * 40f)
                    {
                        caster = npc;
                        break;
                    }
                }
                if (caster != null)
                {
                    try
                    {
                        _bossForbiddenZonesIpc ??= Service.PluginInterface.GetIpcSubscriber<int>("BossMod.Hints.ForbiddenZonesCount");
                        if (_bossForbiddenZonesIpc.HasFunction) zones = _bossForbiddenZonesIpc.InvokeFunc();
                    }
                    catch { /* Missing optional IPC cannot acquire movement ownership. */ }
                }
            }
            bool yielding = runtime.MobMechanicsGuard.Update(enabled, caster != null, zones, now);
            if (yielding != runtime.MobMechanicsYielding)
            {
                if (yielding) _navDriver?.Cancel();
                _taskRunner?.SetSuspended(yielding);
                Service.Log.Info($"[MobMechanics] floor={runtime.Floor} yielding={yielding} caster={caster?.GameObjectId} action={caster?.CastActionId} zones={zones}");
                RecordReplayEvent("mob-mechanics-movement-ownership", new
                {
                    floor = runtime.Floor, yielding, zones,
                    casterId = caster?.GameObjectId, baseId = caster?.BaseId, actionId = caster?.CastActionId,
                    movement = ReadBossMovementDiagnostics(player.Position)
                });
                runtime.MobMechanicsYielding = yielding;
            }
            if ((yielding || caster != null) && now >= runtime.NextMobMechanicsDiagnosticUtc)
            {
                runtime.NextMobMechanicsDiagnosticUtc = now.AddSeconds(1);
                RecordReplayEvent("mob-mechanics-snapshot", new
                {
                    floor = runtime.Floor, hp = player.CurrentHp, maxHp = player.MaxHp,
                    position = new { player.Position.X, player.Position.Y, player.Position.Z },
                    casterId = caster?.GameObjectId, baseId = caster?.BaseId, actionId = caster?.CastActionId,
                    castRemaining = caster == null ? (float?)null : caster.TotalCastTime - caster.CurrentCastTime,
                    zones, movement = ReadBossMovementDiagnostics(player.Position)
                });
            }
        }
        if (runtime.MobMechanicsYielding) _status = "Mob mechanic: yielding movement to BMR";
        return runtime.MobMechanicsYielding;
    }
}
