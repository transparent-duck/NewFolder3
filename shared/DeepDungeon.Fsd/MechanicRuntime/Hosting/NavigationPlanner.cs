using System.Numerics;
using System.Threading;
using BossMod;
using DeepDungeon.Fsd.Core;

namespace MasterPlayer.MechanicRuntime;

/// <summary>A destination or a positioning preference scoped to one encounter.</summary>
public readonly record struct NavigationGoal(Vector3 Position, float Radius, ulong EncounterId = 0)
{
    public bool Replaces(NavigationGoal previous) => EncounterId != previous.EncounterId ||
        EncounterId == 0 && (Radius != previous.Radius || Vector3.DistanceSquared(Position, previous.Position) > .01f);
}

public readonly record struct NavigationRequest(DateTime Now, Vector2 Position, Vector2 Goal,
    float GoalRadius, float Speed, INavigationGrid? Map, EncounterPlanningSnapshot? Encounter = null)
{
    public bool TraceRecovery { get; init; }
    public NavigationRegionGoal? Region { get; init; }
    public float PlayerHeight { get; init; }
    public ulong PlayerId { get; init; }
    public NavigationObservation? Observation { get; init; }
    public EncounterRoute? Continuation { get; init; }
    public int ContinuationIndex { get; init; }
    public NavigationFollower? Follower { get; init; }
    public NavigationPath? PreparedPath { get; init; }
    public ulong AttackApproachTarget { get; init; }
}

/// <summary>One planning entry: spatial A*, safe intervals, or history-dependent search.</summary>
public sealed class NavigationPlanner
{
    private readonly EncounterHintPlanner _timed = new();
    private readonly GridAStar.Search _spatial = new();
    private readonly List<Vector3> _path = new(), _rawPath = new();
    private EncounterTerrainGrid? _arenaGrid;
    private LocalNavigationGrid? _localGrid;
    private ArenaBounds? _arena;
    private WPos _center;
    private INavigationGrid? _lastSpatialGrid;
    private Vector2 _lastGoal;
    private float _lastGoalRadius;
    private NavigationPath? _lastPreparedPath;
    private INavigationGrid? _reentryGrid;
    private EncounterRoute? _reentryRoute;
    private bool UsesSpatialRoute => Solver is "astar" or "direct";
    public INavigationGrid? CurrentTerrain { get; private set; }
    public string Solver { get; private set; } = "none";
    public string Work { get; private set; } = "none";
    public int SearchEvaluations => UsesSpatialRoute ? 1 : _timed.SearchEvaluations;
    public int SearchExtensions => UsesSpatialRoute ? 0 : _timed.SearchExtensions;
    public string? SearchExit => UsesSpatialRoute ? null : _timed.SearchExit;
    public double FirstWaveSeconds => _timed.FirstWaveSeconds;
    public double SecondWaveSeconds => _timed.SecondWaveSeconds;
    public int IntervalExpanded => UsesSpatialRoute ? 0 : _timed.IntervalExpanded;
    public bool IntervalBudgetReached => !UsesSpatialRoute && _timed.IntervalBudgetReached;
    public int CandidateCount => UsesSpatialRoute ? 1 : _timed.CandidateCount;
    public IReadOnlyList<Vector3> RawPath => _rawPath;

    public void Reset()
    {
        _timed.Reset();
        _localGrid = null; _arena = null; _arenaGrid = null;
        ClearRouteState();
    }

    public void ResetRoute()
    {
        _timed.ResetRoute();
        ClearRouteState();
    }

    private void ClearRouteState()
    {
        _path.Clear(); _rawPath.Clear();
        _lastSpatialGrid = null; _lastPreparedPath = null;
        _reentryGrid = null; _reentryRoute = null;
        CurrentTerrain = null; Solver = Work = "none";
    }

