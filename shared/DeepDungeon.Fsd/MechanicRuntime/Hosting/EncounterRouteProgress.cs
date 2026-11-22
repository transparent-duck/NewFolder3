using System.Numerics;

namespace MasterPlayer.MechanicRuntime;

// Shared, native-free timing rule for a physical waypoint follower. A distance
// tolerance may complete a late segment, but cannot start the next one early.
public static class EncounterRouteProgress
{
    public static EncounterRoutePoint[] Continue(EncounterRoute route, Vector2 position, DateTime now, float speed, int startIndex = 0, NavigationObservation? observation = null)
        => Continue(route, position, now, speed, startIndex, observation, route.IntervalTiming);

    internal static EncounterRoutePoint[] Continue(EncounterRoute route, Vector2 position, DateTime now, float speed,
        int startIndex, NavigationObservation? observation, bool intervalTiming)
    {
        var points = route.Points;
        var motion = observation?.Motion ?? NavigationMotion.Instant;
        var velocity = observation?.Velocity ?? Vector2.Zero;
        var elapsed = (now - route.OriginUtc).TotalSeconds;
        var index = Math.Clamp(startIndex, 0, Math.Max(0, points.Length - 1));
        while (index + 1 < points.Length && CanAdvance(position, points[index], points[index + 1], elapsed, .1f, .75f))
            ++index;
        var remaining = new List<EncounterRoutePoint> { new(position, 0) };
        for (var i = index + 1; i < points.Length; ++i)
        {
            if (points[i].GameDriven)
            {
                remaining.Add(points[i] with { Seconds = Math.Max(0, points[i].Seconds - elapsed) });
                velocity = Vector2.Zero;
                continue;
            }
            var from = remaining[^1];
            var wait = points[i].CommandVelocity == Vector2.Zero
                || Vector2.DistanceSquared(points[i - 1].Position, points[i].Position) < 1e-6f;
            var target = wait ? from.Position : points[i].Position;
            var delta = target - from.Position;
            var distance = delta.Length();
            var stopping = i == points.Length - 1 || points[i + 1].Position == points[i].Position
                || points[i + 1].CommandVelocity == Vector2.Zero;
            // The follower has already completed this walking endpoint.
            // Reuse must not invent another tiny approach to the same stop.
            if (stopping && distance <= NavigationFollower.Arrival) distance = 0;
            if (distance > 1e-6f)
            {
                var command = delta / distance * speed;
                var duration = intervalTiming
                    ? distance / speed + (EncounterRouteRequest.IsStopped(velocity) ? EncounterRouteRequest.StartupSeconds : 0)
                    : motion.TravelSeconds(distance, velocity.Length(), speed);
                var arrival = from.Seconds + duration;
                if (intervalTiming) velocity = command;
                else motion.Advance(ref velocity, command, duration);
                remaining.Add(new(target, arrival) { CommandVelocity = command });
            }
            // A rebuilt walking leg may turn on arrival. Only a scheduled hold
            // has a release time; the planner checks the new trajectory below it.
            var until = points[i].Seconds - elapsed;
            if (wait && until > remaining[^1].Seconds + 1e-8)
            {
                remaining.Add(new(remaining[^1].Position, until) { CommandVelocity = Vector2.Zero });
                velocity = Vector2.Zero;
            }
        }
        return remaining.ToArray();
    }

    public static bool CanAdvance(Vector2 position, EncounterRoutePoint from, EncounterRoutePoint to,
        double elapsed, float arrival, float maxDeviation)
    {
        if (elapsed < to.Seconds) return false;
        if (to.GameDriven) return true;
        var delta = to.Position - from.Position;
        var lengthSquared = delta.LengthSquared();
        if (lengthSquared < 1e-6f) return true;
        var along = Vector2.Dot(position - from.Position, delta) / lengthSquared;
        var lateral = Vector2.Distance(position, from.Position + delta * along);
        return Vector2.Distance(position, to.Position) <= arrival || along >= 1 && lateral <= maxDeviation;
    }
}
