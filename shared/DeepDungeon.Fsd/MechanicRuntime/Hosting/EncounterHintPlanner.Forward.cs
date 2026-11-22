using DeepDungeon.MechanicPlanning;
using System.Threading;

namespace MasterPlayer.MechanicRuntime;

public sealed partial class EncounterHintPlanner
{
    private DateTime _planningEpoch;
    private EncounterRoute? _deliveredRoute;
    private ReusePlanner<EncounterForecastState>? _sharedPlanner;
    private AutomaticPlanner<EncounterForecastState>? _automatic;
    private IntervalSolver? _intervalSolver;
    private readonly EncounterPlannerMethod _method;
    private readonly int _searchBudget, _searchRounds, _seed;
    public EncounterHintPlanner(EncounterPlannerMethod method = EncounterPlannerMethod.Auto,
        int searchBudget = 512, int searchRounds = 2, int seed = 1)
    {
        _method = method; _searchBudget = searchBudget; _searchRounds = searchRounds; _seed = seed;
    }
    public string Solver { get; private set; } = "none";
    public string Work { get; private set; } = "none";
    public int SearchEvaluations { get; private set; }
    public int SearchExtensions { get; private set; }
    public string? SearchExit { get; private set; }

    private void ResetForward()
    {
        _sharedPlanner = null; _automatic = null; _intervalSolver = null;
        _deliveredRoute = null;
        _planningEpoch = default; Solver = Work = "none";
    }

    private EncounterRoute PlanAutomatic(EncounterRouteRequest request, CancellationToken token)
    {
        if (_sharedPlanner == null)
        {
            _planningEpoch = request.Now;
            _intervalSolver = new(this);
            _automatic = new(_intervalSolver, new EncounterEventSearch(Math.Max(1, _searchBudget) * Math.Max(1, _searchRounds)),
                _method.ToString().ToLowerInvariant(), CreateOverride());
            _sharedPlanner = new(_automatic);
        }
        var start = (request.Now - _planningEpoch).TotalSeconds;
        var history = EncounterSimulation.NeedsHistory(request.Snapshot);
        Solver = _automatic!.SelectName(history);
        request = request with { IntervalTiming = Solver == "sipp" };
        var problem = new EncounterForwardProblem(request, start, _routeHorizon, token);
        _routeHorizon = _planningHorizon = problem.End - start;
        if ((request.Continuation ?? _deliveredRoute) is { Kind: EncounterRouteKind.Ready } previous
            && previous.EncounterId == request.Snapshot.EncounterId
            && request.Displacement == null
            && !previous.Points.Any(p => p.GameDriven && previous.OriginUtc.AddSeconds(p.Seconds) <= request.Now))
        {
            if (ReferenceEquals(request.Follower?.Plan, previous) && problem.CanKeep(previous))
            {
                Work = "keep";
                return previous;
            }
            var remaining = EncounterRouteProgress.Continue(previous, request.Position, request.Now, request.Speed,
                request.Continuation != null ? request.ContinuationIndex : 0, request.Observation, request.IntervalTiming);
            _sharedPlanner.Retain(ToControls(remaining, problem));
        }
        _intervalSolver!.Recovery = null;
        var plan = _sharedPlanner.Plan(problem);
        Work = _sharedPlanner.LastWork;
        SearchEvaluations = _sharedPlanner.Evaluations;
        SearchExtensions = _sharedPlanner.SearchDiagnostics?.Extensions ?? 0;
        SearchExit = _sharedPlanner.SearchDiagnostics?.Exit;
        // Interval search can exhaust its grid budget on an exposed start.
        // A short step is still executable and can improve the held/retained
        // plan. Score its full tail with the same event and geometry model.
        if (Solver == "sipp" && plan.Assessment is { Failures: > 0 } && request.Snapshot.Hazards.Length > 0)
        {
            var previousPlan = plan;
            var stepEnd = problem.Ends[0];
            foreach (var direction in Directions)
            {
                var walk = Vector2.Normalize(new Vector2(direction.X, direction.Z)) * request.Speed;
                var controls = new[] { walk, Vector2.Zero };
                var ends = new[] { stepEnd, problem.End };
                var score = problem.Evaluate(controls, ends);
                ++SearchEvaluations;
                if (score.CompareTo(plan.Assessment.Value) < 0)
                    plan = new(controls, ends, score);
            }
            if (plan.Velocities != previousPlan.Velocities)
            {
                _sharedPlanner.Retain(plan);
                Work = "escape-step";
            }
        }
        // The chosen candidate already carries its simulation result. Conversion
        // below integrates walking and the same upstream displacement projection.
        var assessment = plan.Assessment!.Value;
        var intervalRoute = Solver == "sipp" && Work == "search" ? _intervalSolver!.LastRoute : null;
        var points = intervalRoute is { Points.Length: > 1, Complete: true, Assessment: { } graphScore }
            && graphScore == assessment && !history && request.Snapshot.Hints is not { StackGoals.Length: > 0 }
            && request.Displacement == null
            ? intervalRoute.Points : ToPoints(plan, request, start);
        if (assessment.GeometryFailures != 0)
            return Route(request, EncounterRouteKind.NoRoute, [], assessment.Failures, (float)assessment.Exposure,
                _totalExpanded, "auto-invalid-geometry");
        var complete = !assessment.RecoveryPending;
        if (Solver == "sipp" && Work == "search" && _intervalSolver!.LastRoute is { Complete: false } prefix)
        {
            complete = false;
            var covered = prefix.Points[^1].Seconds;
            points = points.Where(p => p.Seconds <= covered + 1e-6).ToArray();
        }
        var reason = $"auto-{Solver}-{Work}";
        return Route(request, EncounterRouteKind.Ready, points, assessment.Failures, (float)assessment.Exposure,
            Solver != "sipp" ? SearchExtensions : _totalExpanded, reason) with
        {
            Recovery = _intervalSolver.Recovery,
            IntervalTiming = request.IntervalTiming,
            Complete = complete,
            CompletionSlack = assessment.CompletionSlack,
            Assessment = assessment
        };
    }