    public EncounterRoute Plan(in NavigationRequest request, CancellationToken token)
    {
        var q = request;
        var encounter = q.Encounter;
        var identity = encounter?.EncounterId ?? 0;
        EncounterRoute Empty(string reason) => new(identity, q.Now, EncounterRouteKind.NoRoute, [], 0, 0, 0, reason);
        if (!float.IsFinite(q.Speed) || q.Speed <= 0 || !float.IsFinite(q.GoalRadius) || q.GoalRadius < 0
            || !float.IsFinite(q.Position.X) || !float.IsFinite(q.Position.Y)
            || !float.IsFinite(q.Goal.X) || !float.IsFinite(q.Goal.Y)) return Empty("invalid-navigation-request");
        var displacementKnown = encounter != null && (q.Observation?.Displacement ?? encounter.Displacement) != null;
        if (!displacementKnown && (q.Observation?.MovementLocked == true || q.Observation?.PendingDisplacement == true || q.Observation?.ForcedMovement == true
            || encounter?.Hints?.PendingKnockback == true) || encounter?.Hints?.HoldMovement(q.Now) == true)
        {
            Solver = "hold"; Work = q.Observation?.MovementLocked == true || encounter?.Hints?.HoldMovement(q.Now) == true
                ? "movement-locked" : "displacement-pending";
            return new(identity, q.Now, EncounterRouteKind.Ready,
                [new(q.Position, 0), new(q.Position, .1) { CommandVelocity = Vector2.Zero }], 0, 0, 0, Work)
                { AllowCorrection = false };
        }
        INavigationGrid? grid = q.Map;
        if (encounter is { UsesDutyTerrain: true })
        {
            // Search a bounded window of the actual duty grid. The cell centers
            // and segment tests include the same walls and doors as ordinary A*.
            if (grid != null)
            {
                if (_localGrid == null || !_localGrid.Matches(grid, encounter.ArenaCenter, encounter.Arena.Radius))
                    _localGrid = new(grid, encounter.ArenaCenter, encounter.Arena.Radius);
                grid = _localGrid;
            }
        }
        else if (encounter != null)
        {
            if (!ReferenceEquals(_arena, encounter.Arena) || _center != encounter.ArenaCenter)
            {
                _arenaGrid = new(encounter, token);
                _arena = encounter.Arena; _center = encounter.ArenaCenter;
            }
            grid = new MechanicNavigationGrid(_arenaGrid!, encounter);
        }
        CurrentTerrain = encounter is { UsesDutyTerrain: true } ? q.Map : grid;
        var temporal = encounter?.RequiresTimedPlanning == true;
        var player = new Vector3(q.Position.X, q.PlayerHeight, q.Position.Y);
        bool refreshRegion = q.Region?.ObservePlayer(CurrentTerrain, player, q.Now) == true;
        if (!temporal && q.Region is { Holding: true })
        {
            _rawPath.Clear();
            Solver = "astar"; Work = "region-in-range";
            return new(identity, q.Now, EncounterRouteKind.Ready, [new(q.Position, 0)], 0, 0, 0, Work) { Timed = false };
        }
        if (grid == null) { Solver = "none"; Work = "terrain-unavailable"; return Empty(Work); }
        var start = new Vector3(q.Position.X, 0, q.Position.Y);
        var (startX, startY) = grid.WorldToGrid(start);
        if (!grid.IsFree(startX, startY) || !grid.CanStand(start))
        {
            // The observed position can lie outside the rasterized walkable
            // region. Return directly to its nearest cell before choosing a solver.
            Solver = "direct"; Work = "terrain-reentry";
            if (_reentryRoute != null && _reentryRoute.EncounterId == identity
                && ReferenceEquals(_reentryGrid, grid.Topology))
            {
                var end = _reentryRoute.Points[^1].Position;
                var (x, y) = grid.WorldToGrid(new(end.X, 0, end.Y));
                if (grid.IsFree(x, y)) return _reentryRoute;
            }
            _reentryGrid = grid.Topology;
            _reentryRoute = null;
            _rawPath.Clear();
            if (NavigationGrid.NearestFreeXZ(grid, start, token) is not { } nearest)
                return Empty("terrain-no-reentry-point");
            var destination = new Vector2(nearest.X, nearest.Z);
            _rawPath.Add(start); _rawPath.Add(nearest);
            return _reentryRoute = new(identity, q.Now, EncounterRouteKind.Ready,
                [new(q.Position, 0), new(destination, Vector2.Distance(q.Position, destination) / q.Speed)],
                0, 0, 0, Work) { Timed = false, Complete = false, AllowCorrection = false };
        }
        if (_reentryRoute != null)
        {
            _reentryRoute = null; _reentryGrid = null;
            _timed.ResetRoute();
            q = q with { Continuation = null, ContinuationIndex = 0 };
        }
        if (q.Region is { } region)
        {
            region.Resolve(CurrentTerrain ?? grid, player, refreshRegion, q.Now, token);
            var approach = region.Path;
            if (!region.Holding && approach == null && !temporal)
            {
                Solver = "astar";
                Work = region.Unreachable ? "region-unreachable" : "region-search";
                return Empty(Work);
            }
            // With a hazard and no approach path yet the timed solver can still escape.
            var point = approach?.Points[^1];
            q = q with { Goal = point is { } p ? new(p.X, p.Z) : q.Position,
                GoalRadius = NavigationFollower.Arrival, PreparedPath = approach };
        }
        if (temporal)
        {
            _rawPath.Clear();
            var timed = new EncounterRouteRequest(encounter!, q.Now, q.PlayerId,
                q.Position, q.Goal, q.GoalRadius, q.Speed, default)
            { Grid = grid, Terrain = grid, TraceRecovery = q.TraceRecovery, Observation = q.Observation, Region = q.Region,
                Continuation = q.Continuation, ContinuationIndex = q.ContinuationIndex, Follower = q.Follower };
            var result = _timed.Plan(timed, token);
            Solver = _timed.Solver; Work = _timed.Work;
            return result;
        }
        Solver = "astar"; Work = "search";
        var goal = new Vector3(q.Goal.X, 0, q.Goal.Y);
        var inside = encounter == null || encounter.Arena.Contains(new WPos(q.Position.X, q.Position.Y) - encounter.ArenaCenter);
        var range = new NavigationRange(q.GoalRadius);
        if (inside && range.HasArrived(start, goal))
            return new(identity, q.Now, EncounterRouteKind.Ready, [new(q.Position, 0), new(q.Position, .1)], 0, 0, 0, "astar-arrived") { Timed = false };
        if (ReferenceEquals(_lastSpatialGrid, grid.Topology) && _lastGoal == q.Goal && _lastGoalRadius == q.GoalRadius
            && ReferenceEquals(_lastPreparedPath, q.PreparedPath)
            && q.Continuation is { Reason: "astar" } previous
            && q.ContinuationIndex + 1 < previous.Points.Length)
        {
            var next = previous.Points[q.ContinuationIndex + 1].Position;
            if (grid.CanTraverse(start, new Vector3(next.X, 0, next.Y)))
            { Work = "reuse"; return previous; }
        }
        _lastSpatialGrid = grid.Topology; _lastGoal = q.Goal; _lastGoalRadius = q.GoalRadius; _lastPreparedPath = q.PreparedPath;
        if (TryUsePreparedPath(q.PreparedPath, grid, start, goal, range)) Work = "prepared-path";
        else if (q.Region != null)
        {
            // The local topology or join changed. Reopen the region search so
            // a point-goal nearest-free substitution cannot become a valid stand.
            q.Region.Invalidate();
            return Empty("region-path-disconnected");
        }
        else if (q.AttackApproachTarget != 0)
        {
            Work = "attack-approach";
            _path.Clear();
            _rawPath.Clear();
            _spatial.Begin(grid, start, point => range.HasArrived(point, goal),
                point => Math.Max(0, range.Distance(point, goal) - range.StoppingRadius));
            _spatial.Step(int.MaxValue, double.PositiveInfinity, token);
            token.ThrowIfCancellationRequested();
            if (_spatial.Status != GridSearchStatus.Found) return Empty("attack-approach-unreachable");
            _spatial.CopyPath(_path, _rawPath);
        }
        else if (!GridAStar.TryFindPath(grid, start, goal, _path, _rawPath, _spatial, token)) return Empty("terrain-unreachable");
        var points = new List<EncounterRoutePoint> { new(q.Position, 0) };
        double seconds = 0;
        foreach (var point in _path)
        {
            var p2 = new Vector2(point.X, point.Z);
            var distance = Vector2.Distance(points[^1].Position, p2);
            if (distance <= 1e-5f) continue;
            seconds += distance / q.Speed;
            points.Add(new(p2, seconds));
        }
        var endpoint = points[^1].Position;
        var complete = range.HasArrived(new(endpoint.X, 0, endpoint.Y), goal);
        if (points.Count == 1) return Empty("terrain-unreachable");
        bool approachOnly = q.AttackApproachTarget != 0;
        return new(identity, q.Now, EncounterRouteKind.Ready, points.ToArray(), 0, 0, 0, approachOnly ? "attack-approach" : "astar")
        { Timed = false, Complete = complete, AllowCorrection = !approachOnly };
    }

