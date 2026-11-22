using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using BossMod;

namespace MasterPlayer.MechanicRuntime;

/// <summary>Common-safe direct routes, then safe-interval search over timed
/// hint constraints. Frame-scoped delegates are consumed synchronously.</summary>
public sealed partial class EncounterHintPlanner
{
    public const float HintMargin = .6f;
    public const double ImpactLeadSeconds = .15;
    private double _routeHorizon;
    private static readonly (int X, int Z)[] Directions = NavigationGrid.Directions;
    private INavigationGrid? _terrainGrid;
    private INavigationGrid? _topology;
    private ArenaBounds? _arena;
    private WPos _center;
    private int _width, _height, _count;
    private Vector2[] _positions = [];
    private byte[] _edges = [];
    private bool[] _baseWalk = [], _walk = [];
    private float[][] _distances = [];
    private readonly Dictionary<(ShapeDistance Shape, float Margin), float[]> _shapeDistances = new();
    private readonly List<float[]> _fieldPool = new();
    private int _usedFields;
    private bool _walkPrepared;

    public double FirstWaveSeconds { get; private set; }
    public double SecondWaveSeconds { get; private set; }
    public void Reset() { _arena = null; _topology = null; _shapeDistances.Clear(); ResetRoute(); }

    internal void ResetRoute() { ResetIntervals(); ResetForward(); }

    public EncounterRoute Plan(in EncounterRouteRequest request, CancellationToken cancellationToken)
    {
        IntervalExpanded = 0; IntervalBudgetReached = false;
        Solver = Work = "none"; SearchEvaluations = SearchExtensions = 0; SearchExit = null;
        var q = request;
        var snapshot = q.Snapshot;
        if (snapshot.Residence is { } residence)
            q = q with { Speed = Math.Min(q.Speed, residence.MaximumPlanningSpeed) };
        if (EncounterHintEvaluator.Validate(snapshot) is { } error
            || !float.IsFinite(q.Speed) || q.Speed <= 0 || !Finite(q.Position) || !Finite(q.Goal)
            || !float.IsFinite(q.GoalRadius) || q.GoalRadius < 0)
            return Route(q, EncounterRouteKind.NoRoute, [], 0, 0, 0, "invalid-hint-request");
        UpdateWaveTimes(q);
        var grid = q.Grid ?? GetArenaGrid(snapshot, cancellationToken);
        q = q with { Grid = grid, Terrain = q.Terrain ?? grid };
        EnsureGrid(grid, cancellationToken);
        _positions[_count] = q.Position;
        if (q.Displacement == null && (q.Observation is { MovementLocked: true } or { PendingDisplacement: true } or { ForcedMovement: true }
            || snapshot.Hints?.PendingKnockback == true) || snapshot.Hints?.HoldMovement(q.Now) == true)
        {
            var velocity = q.Velocity;
            var stopped = q.Position + q.Motion.Advance(ref velocity, Vector2.Zero, .25);
            EncounterRoutePoint[] hold = [new(q.Position, 0), new(stopped, .25) { CommandVelocity = Vector2.Zero }];
            var check = EncounterCandidateValidator.Evaluate(q, hold, .25);
            return Route(q, EncounterRouteKind.Ready, hold, check.Violations, check.Exposure, 0, "hint-movement-hold") with { AllowCorrection = false };
        }

        // A later displacement with missing timing cannot invalidate earlier walking.
        // Keep the launch geometry but do not assign a time to the displacement.
        KnockbackPrediction? unresolved = null;
        var knockbacks = q.Snapshot.Knockbacks;
        for (var i = 0; i < knockbacks.Length; ++i)
        {
            ref readonly var knockback = ref knockbacks[i];
            if (knockback.Source.Activation < q.Now || knockback.Timing.Available) continue;
            unresolved = knockback;
            q = q with { Snapshot = q.Snapshot with { Knockbacks = knockbacks[..i] }, TimingBoundary = knockback };
            break;
        }
        _routeHorizon = EncounterPlanningHorizon.Calculate(q);
        foreach (var chase in snapshot.ChasingHazards)
            if (chase.Remaining > 0 && chase.NextActivation != DateTime.MaxValue)
                _routeHorizon = Math.Max(_routeHorizon, (chase.NextActivation - q.Now).TotalSeconds
                    + (chase.Remaining - 1) * chase.Interval + ImpactLeadSeconds);
        string? timingIssue = null;
        if (unresolved is { } missing)
        {
            timingIssue = $"knockback-timing-unavailable:{missing.Source.ActionID}:{missing.Timing.TimelineKey ?? "missing-timeline"}";
            if (missing.Source.Activation != DateTime.MaxValue)
                _routeHorizon = (missing.Source.Activation - q.Now).TotalSeconds;
            if (_routeHorizon <= 0)
                return Route(q, EncounterRouteKind.NoRoute, [], 0, 0, 0, timingIssue) with { Complete = false };
        }
        _planningHorizon = _routeHorizon;
        BeginCandidateBudget();
        var result = PlanAutomatic(q, cancellationToken);
        if (result.Kind == EncounterRouteKind.NoRoute && _planBudgetReached)
            result = result with { Reason = "hint-budget-exhausted" };
        if (timingIssue != null)
        {
            result = result.Kind == EncounterRouteKind.Ready
                ? result with { Complete = false, Reason = timingIssue }
                : result with { Kind = EncounterRouteKind.NoRoute, Points = [], Complete = false, Reason = $"{timingIssue};prefix:{result.Reason}" };
        }
        if (ReferenceEquals(result, q.Continuation)) return _deliveredRoute = result;
        IntervalExpanded = Solver != "sipp" ? SearchExtensions : _totalExpanded;
        IntervalBudgetReached = _planBudgetReached;
        return _deliveredRoute = result with { Expanded = IntervalExpanded };
    }