    private IResearchPlanner<EncounterForecastState>? CreateOverride() => _method switch
    {
        EncounterPlannerMethod.Beam => new LayerSearchPlanner<EncounterForecastState>(_searchBudget, _searchRounds, _seed, true),
        EncounterPlannerMethod.LayerTree => new LayerSearchPlanner<EncounterForecastState>(_searchBudget, _searchRounds, _seed, false),
        EncounterPlannerMethod.ICem => new ICemPlanner<EncounterForecastState>(_searchBudget, _searchRounds, _seed),
        EncounterPlannerMethod.Shooting => new ShootingPlanner<EncounterForecastState>(_searchBudget, _searchRounds, _seed, SeedControls),
        _ => null,
    };

    private static IEnumerable<Vector2[]> SeedControls(PlanningProblem<EncounterForecastState> problem)
    {
        for (var i = 0; i < 24; ++i)
        {
            var angle = i * MathF.PI / 12;
            var velocity = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * problem.Speed;
            yield return Enumerable.Repeat(velocity, problem.Knots).ToArray();
        }
    }

    private static EncounterRoutePoint[] ToPoints(ControlPlan plan, EncounterRouteRequest request, double time)
    {
        var origin = time;
        var simulation = new EncounterSimulation(request, origin, plan.Ends[^1] - origin, CancellationToken.None);
        var state = simulation.CreateState();
        var points = new List<EncounterRoutePoint> { new(request.Position, 0) };
        var knockbacks = request.Snapshot.Knockbacks;
        var k = 0;
        while (k < knockbacks.Length && knockbacks[k].Source.Activation < request.Now) ++k;
        for (var i = 0; i < plan.Ends.Length; ++i)
        {
            var command = plan.Velocities[i];
            while (state.Time < plan.Ends[i] - 1e-8)
            {
                while (k < knockbacks.Length && origin + (knockbacks[k].Source.Activation - request.Now).TotalSeconds <= state.Time + 1e-8) ++k;
                var at = k < knockbacks.Length
                    ? origin + (knockbacks[k].Source.Activation - request.Now).TotalSeconds : double.PositiveInfinity;
                var motion = state.Forced;
                var end = Math.Min(plan.Ends[i], Math.Min(at, motion?.End ?? double.PositiveInfinity));
                simulation.Advance(state, command, end);
                if (motion.HasValue)
                {
                    if (end >= motion.Value.End - 1e-8 || end >= at - 1e-8)
                        points.Add(new(state.P, end - origin) { CommandVelocity = Vector2.Zero, GameDriven = true });
                }
                else if (end >= at - 1e-8 || i + 1 == plan.Ends.Length || command != plan.Velocities[i + 1])
                    points.Add(new(state.P, end - origin)
                    {
                        CommandVelocity = command,
                        KeepMovingToEnd = command != Vector2.Zero && simulation.RequiresMovement(end - 1e-6, end)
                    });
            }
        }
        return points.ToArray();
    }

