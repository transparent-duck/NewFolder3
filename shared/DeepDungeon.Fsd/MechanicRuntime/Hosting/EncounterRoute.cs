using System;
using System.Numerics;

namespace MasterPlayer.MechanicRuntime;

public enum EncounterRouteKind { Ready, Approach, NoRoute }

public readonly record struct EncounterRoutePoint(Vector2 Position, double Seconds)
{
    public Vector2? CommandVelocity { get; init; }
    public bool GameDriven { get; init; }
    internal bool KeepMovingToEnd { get; init; }
}

public readonly record struct RecoveryComparison(int HoldHits, double HoldExposure,
    int? CandidateHits, double? CandidateExposure, double? CandidateFirstViolation);

public sealed record EncounterRoute(ulong EncounterId, DateTime OriginUtc,
    EncounterRouteKind Kind, EncounterRoutePoint[] Points,
    int PredictedHits, float Exposure, int Expanded, string? Reason)
{
    internal bool IntervalTiming { get; init; }
    public double? CompletionSlack { get; init; }
    public RecoveryComparison? Recovery { get; init; }
    public bool Timed { get; init; } = true;
    public bool AllowCorrection { get; init; } = true;
    public bool Complete { get; init; } = true;
    internal DeepDungeon.MechanicPlanning.PlanScore? Assessment { get; init; }
}

/// <summary>One frozen planning request — the surface captures everything
/// on the framework thread; the worker never touches game APIs. OriginUtc
/// for any accepted route is THIS request's Now, never the wall clock.</summary>
public readonly record struct EncounterRouteRequest(
    EncounterPlanningSnapshot Snapshot, DateTime Now, ulong PlayerId,
    Vector2 Position, Vector2 Goal, float GoalRadius, float Speed,
    Vector2 PreviousDirection)
{
    public NavigationRegionGoal? Region { get; init; }
    public EncounterRoute? Continuation { get; init; }
    public int ContinuationIndex { get; init; }
    public NavigationFollower? Follower { get; init; }
    public bool TraceRecovery { get; init; }
    public NavigationObservation? Observation { get; init; }
    public INavigationTerrain? Terrain { get; init; }
    public INavigationGrid? Grid { get; init; }
    internal bool IntervalTiming { get; init; }
    internal KnockbackPrediction? TimingBoundary { get; init; }

    internal bool CanEndAt(Vector2 position, double seconds, in MechanicBranchState branch)
    {
        if (TimingBoundary is not { } boundary || seconds + 1e-6 < (boundary.Source.Activation - Now).TotalSeconds)
            return true;
        var landing = EncounterDisplacement.Project(Snapshot, boundary, position, branch);
        return boundary.AllowsLanding(landing) &&
            Terrain!.CanTraverse(new(position.X, 0, position.Y), new(landing.X, 0, landing.Y));
    }

    internal const double StartupSeconds = .1;
    internal static bool IsStopped(Vector2 velocity) => velocity.LengthSquared() < .0001f;
    // Startup consumes predicted travel time while walking input is already held.
    internal Vector2 AdvanceWalking(ref Vector2 velocity, ref double startupRemaining, Vector2 command, double seconds)
    {
        if (!IntervalTiming) return Motion.Advance(ref velocity, command, seconds);
        if (command == Vector2.Zero) startupRemaining = 0;
        else if (IsStopped(velocity)) startupRemaining = StartupSeconds;
        velocity = command;
        var delay = Math.Min(startupRemaining, seconds);
        startupRemaining -= delay;
        return command * (float)(seconds - delay);
    }
    public NavigationMotion Motion => Observation?.Motion ?? NavigationMotion.Instant;
    public NativeDisplacementObservation? Displacement => (Observation?.Displacement ?? Snapshot.Displacement) is { } motion
        && motion.End > Now && (motion.Native || motion.MovementStart > Now) ? motion : null;
    public Vector2 Velocity => Observation?.Velocity ?? Vector2.Zero;
}

