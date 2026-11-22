using System.Numerics;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Coord;
using DeepDungeon.Fsd.Dalamud.Encounter;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

/// <summary>Combat policy for a persistent attack goal. Navigation owns the region,
/// path and hold state. This policy observes damage, requests another stand, or releases a target.</summary>
internal sealed class CombatPositionRecoveryController(RunContext context, EnemyChaseHelper chase,
    RunFloorTelemetryTrace? telemetry, CombatActionEvidence actionEvidence, Action<string> setStatus,
    Action<string, object> record)
{
    private ulong _id;
    private uint _entityId;
    private uint _hp;
    private bool _aggroObserved;
    private DateTime _lastUpdate;
    private Vector3 _lastPosition;
    private TimeSpan _stalledMovementTime;
    private bool _recovering, _hadAttackOpportunity, _wasDriving;
    private int _failures;
    internal ulong BlockedAggroId { get; private set; }
    private TimeSpan _attackTime;
    private bool _suspended;
    private DateTime _attackWindowStartedAt;

    internal void Suspend()
    {
        _suspended = true;
        _hadAttackOpportunity = _wasDriving = false;
    }

    internal void ObserveTargetEngagement(NavigationSurface surface)
    {
        if (_id == 0 || _suspended || EnemyChaseHelper.ReadAggro(_entityId) is not bool hasAggro) return;
        if (hasAggro)
        {
            _aggroObserved = true;
            return;
        }
        if (_aggroObserved && Service.GameObjects.SearchById(_id) is IBattleChara { IsDead: false, CurrentHp: > 0 } target)
            FailTarget(surface, "target-disengaged", target.CurrentHp);
    }

    internal NavOutcome Navigate(NavigationSurface surface, IBattleChara target, Vector3 player,
        Vector3 targetPosition, float range, DateTime now)
    {
        var id = target.GameObjectId;
        if (id != _id)
        {
            _id = id;
            _entityId = target.EntityId;
            _hp = target.CurrentHp;
            _aggroObserved = EnemyChaseHelper.IsAggroedToPlayer(_entityId);
            _lastUpdate = now;
            _attackTime = _stalledMovementTime = default;
            _recovering = _hadAttackOpportunity = _wasDriving = false;
            _lastPosition = player;
            _attackWindowStartedAt = default;
        }
        if (_suspended)
        {
            _lastUpdate = now;
            _suspended = false;
            ObserveTargetEngagement(surface);
            if (_id == 0) return NavOutcome.Pending;
        }
        bool needsSafety = surface.NeedsSafetyPlan;
        var state = surface.NavigateToAttack(id, targetPosition, player, range);
        var region = surface.AttackRegion;
        var elapsed = TimeSpan.FromSeconds(Math.Clamp((now - _lastUpdate).TotalSeconds, 0, .5));
        _lastUpdate = now;

        if (target.CurrentHp < _hp)
        {
            if (BlockedAggroId == id) BlockedAggroId = 0;
            _attackTime = default;
            _attackWindowStartedAt = default;
            if (_recovering)
            {
                record("sight-recovery-completed", new { targetId = id, reason = "target-hp-progress" });
                _recovering = false;
                region.ClearRejected();
            }
            elapsed = default;
        }
        _hp = target.CurrentHp;
        if (region.Holding || Vector3.DistanceSquared(player, _lastPosition) > .04f)
        {
            _stalledMovementTime = default;
            _lastPosition = player;
        }
        bool canAttack = region.Holding && target.IsTargetable && !needsSafety &&
            !MasterPlayer.MechanicRuntime.MechanicExecutionState.ActionsBlocked &&
            Service.TargetManager.Target?.GameObjectId == id;
        if (canAttack)
        {
            if (_attackWindowStartedAt == default) _attackWindowStartedAt = now;
            if (_hadAttackOpportunity) _attackTime += elapsed;
        }
        _hadAttackOpportunity = canAttack;
        var attackLimit = _recovering ? TimeSpan.FromSeconds(6) : CombatPositionPolicy.UnknownNoProgressLimit;
        if (canAttack && _attackTime >= attackLimit)
        {
            if (_recovering)
                record("sight-recovery-position-rejected", new { targetId = id, reason = "attack-no-progress" });
            else
            {
                RecordStalledAttack(target, player, region.Holding, now);
                telemetry?.ObserveStalledEngageRecoveryStarted();
                record("sight-recovery-started", new { targetId = id, reason = "no-target-progress", failures = _failures });
            }
            _recovering = true;
            _attackTime = default;
            _attackWindowStartedAt = default;
            _hadAttackOpportunity = false;
            if (region.Path is { } path) region.Reject(path.Points[^1]);
            region.Reject(player);
        }

        bool driving = state == NavigationState.Moving && surface.InputEnabled && !needsSafety;
        if (driving && _wasDriving) _stalledMovementTime += elapsed;
        _wasDriving = driving;
        bool stalledMovement = driving && _stalledMovementTime >= CombatPositionPolicy.AttemptLimit;
        if (!needsSafety && (region.Unreachable || state == NavigationState.Failed || stalledMovement))
        {
            var reason = region.Unreachable ? "no-reachable-standing-position"
                : surface.Failure ?? "combat-position-no-progress";
            if (!EnemyChaseHelper.IsAggroedToPlayer(_entityId))
                return FailTarget(surface, reason, _hp);

            var status = $"已交戰，等待可攻擊站位：{reason}";
            if (BlockedAggroId != id)
            {
                BlockedAggroId = id;
                _failures++;
                record("combat-position-blocked", new { targetId = id, reason, hp = _hp, failures = _failures });
            }
            if (stalledMovement)
            {
                _stalledMovementTime = default;
                surface.CancelAttack(id);
            }
            setStatus(status);
            return NavOutcome.Pending;
        }
        if (BlockedAggroId == id && state == NavigationState.Arrived) BlockedAggroId = 0;
        return state == NavigationState.Arrived ? NavOutcome.Arrived : NavOutcome.Moving;
    }

    private void RecordStalledAttack(IBattleChara target, Vector3 player, bool holding, DateTime now)
    {
        try
        {
            var position = target.Position;
            record("combat-attack-no-progress", new
            {
                floor = context.Duty.Floor,
                observedAtUtc = now,
                attackWindowStartedAtUtc = _attackWindowStartedAt,
                inCombat = Service.Condition[ConditionFlag.InCombat],
                fsdTargetId = target.GameObjectId,
                fsdTargetEntityId = target.EntityId,
                gameTargetId = Service.TargetManager.Target?.GameObjectId,
                hp = target.CurrentHp,
                maxHp = target.MaxHp,
                targetable = target.IsTargetable,
                holding,
                playerPosition = new { player.X, player.Y, player.Z },
                targetPosition = new { position.X, position.Y, position.Z },
                native = actionEvidence.Snapshot(_attackWindowStartedAt),
            });
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "[FSD] Could not record combat attack evidence.");
        }
    }

    private NavOutcome FailTarget(NavigationSurface surface, string reason, uint hp)
    {
        var id = _id;
        _failures++;
        record("clearing-target-deprioritized", new { targetId = id, reason, hp,
            failures = _failures });
        if (BlockedAggroId == id) BlockedAggroId = 0;
        context.SuppressCombatTarget(id);
        chase.ReleaseTarget(id);
        context.ClearingTargetId = null;
        surface.CancelAttack(id);
        _id = 0;
        _attackWindowStartedAt = default;
        setStatus("Switching target after combat position recovery failed");
        return NavOutcome.Pending;
    }

}
