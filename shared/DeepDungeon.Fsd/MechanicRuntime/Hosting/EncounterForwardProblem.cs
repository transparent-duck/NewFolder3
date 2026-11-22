using DeepDungeon.MechanicPlanning;
using BossMod;
using System.Threading;

namespace MasterPlayer.MechanicRuntime;

internal sealed class EncounterForwardProblem : PlanningProblem<EncounterForecastState>, IStateSearchGuidance<EncounterForecastState>
{
    internal readonly EncounterRouteRequest Request;
    private readonly EncounterSimulation _simulation;
    private readonly double _start;
    public override double StartTime => _start;
    public override float Speed => Request.Speed;
    public override double[] Ends => _simulation.Ends;
    public override bool RequiresHistory => _simulation.RequiresHistory;
    internal IEnumerable<double> DecisionTimes => _simulation.DecisionTimes;
    internal bool RequiresMovement(double from, double until) => _simulation.RequiresMovement(from, until);
    internal (WPos Previous, float Step, float Radius)? ChaseAt(EncounterForecastState s, double at) => _simulation.ChaseAt(s, at);
    public ReadOnlySpan<Vector2> ControlTargets => [];
    internal EncounterForwardProblem(EncounterRouteRequest request, double start, double horizon, CancellationToken token)
    { Request = request; _start = start; _simulation = new(request, start, horizon, token); }
    public override PlanningProblem<EncounterForecastState> WithResolution(double integrationStep, double layerStep = 0) => this;
    public override EncounterForecastState CreateState() => _simulation.CreateState();
    public override EncounterForecastState Copy(EncounterForecastState source) => _simulation.Copy(source);
    public override void CopyFrom(EncounterForecastState target, EncounterForecastState source) => _simulation.CopyFrom(target, source);
    public override Vector2 Position(EncounterForecastState state) => state.P;
    public override void Advance(EncounterForecastState state, Vector2 velocity, double until) => _simulation.Advance(state, velocity, until);
    public override PlanScore Score(EncounterForecastState state, bool terminal)
    {
        var score = EncounterScoring.Score(Request, state, terminal, RequiresHistory);
        if (!terminal) return score;
        if (_simulation.IsExposed(state)) score = score with { RecoveryPending = true };
        return !Request.CanEndAt(state.P, state.Time - _start, state.Mechanic)
            ? score with { GeometryFailures = score.GeometryFailures + 1 } : score;
    }
    public override bool CanHold(PlanScore score) => Request.Snapshot.Hints is not { Goals.Length: > 0 }
        && score.GeometryFailures == 0 && score.Failures == 0 && !score.RecoveryPending && score.Soft < 1e-9 && score.StackDistance == 0;
    public override bool CanReuse(ControlPlan plan, PlanScore score) =>
        base.CanReuse(plan, score) && !score.RecoveryPending && score.ResidenceMarginShortfall == 0 && score.StackDistance == 0;