    private static ControlPlan ToControls(EncounterRoutePoint[] points, PlanningProblem<EncounterForecastState> problem)
    {
        var controls = new List<Vector2>(); var ends = new List<double>();
        for (var i = 1; i < points.Length; ++i)
        {
            var dt = points[i].Seconds - points[i - 1].Seconds;
            if (dt <= 1e-9) continue;
            var command = points[i].CommandVelocity ?? (points[i].Position - points[i - 1].Position) / (float)dt;
            controls.Add(ControlMath.Project(command, problem.Speed));
            ends.Add(problem.StartTime + points[i].Seconds);
        }
        if (ends.Count == 0 || ends[^1] < problem.End - 1e-8)
        { controls.Add(Vector2.Zero); ends.Add(problem.End); }
        return new(controls.ToArray(), ends.ToArray());
    }

    private sealed class IntervalSolver(EncounterHintPlanner owner) : IResearchPlanner<EncounterForecastState>
    {
        public RecoveryComparison? Recovery { get; set; }
        public EncounterRoute? LastRoute { get; private set; }
        public int Evaluations { get; private set; }
        public double LastCost { get; private set; }
        public ControlPlan Plan(PlanningProblem<EncounterForecastState> problem)
        {
            var q = ((EncounterForwardProblem)problem).Request;
            // The 15ms allowance covers geometry, interval search and recovery.
            // Model construction and retained-candidate evaluation precede it.
            owner._planStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            LastRoute = null;
            if (q.Displacement != null)
                LastRoute = owner.PlanAfterObservedDisplacement(q);
            else if (owner.Prepare(q))
            {
                LastRoute = owner.PlanIntervals(q, CancellationToken.None);
                // A short prefix can still end inside a resolving hazard. Compare
                // its executable tail against escape routes on the original map.
                if (LastRoute is { Complete: false } prefix)
                {
                    var controls = ToControls(prefix.Points, problem);
                    LastRoute = prefix with { Assessment = problem.Evaluate(controls.Velocities, controls.Ends) };
                }
                if (LastRoute == null || LastRoute.Assessment is { Failures: > 0 })
                {
                    var escape = owner.PlanRecoveryIntervals(q, CancellationToken.None);
                    if (q.TraceRecovery)
                    {
                        var hold = problem.Evaluate([Vector2.Zero], [problem.End]);
                        var candidate = escape?.Assessment;
                        Recovery = new(hold.Failures, hold.Exposure, candidate?.Failures,
                            candidate?.Exposure, candidate is { Failures: > 0 } hit ? hit.FirstViolation : null);
                    }
                    if (escape?.Assessment is { } score && (LastRoute?.Assessment is not { } old || score.CompareTo(old) < 0))
                        LastRoute = escape;
                }
            }
            LastRoute ??= Route(q, EncounterRouteKind.NoRoute, [], 0, 0, owner._totalExpanded, "hint-budget-no-route");
            var points = LastRoute.Points;
            if (points.Length < 2)
            {
                var hold = new ControlPlan([Vector2.Zero], [problem.End]);
                var score = problem.Evaluate(hold.Velocities, hold.Ends);
                LastCost = score.Display; Evaluations = 1;
                return hold with { Assessment = score };
            }
            var result = ToControls(points, problem);
            // Static SIPP accumulates the common score during its edge queries.
            // A forced research override with history still needs that model's evolution.
            var assessment = !problem.RequiresHistory && q.Snapshot.Hints is not { StackGoals.Length: > 0 }
                && LastRoute.Assessment is { } scored ? scored
                : problem.Evaluate(result.Velocities, result.Ends);
            LastCost = assessment.Display; Evaluations = 1;
            return result with { Assessment = assessment };
        }
    }
}
