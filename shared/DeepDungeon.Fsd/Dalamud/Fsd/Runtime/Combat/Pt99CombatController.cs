using System;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using global::Dalamud.Game.ClientState.Objects.Types;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat;

/// <summary>Language-independent dual-boss targeting; never takes movement from BMR.</summary>
internal sealed class Pt99CombatController
{
    private DateTime _nextScan, _nextLog;
    private bool _suppressed;
    private Pt99TargetKind? _lastKind;
    private IBattleChara? _target;
    internal Pt99TargetDecision Decision { get; private set; }

    internal void Tick(RunContext context, out string status)
    {
        var player = Service.LocalPlayer;
        if (player == null) { status = "PT99 - waiting for player"; return; }
        var now = DateTime.UtcNow;
        if (now >= _nextScan)
        {
            _nextScan = now.AddMilliseconds(100);
            IBattleChara? grief = null, eater = null, minion = null;
            float minionDistance = float.MaxValue;
            foreach (var obj in Service.GameObjects)
            {
                if (obj is not IBattleNpc enemy || enemy.IsDead ||
                    Vector3.DistanceSquared(player.Position, enemy.Position) > 100 * 100) continue;
                switch (enemy.BaseId)
                {
                    case Pt99TargetPolicy.GriefBaseId: grief = enemy; break;
                    case Pt99TargetPolicy.EaterBaseId: eater = enemy; break;
                    case Pt99TargetPolicy.MinionBaseId when enemy.IsTargetable:
                        float distance = Vector3.DistanceSquared(player.Position, enemy.Position);
                        if (distance < minionDistance) { minion = enemy; minionDistance = distance; }
                        break;
                }
            }
            bool light = false, dark = false;
            foreach (var buff in player.StatusList)
            {
                light |= buff.StatusId == Pt99TargetPolicy.LightStatusId;
                dark |= buff.StatusId == Pt99TargetPolicy.DarkStatusId;
            }
            Decision = Pt99TargetPolicy.Decide(light, dark, grief?.CurrentHp ?? 0, grief?.MaxHp ?? 0,
                eater?.CurrentHp ?? 0, eater?.MaxHp ?? 0, minion != null);
            _target = Decision.Target switch
            {
                Pt99TargetKind.Minion => minion,
                Pt99TargetKind.Grief when grief?.IsTargetable == true => grief,
                Pt99TargetKind.Eater when eater?.IsTargetable == true => eater,
                _ => null
            };
            bool suppress = Decision.SuppressOutput || _target == null;
            if (suppress != _suppressed)
            {
                _suppressed = suppress;
                context.SetBossRotationSuppressed?.Invoke(suppress);
            }
            if (_lastKind != Decision.Target || now >= _nextLog)
            {
                _lastKind = Decision.Target;
                _nextLog = now.AddSeconds(2);
                Service.Log.Info($"[PT99.Target] choice={Decision.Target}, target={_target?.GameObjectId}, light={light}, dark={dark}, grief={grief?.CurrentHp}/{grief?.MaxHp}, eater={eater?.CurrentHp}/{eater?.MaxHp}, hpDifference={Decision.HpDifference:F2}, suppressed={suppress}; external movement retained");
            }
        }
        Service.TargetManager.Target = _target is { IsDead: false, IsTargetable: true } ? _target : null;
        status = $"PT99 - {Decision.Target} (HP difference {Decision.HpDifference:F1}%)";
    }
}