    internal bool CanKeep(EncounterRoute route)
    {
        var q = Request;
        var elapsed = (q.Now - route.OriginUtc).TotalSeconds;
        var escaping = route is { PredictedHits: 0, Exposure: > 0, Assessment.RecoveryPending: true };
        if ((!route.Complete && !escaping) || !route.Timed || route.Points.Length < 2 || elapsed < 0
            || q.Observation is not { } observation || q.Snapshot.Knockbacks.Length != 0
            || q.Snapshot.PathMechanic != null || q.Displacement != null)
            return false;

        var follower = q.Follower!.CopyForPrediction();
        var state = CreateState();
        if (!state.StartsExposed && route.Exposure == 0
            && route.Points[^1].Seconds + 1e-7 < elapsed + End - _start) return false;
        // Run the same waypoint decisions without advancing the live cursor.
        // Stop integration at each switch or arrival so a turn cannot overshoot.
        while (state.Time < End - 1e-8)
        {
            var seconds = state.Time - _start;
            var at = q.Now.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
            var command = follower.Step(observation with { Position = state.P, Velocity = state.Velocity, At = at });
            if (command.Kind == NavigationCommandKind.Replan) return false;
            if (command.Correction is { } corrected && corrected != state.P)
            {
                var from = new Vector3(state.P.X, 0, state.P.Y);
                var to = new Vector3(corrected.X, 0, corrected.Y);
                if (!q.Terrain!.CanTraverse(from, to)) return false;
                var crossing = EncounterHazardRules.Evaluate(q.Snapshot.Hazards, state.P, corrected,
                    seconds, seconds + 1e-7, q.Now, allowExistingExposure: true);
                if (!crossing.GeometryValid || crossing.Hits != 0) return false;
                state.P = corrected;
            }
            var duration = End - state.Time;
            if (follower.Index + 1 < route.Points.Length)
            {
                var untilSwitch = route.Points[follower.Index + 1].Seconds - elapsed - seconds;
                if (untilSwitch > 0) duration = Math.Min(duration, Math.Max(1e-7, untilSwitch));
            }
            var velocity = Vector2.Zero;
            var delta = command.Target - state.P;
            var distance = delta.Length();
            if (command.Kind == NavigationCommandKind.Move && distance > 1e-6f)
            {
                velocity = delta / distance * q.Speed;
                // At a genuine stop the follower accepts the observed position
                // inside Arrival. It can then correct once if the observation permits.
                var travel = follower.WalkingToStop ? Math.Max(0, distance - NavigationFollower.Arrival) : distance;
                var arrival = q.IntervalTiming
                    ? travel / q.Speed + (EncounterRouteRequest.IsStopped(state.Velocity) ? EncounterRouteRequest.StartupSeconds : state.StartupRemaining)
                    : q.Motion.TravelSeconds(travel, state.Velocity.Length(), q.Speed);
                // Advance past coordinate rounding at the arrival boundary.
                duration = Math.Min(duration, Math.Max(1e-5, arrival));
            }
            Advance(state, velocity, state.Time + duration);
            if (state.Failures != 0 || state.GeometryFailures != 0) return false;
        }
        var score = Score(state, true);
        if (!CanReuse(default, score))
            return escaping && state.Travel > 0 && score.RecoveryPending && score.ResidenceMarginShortfall == 0 && score.StackDistance == 0;
        // Preserve the existing preference to settle when standing still already
        // satisfies every active goal, even if an older walking plan is feasible.
        var hold = CreateState();
        Advance(hold, Vector2.Zero, End);
        return !CanHold(Score(hold, true)) || state.Travel == 0;
    }
    public override bool CanExpand(EncounterForecastState state) => state.GeometryFailures == 0 && !state.TimingUnresolved;
    public void SampleTarget(EncounterForecastState s, Random random, bool perturbHistory)
    {
        var center = Request.Snapshot.ArenaCenter; var radius = Request.Snapshot.Arena.Radius;
        s.P = new(center.X + ((float)random.NextDouble() * 2 - 1) * radius,
            center.Z + ((float)random.NextDouble() * 2 - 1) * radius);
        Request.Snapshot.PathMechanic?.Sample(ref s.Mechanic, s.Time, random);
        if (perturbHistory)
            for (var i = 0; i < s.Centers.Length; ++i)
                s.Centers[i] += new Vector2((float)(random.NextDouble() * 6 - 3), (float)(random.NextDouble() * 6 - 3));
    }
    public double Distance(EncounterForecastState a, EncounterForecastState b)
    {
        if (a.Outside != b.Outside) return double.PositiveInfinity;
        if (a.Forced.HasValue != b.Forced.HasValue) return double.PositiveInfinity;
        double d = Vector2.DistanceSquared(a.P, b.P) + Vector2.DistanceSquared(a.Velocity, b.Velocity);
        if (a.Forced is { } first && b.Forced is { } second)
            d += Vector2.DistanceSquared(first.From, second.From) + Vector2.DistanceSquared(first.To, second.To)
                + Math.Pow(first.MovementStart - second.MovementStart, 2) + Math.Pow(first.End - second.End, 2);
        if (Request.Snapshot.PathMechanic is { } mechanic) d = mechanic.AddDistance(d, a.Mechanic, b.Mechanic);
        for (var i = 0; i < a.Centers.Length; ++i) d += Vector2.DistanceSquared(a.Centers[i], b.Centers[i]);
        return d;
    }
    public ulong Label(EncounterForecastState s) => (ulong)(uint)HashCode.Combine(s.P, s.EventIndex, s.Forced, Request.Snapshot.PathMechanic?.BranchDeadline(s.Mechanic) ?? double.PositiveInfinity);
    public double Heuristic(EncounterForecastState s) => EncounterScoring.GoalDistance(Request, s.P);
}
