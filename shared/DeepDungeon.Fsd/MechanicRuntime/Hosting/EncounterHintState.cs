using System;
using System.Numerics;
using BossMod;

namespace MasterPlayer.MechanicRuntime;

public sealed record EncounterHintTarget(ulong Id, Vector3 Position, float Radius);

/// <summary>
/// A frame-scoped view of upstream navigation policy. Geometry and goal
/// delegates are evaluated synchronously on the framework thread.
/// </summary>
public sealed record EncounterHintState(
    Func<WPos, float>[] Goals, Bitmap.Region ObstacleMap,
    AIHints.SpecialMode Mode, DateTime ModeActivation, DateTime ModeFinish,
    bool ForceCancelCast, float MaxCastTime,
    ulong ForcedTargetId, ulong PreferredTargetId, ulong ClearTargetId,
    ulong InteractTargetId, bool KeepMoving, Vector2 PreferredDirection)
{
    public StackPositionGoal[] StackGoals { get; init; } = [];
    public string ModuleName { get; init; } = "";
    public bool PendingKnockback { get; init; }
    // A candidate from the generic enemy list alone does not override FSD targeting.
    public bool HasTargetDirective { get; init; }
    public bool HoldFire => HasTargetDirective && AttackTarget == null;
    public EncounterHintTarget? AttackTarget { get; init; }
    public Vector3? ForcedMovement { get; init; }
    public Angle? LockedFacing { get; init; }
    public DateTime FacingExpire { get; init; }
    public (Angle Center, Angle HalfWidth, DateTime Activation)[] ForbiddenDirections { get; init; } = [];

    public bool ModeActive(DateTime now) =>
        now.AddSeconds(EncounterHintPlanner.ImpactLeadSeconds) >= ModeActivation
        && (ModeFinish == default || now <= ModeFinish);

    public bool HoldMovement(DateTime now) =>
        Mode is AIHints.SpecialMode.NoMovement or AIHints.SpecialMode.Pyretic && ModeActive(now);

    public bool RequireMovement(DateTime now) =>
        KeepMoving || Mode == AIHints.SpecialMode.Freezing && ModeActive(now);
}
