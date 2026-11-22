using DeepDungeon.MechanicPlanning;
using DeepDungeon.Fsd.Core;
namespace MasterPlayer.MechanicRuntime;

internal static class EncounterScoring
{
    public static double ExposureCost(double exposure) => exposure * 15000;
    public static double ImpactCost(double shortfall) => shortfall * shortfall * 15000;
    public static double TransitionCost(in EncounterTransitionResult result) =>
        ExposureCost(result.Exposure) + result.ImpactMarginSquared * 15000;

    // Requests retain the original allowed radius. Reserve the movement
    // tolerance through the same rule used by navigation and interaction.
    public static float GoalDistance(in EncounterRouteRequest request, Vector2 point, bool lowerBound = false)
    {
        var distance = MathF.Max(0, Vector2.Distance(point, request.Goal) - new NavigationRange(request.GoalRadius).StoppingRadius);
        return request.Region?.Distance(point, distance, lowerBound) ?? distance;
    }

    internal static float StackDistance(in EncounterRouteRequest request, Vector2 point)
    {
        if (request.Snapshot.Hints is not { StackGoals.Length: > 0 } hints) return 0;
        var value = float.PositiveInfinity;
        foreach (var goal in hints.StackGoals)
            if (goal.Activation == default || goal.Activation >= request.Now)
                value = Math.Min(value, goal.Cost(new(point.X, point.Y)));
        return float.IsFinite(value) ? Math.Max(0, value - NavigationFollower.Arrival) : 0;
    }

    public static float GoalCost(in EncounterRouteRequest request, Vector2 point, bool lowerBound = false)
    {
        var cost = GoalDistance(request, point, lowerBound) / request.Speed;
        var offset = new BossMod.WPos(point.X, point.Y) - request.Snapshot.ArenaCenter;
        if (!request.Snapshot.Arena.Contains(offset))
            cost += (offset - request.Snapshot.Arena.ClampToBounds(offset)).Length() / request.Speed;
        if (request.Snapshot.Hints is { } hints)
            foreach (var goal in hints.Goals)
            {
                var value = goal(new(point.X, point.Y));
                if (float.IsFinite(value)) cost -= value;
            }
        return cost;
    }
    public static PlanScore StaticScore(in EncounterRouteRequest request, Vector2 endpoint,
        double travel, double soft, double margin = 0, int failures = 0, double exposure = 0, int geometryFailures = 0) =>
        Score(request, new() { P = endpoint, Travel = travel, Soft = soft, ImpactMarginShortfall = margin,
            Failures = failures, Exposure = exposure, GeometryFailures = geometryFailures }, true, false);
    public static PlanScore Score(in EncounterRouteRequest request, EncounterForecastState s, bool terminal, bool history)
    {
        var soft = s.Soft;
        if (terminal) soft += GoalCost(request, s.P) * request.Speed * 10;
        var score = new PlanScore(s.Failures, soft, s.Travel, s.ImpactMarginShortfall, s.GeometryFailures, s.Exposure)
        { FirstViolation = s.FirstViolation, HasStackGoal = request.Snapshot.Hints is { StackGoals.Length: > 0 }, StackDistance = s.StackCost };
        return terminal && request.Snapshot.PathMechanic is { } mechanic
            ? mechanic.ScoreTerminal(s.Mechanic, s.Time, score) : score;
    }
}
