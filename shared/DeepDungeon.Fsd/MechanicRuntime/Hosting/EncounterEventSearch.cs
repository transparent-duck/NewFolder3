using BossMod;
using DeepDungeon.MechanicPlanning;

namespace MasterPlayer.MechanicRuntime;

// Search at mechanic boundaries. Walking and waiting between boundaries are
// still evaluated by the same simulator as a replayed or retained route.
internal sealed class EncounterEventSearch(int budget) : IResearchPlanner<EncounterForecastState>, ITreeDiagnostics
{
    private const int BeamWidth = 8;
    private const int DestinationsPerBranch = 12;
    private const int Directions = 16;
    private static readonly int[] DirectionOrder = [0, 4, 8, 12, 2, 6, 10, 14, 1, 3, 5, 7, 9, 11, 13, 15];
    private sealed record Branch(EncounterForecastState State, Branch? Parent, Vector2 Command,
        double Depart, double Arrive, PlanScore Score, double Priority);
    private readonly record struct Destination(Vector2 Position, double Travel, double Depart, double Priority);

    private ShapeDistance? _region;
    private INavigationGrid? _topology;
    private Vector2[] _resetPoints = [], _contactPoints = [];

    public int Evaluations { get; private set; }
    public double LastCost { get; private set; }
    public TreeDiagnostics? SearchDiagnostics { get; private set; }

    public ControlPlan Plan(PlanningProblem<EncounterForecastState> problem)
    {
        var model = (EncounterForwardProblem)problem;
        var q = model.Request;
        var residence = q.Snapshot.Residence;
        var diagnostics = SearchDiagnostics = new TreeDiagnostics();
        Evaluations = 0;
        PreparePoints(q.Grid!, residence);

        var start = problem.CreateState();
        var best = new ControlPlan([Vector2.Zero], [problem.End], problem.Evaluate([Vector2.Zero], [problem.End]));
        ++Evaluations;
        var boundaries = Boundaries(model);
        var layer = new List<Branch> { new(start, null, Vector2.Zero, start.Time, start.Time, best.Assessment!.Value, 0) };
        for (var i = 0; i < boundaries.Length; ++i)
        {
            var next = new List<Branch>();
            var remaining = budget - diagnostics.Extensions;
            var perBranch = Math.Max(1, remaining / (boundaries.Length - i) / layer.Count);
            if (layer.Count > 1) perBranch = Math.Min(DestinationsPerBranch, perBranch);
            var at = boundaries[i];
            foreach (var parent in layer)
            {
                var available = Math.Min(perBranch, budget - diagnostics.Extensions);
                if (available == 0) break;
                foreach (var target in Destinations(model, parent.State, at, residence).Take(available))
                {
                    ++diagnostics.Extensions;
                    var state = problem.Copy(parent.State);
                    var delta = target.Position - state.P;
                    var command = delta.LengthSquared() < 1e-8f ? Vector2.Zero : Vector2.Normalize(delta) * problem.Speed;
                    var depart = target.Depart;
                    var arrival = Math.Min(at, depart + target.Travel);
                    if (depart > state.Time) problem.Advance(state, Vector2.Zero, depart);
                    if (arrival > state.Time) problem.Advance(state, command, arrival);
                    if (at > state.Time) problem.Advance(state, Vector2.Zero, at);
                    if (!problem.CanExpand(state)) continue;
                    var score = problem.Score(state, i + 1 == boundaries.Length);
                    var priority = Priority(model, state, residence);
                    var branch = new Branch(state, parent, command, depart, arrival, score, priority);
                    if (i + 1 == boundaries.Length)
                    {
                        if (score.CompareTo(best.Assessment!.Value) < 0) best = Controls(branch);
                    }
                    else if (CanResetBeforeDeadline(q, state, residence))
                        next.Add(branch);
                }
            }
            if (i + 1 == boundaries.Length) break;
            if (next.Count == 0) { diagnostics.Exit = diagnostics.Extensions == budget ? "budget" : "event-exhausted"; break; }
            layer = SelectLayer(next);
        }
        Evaluations += diagnostics.Extensions;
        LastCost = best.Assessment!.Value.Display;
        if (diagnostics.Extensions < budget && diagnostics.Exit == "budget")
            diagnostics.Exit = best.Assessment.Value is { GeometryFailures: 0, Failures: 0, RecoveryPending: false, StackDistance: 0 }
                ? "event-complete" : "event-no-feasible-route";
        return best;
    }

    private static double[] Boundaries(EncounterForwardProblem problem)
    {
        var result = new List<double>();
        var last = problem.StartTime;
        var continuous = problem.Request.Snapshot.Hints is { KeepMoving: true } or { Mode: AIHints.SpecialMode.Freezing };
        var maxLeg = continuous ? 1.2 : 4;
        foreach (var at in problem.DecisionTimes.Append(problem.End).Where(t => t > problem.StartTime && t <= problem.End).Order())
        {
            if (at - last < 1e-7) continue;
            while (at - last > maxLeg * 1.5) result.Add(last += maxLeg);
            result.Add(last = at);
        }
        return result.ToArray();
    }