    private bool TryUsePreparedPath(NavigationPath? route, INavigationGrid grid,
        Vector3 start, Vector3 goal, NavigationRange range)
    {
        if (route is not { Points.Length: > 0 }
            || !range.HasArrived(route.Points[^1], goal)) return false;
        // The player can have moved while the incremental search was running.
        // Join the furthest visible point; keep the rest of the searched route.
        int join = route.Points.Length - 1;
        while (join >= 0 && !grid.CanTraverse(start, route.Points[join])) --join;
        if (join < 0) return false;
        var previous = start;
        for (int i = join; i < route.Points.Length; ++i)
        {
            if (!grid.CanTraverse(previous, route.Points[i])) return false;
            previous = route.Points[i];
        }
        _path.Clear(); _rawPath.Clear();
        for (int i = join; i < route.Points.Length; ++i) _path.Add(route.Points[i]);
        _rawPath.AddRange(_path);
        return true;
    }
    /// <summary>Check the live first command of an asynchronously delivered route.</summary>
    public static bool CanStart(EncounterRoute route, in NavigationObservation observation,
        in NavigationCommand command, EncounterPlanningSnapshot? encounter, INavigationGrid? terrain,
        float speed, double untilReplan, EncounterPlanningSnapshot? planned = null)
    {
        if (command.Kind != NavigationCommandKind.Move) return true;
        var from = command.Correction ?? observation.Position;
        var to = command.Target;
        var floorFrom = new Vector3(from.X, 0, from.Y);
        var floorTo = new Vector3(to.X, 0, to.Y);
        // Terrain-reentry exists specifically for a start outside the bitmap.
        if (route.Reason == "terrain-reentry") return true;
        if (encounter != null && terrain is MechanicNavigationGrid)
            terrain = new MechanicNavigationGrid(terrain.Topology, encounter);
        if (terrain?.CanTraverse(floorFrom, floorTo) == false) return false;
        if (encounter == null) return true;

        var delta = to - from;
        var distance = delta.Length();
        if (distance <= 1e-6f || speed <= 0) return true;
        var duration = Math.Min(untilReplan, distance / speed);
        if (duration <= 0) return true;
        var end = from + delta * (float)(duration * speed / distance);
        if (!EncounterGeometry.GeometryLegal(encounter, from, end)) return false;
        foreach (var hazard in encounter.Hazards.AsSpan())
        {
            var actual = CheckHazard(hazard, from, end, 0, duration, observation.At);
            if (!actual.GeometryValid) return false;
            if (actual.Hits == 0) continue;
            if (route.PredictedHits == 0 || planned == null ||
                !WasPlanned(route, planned.Hazards, hazard, observation.At, duration)) return false;
        }
        return true;
    }

