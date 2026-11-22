using DeepDungeon.Fsd.Core;
using System.Numerics;

namespace MasterPlayer.MechanicRuntime;

public enum NavigationCommandKind { Move, Wait, Complete, Replan }
public readonly record struct NavigationCommand(NavigationCommandKind Kind, Vector2 Target,
    Vector2? Correction = null);

/// <summary>One native-free follower for spatial and timed plans. No terrain, mechanic or score queries.</summary>
public sealed class NavigationFollower
{
    public const float Arrival = NavigationRange.MovementTolerance;
    public const float CorrectionLimit = .15f;
    private EncounterRoute? _plan;
    private int _index;
    private int _correctedEnd = -1;
    private Vector2? _lastCorrection;
    public int Index => _index;
    public EncounterRoute? Plan => _plan;
    public NavigationFollower CopyForPrediction() => (NavigationFollower)MemberwiseClone();
    internal bool WalkingToStop => _plan is { } route && _index + 1 < route.Points.Length
        && (_index + 2 == route.Points.Length || route.Points[_index + 2].Position == route.Points[_index + 1].Position);
    public void Reset() { _plan = null; _index = 0; _correctedEnd = -1; _lastCorrection = null; }
    public void Adopt(EncounterRoute? plan)
    {
        if (plan == null || plan.Kind == EncounterRouteKind.NoRoute || ReferenceEquals(plan, _plan)) return;
        _plan = plan; _index = 0; _correctedEnd = -1;
    }
    public NavigationCommand Step(in NavigationObservation observation)
    {
        if (observation.MovementLocked || observation.PendingDisplacement || observation.ForcedMovement)
            return new(NavigationCommandKind.Wait, observation.Position);
        if (_plan is not { Points.Length: > 1 } route)
            return new(NavigationCommandKind.Complete, observation.Position);
        var points = route.Points; var elapsed = (observation.At - route.OriginUtc).TotalSeconds;
        // Replacing the plan does not grant another correction at the same stop.
        // A subsequent actual walking displacement begins a new walking episode.
        if (_lastCorrection is { } previous && observation.Velocity.LengthSquared() > .01f
            && Vector2.DistanceSquared(observation.Position, previous) > CorrectionLimit * CorrectionLimit)
            _lastCorrection = null;
        Vector2? correction = null;
        for (; _index + 1 < points.Length; ++_index)
        {
            var a = points[_index]; var b = points[_index + 1];
            if (b.GameDriven)
                return new(elapsed < b.Seconds ? NavigationCommandKind.Wait : NavigationCommandKind.Replan,
                    observation.Position);
            // A mechanic handoff cannot authorize walking to the projected landing.
            if (_index + 2 < points.Length && points[_index + 2].GameDriven && elapsed >= points[_index + 2].Seconds)
                return new(NavigationCommandKind.Replan, observation.Position);
            if (b.CommandVelocity == Vector2.Zero && elapsed < b.Seconds)
                return new(NavigationCommandKind.Wait, observation.Position, correction);
            if (Vector2.DistanceSquared(a.Position, b.Position) < 1e-8f)
            {
                if (elapsed < b.Seconds)
                    return new(NavigationCommandKind.Wait, observation.Position, correction);
                continue;
            }
            var end = _index + 1;
            var stopping = WalkingToStop;
            var distance = Vector2.Distance(observation.Position, b.Position);
            var onTime = !route.Timed || elapsed >= b.Seconds;
            if (stopping && distance <= Arrival)
            {
                // A timed moving leg may reach waypoint tolerance before its
                // deadline. Finish the walking command without correcting or pausing.
                if (!onTime && b.KeepMovingToEnd && b.CommandVelocity is { } velocity)
                    return new(NavigationCommandKind.Move, observation.Position + Vector2.Normalize(velocity) * Arrival);
                if (_correctedEnd != end && _lastCorrection == null && observation.CanCorrect && route.AllowCorrection
                    && distance <= CorrectionLimit)
                { correction = b.Position; _correctedEnd = end; _lastCorrection = b.Position; }
                if (!onTime) return new(NavigationCommandKind.Wait, observation.Position, correction);
                continue;
            }
            if (!stopping && onTime && EncounterRouteProgress.CanAdvance(observation.Position, a, b,
                    double.PositiveInfinity, Arrival, Arrival)) continue;
            // Ordinary input selects a heading, not a fractional walking speed.
            return new(NavigationCommandKind.Move, b.Position, correction);
        }
        return new(NavigationCommandKind.Complete, points[^1].Position, correction);
    }
}
