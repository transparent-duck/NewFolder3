using System;
using System.Collections.Generic;
using System.Numerics;
using BossMod;
using BossMod.Components;

namespace MasterPlayer.MechanicRuntime;

/// <summary>Shared hint conversion with locally maintained encounter corrections;
/// no private mechanic queues or module-specific lifecycle reconstruction.</summary>
public sealed class EncounterHintSnapshotBuilder
{
    private EncounterPlanningSnapshot? _last;
    private Actor? _lastBoss;
    private readonly Pt20HintAdapter _pt20 = new();
    private readonly Pt50HintAdapter _pt50 = new();
    public void Reset()
    {
        _last = null;
        _lastBoss = null;
        _pt50.Reset();
    }

    public EncounterPlanningSnapshot? Capture(MechanicSession session, AIHints hints)
    {
        var world = session.World;
        var player = world.Party.Player();
        if (player == null || _last is { } previous && previous.Territory != world.CurrentZone)
            Reset();
        if (player == null) return null;
        var module = session.BossModules.ActiveModule;
        if (module == null)
        {
            // A dead actor can outlive module selection. Disappearance alone
            // is not completion evidence.
            if (_last != null && _lastBoss is { IsDead: true })
                return _last = _last with { CapturedAt = world.CurrentTime, BossDead = true, BossTargetable = false };
            return null;
        }
        var boss = module.PrimaryActor;
        var now = world.CurrentTime;
        var active = module.StateMachine.ActivePhase != null;
        var knockbacks = active ? hints.Knockbacks.OrderBy(k => k.Source.Activation).ToArray() : [];
        for (var i = 0; i < knockbacks.Length; ++i)
        {
            var prediction = knockbacks[i];
            var source = prediction.Source;
            var casterOid = world.Actors.Find(source.ActorID)?.OID ?? 0;
            knockbacks[i] = prediction with
            {
                Timing = KnockbackMotionData.Resolve(source.ActionID, casterOid, source.Distance,
                    source.Kind, source.ActivationIsEffect)
            };
        }
        HashSet<GenericChasingAOEs.Chaser>? simulatedSteering = null;
        var zones = new List<EncounterHazard>(hints.ForbiddenZones.Count);
        foreach (var zone in hints.ForbiddenZones)
        {
            if (zone.SteeringFor is { NumRemaining: > 0, Shape: AOEShapeCircle, Target: { IsDead: false, IsDestroyed: false } } chaser)
            {
                (simulatedSteering ??= []).Add(chaser);
                continue;
            }
            if (zone.SingleImpact && zone.shapeDistance is SDKnockbackInSquareFixedDirection square
                && MoveLandingRestriction(knockbacks, zone.activation, square))
                continue;
            zones.Add(EncounterHazard.FromHint(zone) with
            {
                Geometry = TryCircle(zone.shapeDistance, out var origin, out var radius)
                    ? new("Circle", origin.X, origin.Z, OuterRadius: radius) : null
            });
        }

        if (module is BossMod.Endwalker.Trial.T02Hydaelyn.T02Hydaelyn)
            module.FindComponent<HydaelynLightwave>()?.AddForecast(zones, now);
        Pt10HintAdapter.Adapt(module, player, zones);
        _pt20.Adapt(module, player, zones, now);
        var residence = _pt50.Adapt(module, player, zones, now);

        foreach (var obstacle in hints.TemporaryObstacles)
            zones.Add(new(obstacle, default, EncounterHazardKind.Persistent));

        var chasers = new List<EncounterChaser>();
        var adaptedChasers = new List<EncounterChaser>();
        var keepMoving = false;
        var preferred = Vector2.Zero;
        var movement = player.LastFrameMovement;
        if (movement.LengthSq() > 0.0001f)
            preferred = Vector2.Normalize(new(movement.X, movement.Z));
        foreach (var (chaser, step) in hints.Chasers)
        {
            if (chaser.NumRemaining <= 0 || chaser.Target.IsDead || chaser.Target.IsDestroyed)
                continue;
            if (chaser.Shape is not AOEShapeCircle circle) continue;
            chasers.Add(new(chaser.Target.InstanceID, chaser.Target.Position, chaser.PrevPos,
                circle.Radius, chaser.MoveDist, step, chaser.NumRemaining,
                chaser.NextActivation, chaser.SecondsBetweenActivations));
            var adapted = simulatedSteering?.Contains(chaser) == true;
            if (adapted)
                adaptedChasers.Add(chasers[^1]);
            if (chaser.Target.InstanceID != player.InstanceID) continue;
            keepMoving |= !adapted;
            var offset = player.Position - chaser.PrevPos;
            var direction = new Vector2(offset.X, offset.Z);
            if (direction.LengthSquared() < 0.01f)
            {
                direction = preferred;
                if (direction.LengthSquared() < 0.0001f)
                {
                    var radial = player.Position - module.Center;
                    direction = new(-radial.Z, radial.X);
                }
            }
            if (direction.LengthSquared() < 0.0001f) direction = Vector2.UnitX;
            direction = Vector2.Normalize(direction);
            if (preferred == Vector2.Zero) preferred = direction;
        }
        ulong target = 0;
        Actor? targetActor = null;
        var priority = -1;
        foreach (var candidate in hints.PotentialTargets)
        {
            if (candidate.Priority <= priority || !candidate.Actor.IsTargetable
                || candidate.Actor.IsDead || candidate.Actor.IsDestroyed
                || candidate.Actor.InstanceID == hints.ClearTargetID)
                continue;
            target = candidate.Actor.InstanceID;
            targetActor = candidate.Actor;
            priority = candidate.Priority;
        }
        if (targetActor == null && module.StateMachine.ActivePhase == null
            && boss.IsTargetable && !boss.IsDead && boss.InstanceID != hints.ClearTargetID)
        {
            targetActor = boss;
            target = boss.InstanceID;
        }
        if (hints.ForcedTarget is { IsDead: false, IsDestroyed: false, IsTargetable: true } forced
            && forced.InstanceID != hints.ClearTargetID)
            targetActor = forced;
        var clearTarget = hints.ClearTargetID;
        if (clearTarget == 0 && hints.FindEnemy(world.Actors.Find(player.TargetID)) is { Priority: AIHints.Enemy.PriorityForbidden })
            clearTarget = player.TargetID;
        var mode = hints.ImminentSpecialMode;
        var state = new EncounterHintState(hints.GoalZones.ToArray(), hints.PathfindMapObstacles,
            mode.mode, mode.activation, mode.finish, hints.ForceCancelCast, hints.MaxCastTime,
            hints.ForcedTarget?.InstanceID ?? 0, target, clearTarget,
            hints.InteractWithTarget?.InstanceID ?? 0, keepMoving, preferred)
        {
            StackGoals = active ? hints.StackGoals.ToArray() : [],
            ModuleName = module.GetType().FullName ?? module.GetType().Name,
            HasTargetDirective = active && HasTargetDirective(module, player, hints) ||
                hints.ForcedTarget != null || clearTarget != 0,
            AttackTarget = targetActor == null ? null : new(targetActor.InstanceID,
                new(targetActor.Position.X, targetActor.PosRot.Y, targetActor.Position.Z), targetActor.HitboxRadius),
            ForcedMovement = hints.ForcedMovement,
            PendingKnockback = player.PendingKnockbacks.Count > 0,
            LockedFacing = hints.LockedFacing(now),
            FacingExpire = hints.DesiredFacingExpire,
            ForbiddenDirections = hints.ForbiddenDirections.ToArray()
        };
        state = Pt80HintAdapter.Adapt(module, player, state, now);
        AddImpactForecasts(hints, zones, knockbacks, now);
        _lastBoss = boss;
        var snapshot = new EncounterPlanningSnapshot(world.CurrentZone, boss.InstanceID, boss.OID, now,
            new(boss.Position.X, boss.PosRot.Y, boss.Position.Z), boss.HitboxRadius,
            boss.IsTargetable, boss.IsDead, module.Center, module.Bounds,
            zones.ToArray(), [], [], chasers.ToArray())
        { Hints = state, Knockbacks = knockbacks,
            TemporaryObstacles = hints.TemporaryObstacles.ToArray(),
            ChasingHazards = adaptedChasers.ToArray(), PathMechanic = residence };
        var observed = session.Displacements.Capture(snapshot, snapshot.Knockbacks);
        return _last = snapshot with { Knockbacks = observed.Forecasts, Displacement = observed.Current };
    }

