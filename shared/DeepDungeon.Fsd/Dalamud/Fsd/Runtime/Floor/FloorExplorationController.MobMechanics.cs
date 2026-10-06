using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private bool YieldToMobMechanics()
    {
        var player = Service.LocalPlayer;
        if (player == null) return false;
        var now = DateTime.UtcNow;
        bool enabled = _ctx?.Configuration.BossMechanicsActive == true &&
            _ctx.Configuration.BossMechanics.Provider == FsdBossMechanicsProvider.Bmr;
        if (!enabled || now >= this.NextMobMechanicsCheckUtc)
        {
            this.NextMobMechanicsCheckUtc = now.AddMilliseconds(100);
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
                    zones = ReadMobForbiddenZoneCount();
                }
            }
            bool yielding = this.MobMechanicsGuard.Update(enabled, caster != null, zones, now);
            if (yielding != this.MobMechanicsYielding)
            {
                if (yielding) _navDriver?.Cancel();
                _taskRunner?.SetSuspended(yielding);
                Service.Log.Info($"[MobMechanics] floor={this.Floor} yielding={yielding} caster={caster?.GameObjectId} action={caster?.CastActionId} zones={zones}");
                RecordReplayEvent("mob-mechanics-movement-ownership", new
                {
                    floor = this.Floor,
                    yielding,
                    zones,
                    casterId = caster?.GameObjectId,
                    baseId = caster?.BaseId,
                    actionId = caster?.CastActionId,
                    movement = ReadBossMovementDiagnostics(player.Position)
                });
                this.MobMechanicsYielding = yielding;
            }
            if ((yielding || caster != null) && now >= this.NextMobMechanicsDiagnosticUtc)
            {
                this.NextMobMechanicsDiagnosticUtc = now.AddSeconds(1);
                RecordReplayEvent("mob-mechanics-snapshot", new
                {
                    floor = this.Floor,
                    hp = player.CurrentHp,
                    maxHp = player.MaxHp,
                    position = new { player.Position.X, player.Position.Y, player.Position.Z },
                    casterId = caster?.GameObjectId,
                    baseId = caster?.BaseId,
                    actionId = caster?.CastActionId,
                    castRemaining = caster == null ? (float?)null : caster.TotalCastTime - caster.CurrentCastTime,
                    zones,
                    movement = ReadBossMovementDiagnostics(player.Position)
                });
            }
        }
        if (this.MobMechanicsYielding) _status = "Mob mechanic: yielding movement to BMR";
        return this.MobMechanicsYielding;
    }
}
