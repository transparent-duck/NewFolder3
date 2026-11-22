using BossMod;
using System.Threading;
namespace MasterPlayer.MechanicRuntime;

public sealed record EncounterCandidateAssessment(int Violations, float Exposure, bool GeometryValid,
    bool Complete, bool StaleChase, double FirstViolation, EncounterHazard[] ChaseConstraints,
    EncounterHazard[] ResidenceConstraints, double ResidenceDeadline, bool StartsExposed)
{
    public bool TimingUnresolved { get; init; }
}

/// <summary>Offline diagnostics and legacy candidate callers use the production simulation.</summary>
public static class EncounterCandidateValidator
{
    public static EncounterCandidateAssessment Evaluate(in EncounterRouteRequest request,
        ReadOnlySpan<EncounterRoutePoint> points, double horizon)
    {
        if (points.Length == 0) return new(1, 0, false, false, false, 0, [], [], double.PositiveInfinity, false);
        var q = request with { Position = points[0].Position };
        var simulation = new EncounterSimulation(q, 0, Math.Max(.001, horizon), CancellationToken.None, collectConstraints: true);
        var state = simulation.CreateState();
        for (var i = 1; i < points.Length; ++i)
        {
            var dt = points[i].Seconds - points[i - 1].Seconds;
            if (points[i].GameDriven && Math.Abs(dt) <= 1e-8) continue;
            if (dt <= 0) { ++state.GeometryFailures; continue; }
            var command = points[i].CommandVelocity ?? (points[i].Position - points[i - 1].Position) / (float)dt;
            simulation.Advance(state, command, points[i].Seconds);
        }
        var complete = !state.TimingUnresolved && points.Length >= 2
            && points[0].Seconds == 0 && points[^1].Seconds >= horizon - 1e-6;
        if (!complete && !state.TimingUnresolved) { ++state.Failures; state.FirstViolation = Math.Min(state.FirstViolation, points[^1].Seconds); }
        return new(state.Failures, (float)state.Exposure, state.GeometryFailures == 0, complete,
            state.StaleChase, state.FirstViolation, simulation.ChaseConstraints!.ToArray(), simulation.ResidenceConstraints!.ToArray(),
            q.Snapshot.PathMechanic?.BranchDeadline(state.Mechanic) ?? double.PositiveInfinity, state.StartsExposed)
        { TimingUnresolved = state.TimingUnresolved };
    }

    public static Vector2 PositionAt(ReadOnlySpan<EncounterRoutePoint> points, double seconds)
    {
        if (points.Length == 0) return default;
        for (var i = 1; i < points.Length; ++i)
            if (seconds <= points[i].Seconds)
                return Vector2.Lerp(points[i - 1].Position, points[i].Position,
                    (float)Math.Clamp((seconds - points[i - 1].Seconds) / Math.Max(.000001, points[i].Seconds - points[i - 1].Seconds), 0, 1));
        return points[^1].Position;
    }

}