    // Only detach a rule whose existing translation exactly matches one
    // unconditional displacement. Keep all other cast-time hint semantics.
    private static bool MoveLandingRestriction(KnockbackPrediction[] knockbacks, DateTime at,
        SDKnockbackInSquareFixedDirection square)
    {
        var match = -1;
        ShapeDistance? unsafeLanding = null;
        for (var i = 0; i < knockbacks.Length; ++i)
        {
            var prediction = knockbacks[i];
            var source = prediction.Source;
            if (source.Activation != at) continue;
            if (match >= 0 || prediction.StopAtWall || prediction.StopAfterWall
                || prediction.PathDependentImmunityStatus != 0 || prediction.UnsafeLanding != null
                || source.Shape != null || source.Kind != GenericKnockback.Kind.DirForward)
                return false;
            unsafeLanding = square.UnsafeLandingFor(source.Distance * source.Direction.ToDirection());
            if (unsafeLanding == null) return false;
            match = i;
        }
        if (match < 0) return false;
        knockbacks[match] = knockbacks[match] with { UnsafeLanding = unsafeLanding };
        return true;
    }

    private static void AddImpactForecasts(AIHints hints, List<EncounterHazard> zones,
        ReadOnlySpan<KnockbackPrediction> knockbacks, DateTime now)
    {
        if (hints.ImpactForecasts.Count == 0 || knockbacks.IsEmpty) return;

        // Include impacts that overlap the time already required for forced movement.
        var through = now;
        foreach (ref readonly var knockback in knockbacks)
        {
            var at = knockback.Source.Activation;
            if (at < now || at == DateTime.MaxValue) continue;
            var end = knockback.Timing.Available ? at.AddSeconds(knockback.Timing.ApplicationDelay + knockback.Timing.Duration) : at;
            if (end > through) through = end;
        }
        foreach (var forecast in hints.ImpactForecasts)
        {
            if (forecast.activation >= now && forecast.activation <= through)
                zones.Add(EncounterHazard.FromHint(forecast) with { Predicted = true });
        }
    }

