using BossMod;
using static BossMod.Components.GenericKnockback;

namespace MasterPlayer.MechanicRuntime;

public readonly record struct KnockbackPrediction(Knockback Source, bool StopAtWall, bool StopAfterWall,
    float HitboxRadius, uint PathDependentImmunityStatus)
{
    public KnockbackMotionTiming Timing { get; init; } = new(double.PositiveInfinity, double.PositiveInfinity);
    internal ShapeDistance? UnsafeLanding { get; init; }
    internal bool AllowsLanding(Vector2 landing) => UnsafeLanding?.Contains(new(landing.X, landing.Y)) != true;
}

/// <summary>Observed native motion or a received effect bound to its actual
/// action timeline. No inferred row or added delay is used here.</summary>
public readonly record struct NativeDisplacementObservation(Vector2 Target, DateTime ObservedAt,
    DateTime MovementStart, double Duration, float Progress, float RemainingDistance,
    bool Native, uint ActionId = 0, uint GlobalSequence = 0, DateTime ReceivedAt = default)
{
    public static NativeDisplacementObservation FromNativeRecord(Vector2 target, DateTime at,
        double duration, float progress, float distance) => new(target, at, at, duration,
            progress, distance * (1 - NativeKnockbackCurve.Progress(progress)), true);
    public DateTime End => MovementStart.AddSeconds(Duration * (1 - Progress));
    internal ForcedDisplacement Transition(Vector2 from, DateTime now, double start)
    {
        var begins = start + Math.Max(0, (MovementStart - now).TotalSeconds);
        return new(from, Target, start, begins,
            Math.Max(begins, start + (End - now).TotalSeconds), RemainingDistance, Progress);
    }
}

/// <summary>Forced input and motion are distinct: hold the launch through effect
/// application, travel on the native curve, then release ordinary walking.</summary>
internal readonly record struct ForcedDisplacement(Vector2 From, Vector2 To, double At,
    double MovementStart, double End, float UnclippedDistance = 0, float InitialProgress = 0)
{
    internal Vector2 Position(double time)
    {
        if (time <= MovementStart) return From;
        if (time >= End || End <= MovementStart) return To;
        var distance = Vector2.Distance(From, To);
        if (distance <= 1e-6f) return From;
        var initial = NativeKnockbackCurve.Progress(InitialProgress);
        var u = InitialProgress + (1 - InitialProgress) * (float)((time - MovementStart) / (End - MovementStart));
        var progress = (NativeKnockbackCurve.Progress(u) - initial) / Math.Max(1e-6f, 1 - initial);
        return Vector2.Lerp(From, To, Math.Min(1, progress * Math.Max(distance, UnclippedDistance) / distance));
    }
}

internal static class NativeKnockbackCurve
{
    // Native curve id3, three cubic segments. Read from the initialized curve
    // object on 7.58 (141834540: arrays at +704/+720/+736/+752, count +768).
    // IEEE bits preserve native coefficients; this is not the 2u-u² fit.
    internal static float Progress(float u)
    {
        var t = Math.Clamp(u, 0, 1) * 3;
        var i = Math.Min((int)t, 2);
        t -= i;
        var a = i == 0 ? 0f : i == 1 ? F(0x3f19999a) : F(0x3f666666);
        var b = i == 0 ? F(0x3f2aaaac) : i == 1 ? F(1055846125) : F(0x3e2aaaaa);
        var c = i == 0 ? 0f : i == 1 ? F(0xbe4cccd2) : F(0xbdccccc3);
        var d = i == 0 ? F(0xbd88888c) : i == 1 ? F(0x3d088896) : F(0x3d088882);
        return ((d * t + c) * t + b) * t + a;
    }
    private static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);
}

internal static class EncounterDisplacement
{
    internal static ForcedDisplacement? Transition(EncounterPlanningSnapshot snapshot,
        KnockbackPrediction prediction, Vector2 from, in MechanicBranchState branch, double at)
    {
        var affected = (prediction.Source.Shape == null || prediction.Source.Shape.Check(
            new(from.X, from.Y), prediction.Source.Origin, prediction.Source.Direction))
            && (prediction.Source.IgnoreImmunes || prediction.PathDependentImmunityStatus == 0
                || snapshot.PathMechanic?.GrantsImmunity(branch, prediction.PathDependentImmunityStatus) != true);
        if (!affected) return new ForcedDisplacement(from, from, at, at, at);
        if (!prediction.Timing.Available) return null;
        var landing = Project(snapshot, prediction, from, branch);
        return new ForcedDisplacement(from, landing, at,
            at + prediction.Timing.ApplicationDelay,
            at + prediction.Timing.ApplicationDelay + prediction.Timing.Duration,
            prediction.Source.Kind is Kind.TowardsOrigin or Kind.DirBackward
                ? Vector2.Distance(from, landing) : prediction.Source.Distance);
    }