    private IEnumerable<Destination> Destinations(EncounterForwardProblem model, EncounterForecastState state,
        double at, EncounterResidenceHazard? residence)
    {
        var q = model.Request;
        var duration = at - state.Time;
        var range = q.Speed * (float)duration;
        var continuous = model.RequiresMovement(state.Time, at);
        var chase = model.ChaseAt(state, at);
        var contact = residence?.ResolutionAt is { } resolution
            && Math.Abs(at - model.StartTime - (resolution - q.Now).TotalSeconds) < 1e-6;
        var contactSlack = residence?.TargetContactSlack ?? 0;
        var afterContact = residence != null && state.Mechanic.Residence.Immune
            && state.Mechanic.WindAt <= state.Time
            && state.Time < state.Mechanic.WindAt + contactSlack;
        var targets = new List<Destination>();
        void Add(Vector2 point, double bias, double depart, bool beforeContact = false, bool fullLeg = false)
        {
            if (continuous && !fullLeg) return;
            var distance = Vector2.Distance(state.P, point);
            if ((!fullLeg && distance > range + 1e-6) || !q.Terrain!.CanStand(new(point.X, 0, point.Y))) return;
            var travel = fullLeg ? duration : q.Motion.TravelSeconds(distance, depart > state.Time || beforeContact ? 0 : state.Velocity.Length(), q.Speed);
            if (beforeContact)
                depart = Math.Max(state.Time, at - travel - residence!.ImmunityGrantSeconds - contactSlack);
            if (travel + depart > at + 1e-6) return;
            var rank = bias - distance * .015;
            if (chase is { } c)
            {
                var landing = EncounterChaseRules.Landing(c.Previous, new(point.X, point.Y), c.Step);
                var clearance = Vector2.Distance(point, new(landing.X, landing.Z)) - c.Radius;
                rank += Math.Clamp(clearance, -6, 2) * 2;
            }
            targets.Add(new(point, travel, depart, Math.Round(rank, 4)));
        }

        void AddHeading(Vector2 direction, double bias)
        {
            if (!continuous)
            {
                Add(state.P + direction * (range * .99f), bias, state.Time);
                Add(state.P + direction * (range * .55f), bias - .2, state.Time);
                return;
            }
            var velocity = state.Velocity;
            var startup = state.StartupRemaining;
            var point = state.P + q.AdvanceWalking(ref velocity, ref startup, direction * q.Speed, duration);
            Add(point, bias, state.Time, fullLeg: true);
        }

        Add(state.P, !contact && residence != null && !state.Mechanic.Residence.Active ? 3.1 : 0, state.Time);
        Add(q.Goal, 0, state.Time);
        if (residence != null)
        {
            foreach (var p in contact ? _contactPoints : _resetPoints)
            {
                if (afterContact) Add(p, 3.1, state.Mechanic.WindAt + contactSlack);
                Add(p, contact ? 8 : 3, state.Time, contact);
            }
        }
        foreach (var d in DirectionOrder)
        {
            var angle = d * MathF.Tau / Directions;
            AddHeading(new(MathF.Cos(angle), MathF.Sin(angle)), -.1);
        }
        if (chase is { } chaser)
        {
            var outward = state.P - new Vector2(chaser.Previous.X, chaser.Previous.Z);
            if (outward.LengthSquared() > 1e-6f)
                AddHeading(Vector2.Normalize(outward), .1);
        }

        // Sample diverse endpoints before spending the simulation budget on
        // neighbouring variants of the same walk.
        var selected = new List<Destination>();
        var separation = Math.Clamp(range * .22f, .5f, 1.35f);
        foreach (var target in targets.OrderByDescending(t => t.Priority))
        {
            if (selected.Any(t => Math.Abs(t.Depart - target.Depart) < .15
                && Vector2.DistanceSquared(t.Position, target.Position) < separation * separation)) continue;
            selected.Add(target);
            yield return target;
        }
    }