    private static bool HasTargetDirective(BossModule module, Actor player, AIHints hints)
    {
        // These Pilgrims modules assign even priority zero explicitly. All
        // other modules can communicate a directive by changing an initial priority.
        var explicitPolicy = module is BossMod.Endwalker.DeepDungeon.PilgrimsTraverse.DD10OrnamentalLeafman.DD10OrnamentalLeafman
            or BossMod.Endwalker.DeepDungeon.PilgrimsTraverse.DD40ForgivenNaivety.DD40ForgivenNaivety
            || module.FindComponent<BossMod.Endwalker.DeepDungeon.PilgrimsTraverse.DD99EminentGrief.LightAndDark>() != null;
        var world = module.WorldState;
        var fate = Utils.IsPlayerSyncedToFate(world) ? world.Client.ActiveFate.ID : 0;
        var passive = module.ShouldPrioritizeAllEnemies ? 0 : AIHints.Enemy.PriorityUndesirable;
        return hints.PotentialTargets.Count > 0 && explicitPolicy
            || hints.PotentialTargets.Any(e => e.ShouldBeTargeted || e.Priority != InitialPriority(e.Actor, player, world.Party, fate, passive));
    }

    // BMR's FillEnemies defaults, before any boss priorities are applied.
    private static int InitialPriority(Actor actor, Actor player, PartyState party, uint fate, int passive)
    {
        if (actor.FateID != 0 && actor.FateID != fate || MathF.Abs(actor.PosRot.Y - player.PosRot.Y) > 12)
            return AIHints.Enemy.PriorityInvincible;
        if (actor.PendingDead) return AIHints.Enemy.PriorityPointless;
        return actor.AggroPlayer || actor.InCombat && party.FindSlot(actor.TargetID) >= 0 ? 0 : passive;
    }

    public static bool TryCircle(ShapeDistance shape, out WPos center, out float radius)
    {
        if (shape is SDCircle circle)
        {
            center = circle.Center;
            radius = circle.Radius;
            return true;
        }
        center = default;
        radius = 0;
        return false;
    }
}