    private static bool Finite(Vector2 p) => float.IsFinite(p.X) && float.IsFinite(p.Y);
    private static EncounterRoute Route(EncounterRouteRequest q, EncounterRouteKind kind, EncounterRoutePoint[] points,
        int violations, float exposure, int expanded, string reason) =>
        new(q.Snapshot.EncounterId, q.Now, kind, points, violations, exposure, expanded, reason);

    private INavigationGrid GetArenaGrid(EncounterPlanningSnapshot s, CancellationToken token)
    {
        if (!ReferenceEquals(_arena, s.Arena) || _center != s.ArenaCenter)
        {
            _terrainGrid = new EncounterTerrainGrid(s, token);
            _arena = s.Arena; _center = s.ArenaCenter;
        }
        return new MechanicNavigationGrid(_terrainGrid!, s);
    }

    private void EnsureGrid(INavigationGrid grid, CancellationToken token)
    {
        if (ReferenceEquals(_topology, grid.Topology)) return;
        _topology = null; // A cancelled fill cannot be reused as a complete topology.
        _width = grid.Width; _height = grid.Height; _count = _width * _height;
        _positions = new Vector2[_count + 1]; _baseWalk = new bool[_count + 1]; _edges = new byte[_count];
        for (var i = 0; i < _count; ++i)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            var p = grid.GridToWorld(i % _width, i / _width);
            _positions[i] = new(p.X, p.Z); _baseWalk[i] = grid.Topology.IsFree(i % _width, i / _width);
            if (!_baseWalk[i]) continue;
            for (var d = 0; d < 8; ++d)
                if (NavigationGrid.Connected(grid.Topology, i % _width, i / _width, Directions[d].X, Directions[d].Z))
                    _edges[i] |= (byte)(1 << d);
        }
        _walk = new bool[_count + 1]; _walkPrepared = false;
        _distances = []; _shapeDistances.Clear(); _fieldPool.Clear(); _usedFields = 0;
        _topology = grid.Topology;
    }

    private static bool TerrainLegal(EncounterRouteRequest q, Vector2 from, Vector2 to) =>
        q.Terrain!.CanTraverse(new Vector3(from.X, 0, from.Y), new(to.X, 0, to.Y));

    private bool Prepare(EncounterRouteRequest q)
    {
        var s = q.Snapshot;
        if (_shapeDistances.Count > 96) _shapeDistances.Clear();
        if (_distances.Length < s.Hazards.Length) Array.Resize(ref _distances, s.Hazards.Length);
        for (var h = 0; h < s.Hazards.Length; ++h)
        {
            if (CandidateBudgetExpired()) return false;
            var zone = s.Hazards[h];
            var key = (zone.Shape, zone.ClearanceMargin);
            if (!_shapeDistances.TryGetValue(key, out var field))
            {
                if (_usedFields == _fieldPool.Count) _fieldPool.Add(new float[_count + 1]);
                field = _fieldPool[_usedFields++];
                for (var i = 0; i < _count; ++i)
                {
                    if ((i & 255) == 0 && CandidateBudgetExpired()) return false;
                    var d = _baseWalk[i] ? EncounterHazardRules.Clearance(zone, _positions[i])
                        : float.PositiveInfinity;
                    field[i] = float.IsFinite(d) ? d : float.NegativeInfinity;
                }
                _shapeDistances[key] = field;
            }
            _distances[h] = field;
            var startDistance = EncounterHazardRules.Clearance(zone, q.Position);
            field[_count] = float.IsFinite(startDistance) ? startDistance : float.NegativeInfinity;
        }
        if (!_walkPrepared)
        for (var i = 0; i < _count; ++i)
        {
            if ((i & 255) == 0 && CandidateBudgetExpired()) return false;
            _walk[i] = _baseWalk[i]
                && q.Grid!.IsFree(i % _width, i / _width);
        }
        _walkPrepared = true;
        _walk[_count] = TerrainLegal(q, q.Position, q.Position);
        return true;
    }

    // Diagnostic timestamps only. No route is ranked by a two-wave model.
    private void UpdateWaveTimes(EncounterRouteRequest q)
    {
        var first = double.PositiveInfinity;
        var second = double.PositiveInfinity;
        void Add(DateTime at)
        {
            if (at == DateTime.MaxValue) return;
            var t = Math.Max(0, (at - q.Now).TotalSeconds);
            if (t < first - .001) { second = first; first = t; }
            else if (t > first + .001 && t < second) second = t;
        }
        foreach (var hazard in q.Snapshot.Hazards) Add(hazard.Activation);
        foreach (var chase in q.Snapshot.ChasingHazards)
        {
            Add(chase.NextActivation);
            if (chase.Remaining > 1) Add(chase.NextActivation.AddSeconds(chase.Interval));
        }
        if (q.Snapshot.PathMechanic is { } mechanic) Add(mechanic.Deadline);
        FirstWaveSeconds = double.IsFinite(first) ? first : 0;
        SecondWaveSeconds = second;
    }

    private static bool HasDynamicGeometry(EncounterPlanningSnapshot snapshot) =>
        snapshot.StaticObstacles.Length != 0 || snapshot.TemporaryObstacles.Length != 0 || snapshot.Hints?.ObstacleMap.Bitmap != null;
}