    private void PreparePoints(INavigationGrid grid, EncounterResidenceHazard? residence)
    {
        var shape = residence?.UnsafeRegion;
        if (ReferenceEquals(_region, shape) && ReferenceEquals(_topology, grid.Topology)) return;
        _region = shape;
        _topology = grid.Topology;
        _resetPoints = [];
        _contactPoints = [];
        if (shape == null) return;

        var reset = new List<Vector2>();
        var contact = new List<Vector2>();
        var cellSize = Vector3.Distance(grid.GridToWorld(0, 0), grid.GridToWorld(1, 0));
        var spacing = Math.Max(1, (int)MathF.Round(1 / cellSize));
        for (var z = 0; z < grid.Height; z += spacing)
            for (var x = 0; x < grid.Width; x += spacing)
            {
                if (!grid.IsFree(x, z)) continue;
                var p = grid.GridToWorld(x, z);
                var distance = shape.Distance(new(p.X, p.Z));
                if (distance >= EncounterHintPlanner.HintMargin + NavigationFollower.Arrival) reset.Add(new(p.X, p.Z));
                if (distance <= -EncounterHintPlanner.HintMargin - NavigationFollower.Arrival && distance >= -2)
                    contact.Add(new(p.X, p.Z));
            }
        _resetPoints = reset.ToArray();
        _contactPoints = contact.ToArray();
    }

    private static bool CanResetBeforeDeadline(in EncounterRouteRequest request,
        EncounterForecastState state, EncounterResidenceHazard? rule)
    {
        ref readonly var residence = ref state.Mechanic.Residence;
        if (rule == null || !residence.Active || residence.Reported) return true;
        var reset = Math.Max(0, rule.ResetDwellSeconds - residence.SafeContact);
        // A straight line to the nearest boundary is optimistic. An upcoming
        // forced displacement can beat that walking bound, so use dwell alone.
        if (request.Snapshot.Knockbacks.Length == 0 && request.Displacement == null && rule.ResetBoundary is { } boundary)
            reset += boundary.DistanceLowerBound(state.P) / request.Speed;
        return state.Time + reset < residence.Deadline;
    }

    private static double Priority(EncounterForwardProblem model, EncounterForecastState s, EncounterResidenceHazard? rule)
    {
        var value = -s.ImpactMarginShortfall * 10 - model.Heuristic(s) * .04;
        if (rule != null) value -= rule.MarginShortfall(s.Mechanic.Residence) * 4;
        if (rule != null && s.Mechanic.Residence is { Active: true } r)
            value -= 2 + Math.Max(0, 2 - (r.Deadline - s.Time - rule.SafetyMarginSeconds)) * 2;
        if (rule != null && double.IsFinite(s.Mechanic.WindAt)) value += s.Mechanic.WindSlack;
        return value;
    }

    private static List<Branch> SelectLayer(List<Branch> candidates)
    {
        var selected = new List<Branch>();
        foreach (var candidate in candidates.OrderBy(b => b.Score.Failures)
                     .ThenByDescending(b => b.Score.Failures > 0 ? b.Score.FirstViolation : 0)
                     .ThenByDescending(b => b.Priority).ThenBy(b => b.Score))
        {
            var state = candidate.State;
            // Diversity, not dominance: nearby states on different chase or
            // residence histories must remain eligible as separate branches.
            if (selected.Any(b => SameNeighbourhood(b.State, state))) continue;
            selected.Add(candidate);
            if (selected.Count == BeamWidth) break;
        }
        return selected;
    }

    private static bool SameNeighbourhood(EncounterForecastState first, EncounterForecastState second)
    {
        ref readonly var a = ref first.Mechanic.Residence;
        ref readonly var b = ref second.Mechanic.Residence;
        if (Vector2.DistanceSquared(first.P, second.P) >= 1.44f
            || Vector2.DistanceSquared(first.Velocity, second.Velocity) > 1
            || first.Forced != second.Forced || first.Failures != second.Failures
            || first.EventIndex != second.EventIndex || first.Stopped != second.Stopped
            || a.Immune != b.Immune || a.Active != b.Active || a.Reported != b.Reported
            || Math.Abs(a.Deadline - b.Deadline) > .12 || Math.Abs(a.SafeContact - b.SafeContact) > .12
            || Math.Abs(a.ImmunityContact - b.ImmunityContact) > .12
            || !first.Unsafe.AsSpan().SequenceEqual(second.Unsafe))
            return false;
        for (var i = 0; i < first.Centers.Length; ++i)
            if (Vector2.DistanceSquared(first.Centers[i], second.Centers[i]) >= 1) return false;
        return true;
    }

    private static ControlPlan Controls(Branch terminal)
    {
        var chain = new Stack<Branch>();
        for (var current = terminal; current.Parent != null; current = current.Parent) chain.Push(current);
        var controls = new List<Vector2>();
        var ends = new List<double>();
        var at = chain.Peek().Parent!.State.Time;
        void Add(Vector2 command, double until)
        {
            if (until <= at) return;
            controls.Add(command);
            ends.Add(at = until);
        }
        foreach (var leg in chain)
        {
            Add(Vector2.Zero, leg.Depart);
            Add(leg.Command, leg.Arrive);
            Add(Vector2.Zero, leg.State.Time);
        }
        return new(controls.ToArray(), ends.ToArray(), terminal.Score);
    }
}