    private static EncounterTransitionResult CheckHazard(EncounterHazard hazard, Vector2 from, Vector2 to,
        double begin, double end, DateTime now) =>
        EncounterHazardRules.Evaluate(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref hazard, 1),
            from, to, begin, end, now, allowExistingExposure: true);

    private static bool WasPlanned(EncounterRoute route, EncounterHazard[] planned,
        EncounterHazard live, DateTime now, double duration)
    {
        var elapsed = (now - route.OriginUtc).TotalSeconds;
        foreach (var hazard in planned)
        {
            if (hazard.Kind != live.Kind || hazard.SourceId != live.SourceId || hazard.Activation != live.Activation
                || !Equals(hazard.Shape, live.Shape) && (hazard.Geometry == null || hazard.Geometry != live.Geometry))
                continue;
            var points = route.Points;
            for (var i = 1; i < points.Length; ++i)
            {
                var a = points[i - 1];
                var b = points[i];
                if (a.Seconds >= elapsed + duration) break;
                var start = Math.Max(elapsed, a.Seconds);
                var end = Math.Min(elapsed + duration, b.Seconds);
                if (start >= end || b.GameDriven) continue;
                var from = Vector2.Lerp(a.Position, b.Position, (float)((start - a.Seconds) / (b.Seconds - a.Seconds)));
                var to = Vector2.Lerp(a.Position, b.Position, (float)((end - a.Seconds) / (b.Seconds - a.Seconds)));
                if (CheckHazard(hazard, from, to, start - elapsed, end - elapsed, now) is { GeometryValid: true, Hits: > 0 })
                    return true;
            }
        }
        return false;
    }
}

/// <summary>The same directed terrain constraints are queried by all three searchers.</summary>
internal sealed class MechanicNavigationGrid(INavigationGrid grid, EncounterPlanningSnapshot snapshot) : INavigationGrid
{
    internal bool UsesOnlyArenaGeometry { get; } = grid is EncounterTerrainGrid &&
        snapshot.StaticObstacles.Length == 0 && snapshot.TemporaryObstacles.Length == 0 &&
        snapshot.Hints is not { ObstacleMap.Bitmap: not null };
    public INavigationGrid Topology => grid.Topology;
    public int Width => grid.Width;
    public int Height => grid.Height;
    public (int x, int y) WorldToGrid(Vector3 p) => grid.WorldToGrid(p);
    public Vector3 GridToWorld(int x, int y) => grid.GridToWorld(x, y);
    public bool IsFree(int x, int y)
    {
        if (!grid.IsFree(x, y)) return false;
        return UsesOnlyArenaGeometry || CanStand(grid.GridToWorld(x, y));
    }
    public bool CanStand(Vector3 position) => grid.CanStand(position) &&
        (UsesOnlyArenaGeometry || EncounterGeometry.GeometryLegal(snapshot, new(position.X, position.Z), new(position.X, position.Z)));
    public bool CanTraverse(Vector3 from, Vector3 to) =>
        grid.CanTraverse(from, to) && (UsesOnlyArenaGeometry || EncounterGeometry.GeometryLegal(snapshot, new(from.X, from.Z), new(to.X, to.Z)));
}