    internal static EncounterTransitionResult Assess(EncounterPlanningSnapshot snapshot,
        in ForcedDisplacement motion, DateTime now)
    {
        var hits = 0; var exposure = 0f; var soft = 0d; var margin = 0d;
        var from = motion.From; var at = motion.At;
        if (!double.IsFinite(motion.End)) return new(false, 0, 0, false);
        while (at < motion.End - 1e-8)
        {
            var end = Math.Min(motion.End, at + .05);
            if (at < motion.MovementStart) end = Math.Min(end, motion.MovementStart);
            var to = motion.Position(end);
            var assessment = EncounterHazardRules.Evaluate(snapshot.Hazards, from, to, at, end, now, false);
            if (!assessment.GeometryValid) return assessment;
            hits += assessment.Hits; exposure += assessment.Exposure;
            soft += assessment.ImpactMarginSquared; margin = Math.Max(margin, assessment.ImpactMarginShortfall);
            from = to; at = end;
        }
        return new(true, hits, exposure, false, soft, margin);
    }

    // Geometric projection shared by all motion phases. It never becomes
    // walking input or a position correction.
    internal static Vector2 Project(EncounterPlanningSnapshot snapshot, KnockbackPrediction prediction,
        Vector2 from, in MechanicBranchState branch)
    {
        if (!prediction.Source.IgnoreImmunes && prediction.PathDependentImmunityStatus != 0
            && snapshot.PathMechanic?.GrantsImmunity(branch, prediction.PathDependentImmunityStatus) == true)
            return from;
        var p = ProjectLanding(prediction.Source, new(from.X, from.Y), prediction.HitboxRadius,
            snapshot.Arena, snapshot.ArenaCenter, prediction.StopAtWall, prediction.StopAfterWall);
        return new(p.X, p.Z);
    }

    private const float approxHitBoxRadius = 0.499f;
    private const float maxIntersectionError = 0.5f - approxHitBoxRadius;

    private static WPos ProjectLanding(in Knockback s, WPos from, float hitboxRadius,
        ArenaBounds arena, WPos center, bool stopAtWall, bool stopAfterWall)
    {
        if (s.Shape != null && !s.Shape.Check(from, s.Origin, s.Direction))
        {
            return from; // not affected at this candidate position
        }

        var dir = s.Kind switch
        {
            Kind.AwayFromOrigin => from != s.Origin ? (from - s.Origin).Normalized() : default,
            Kind.TowardsOrigin => from != s.Origin ? (s.Origin - from).Normalized() : default,
            Kind.DirBackward => (s.Direction + 180f.Degrees()).ToDirection(),
            Kind.DirForward => s.Direction.ToDirection(),
            Kind.DirLeft => s.Direction.ToDirection().OrthoL(),
            Kind.DirRight => s.Direction.ToDirection().OrthoR(),
            _ => default
        };
        if (dir == default)
        {
            return from;
        }

        var distance = s.Distance;
        if (s.Kind == Kind.TowardsOrigin)
        {
            distance = Math.Min(distance, (s.Origin - from).Length() - s.MinDistance);
        }

        if (s.Kind == Kind.DirBackward)
        {
            var perpendicularDir = s.Direction.ToDirection().OrthoL();
            var perpendicularDistance = Math.Abs((from - s.Origin).Cross(perpendicularDir) / perpendicularDir.Length());
            distance = Math.Min(distance, perpendicularDistance - s.MinDistance);
        }

        if (distance <= 0f)
        {
            return from;
        }

        if (stopAtWall)
        {
            distance = Math.Min(distance, arena.IntersectRay(from - center, dir) - Math.Max(maxIntersectionError, hitboxRadius - approxHitBoxRadius)); // hitbox radius can be != 0.5 if player is transformed/mounted, but normal arenas with walls should account for walkable arena in their shape already
        }

        if (stopAfterWall)
        {
            distance = Math.Min(distance, arena.IntersectRay(from - center, dir) + maxIntersectionError);
        }

        var walls = s.SafeWalls;
        var lenW = walls.Length;
        if (lenW != 0)
        {
            var distanceToWall = float.MaxValue;
            for (var j = 0; j < lenW; ++j)
            {
                var wall = walls[j];
                var t = Intersect.RaySegment(from, dir, wall.Vertex1, wall.Vertex2);
                if (t < distanceToWall && t <= s.Distance)
                {
                    distanceToWall = t;
                }
            }
            var hitboxradius = hitboxRadius < approxHitBoxRadius ? 0.5f : hitboxRadius; // some NPCs have less than 0.5 radius and cause error while clamping
            distance = distanceToWall < float.MaxValue
                ? Math.Min(distance, distanceToWall - Math.Clamp(hitboxradius - approxHitBoxRadius, maxIntersectionError, hitboxradius - approxHitBoxRadius))
                : Math.Min(distance, arena.IntersectRay(from - center, dir) + maxIntersectionError);
        }

        return from + distance * dir;
    }

}
