using System.Collections;
using System.Numerics;
using System.Diagnostics;
using System.Threading;
using DeepDungeon.Fsd.Core;

namespace MasterPlayer.MechanicRuntime;

/// <summary>The cells examined by a completed search that found no valid stand.</summary>
public sealed class NavigationRegionExhaustion
{
    private readonly INavigationGrid _grid;
    private readonly Vector3 _target;
    private readonly float _sightOffset;
    private readonly BitArray _examined;

    internal NavigationRegionExhaustion(INavigationGrid grid, Vector3 target, float sightOffset, BitArray examined)
    {
        _grid = grid;
        _target = target;
        _sightOffset = sightOffset;
        _examined = examined;
    }

    public bool AppliesTo(INavigationGrid? map, Vector3 player, Vector3 destination, float offset)
    {
        if (!ReferenceEquals(map?.Topology, _grid.Topology) || destination != _target || offset != _sightOffset)
            return false;
        var (x, y) = _grid.WorldToGrid(player);
        return (uint)x < (uint)_grid.Width && (uint)y < (uint)_grid.Height &&
            _examined[y * _grid.Width + x] && _grid.CanStand(player);
    }
}

/// <summary>A persistent arrival region for one observed target. The host supplies
/// ground and sight observations; search, arrival and route continuity use the same goal.</summary>
public sealed class NavigationRegionGoal(
    Func<INavigationGrid, Vector3, Vector3?> ground,
    Func<Vector3, Vector3, float, float, bool?> canStand,
    Func<Vector3, Vector3, float, float>? horizontalRadiusSquared = null)
{
    private const float StandClearance = NavigationFollower.Arrival + .01f;
    private const double SearchSliceMilliseconds = 5;
    private readonly GridAStar.Search _search = new();
    private readonly List<Vector3> _points = new(), _rejected = new();
    private INavigationGrid? _terrain;
    private Vector3 _player;
    private readonly Dictionary<Vector2, bool> _accepted = new();

    private Vector3 _searchTarget, _pathTarget;
    private bool _fallback;
    private DateTime _nextCheck;
    private float _approach, _hold, _sightOffset;
    public ulong TargetId { get; private set; }
    public Vector3 Target { get; private set; }
    public bool Holding { get; private set; }
    public bool Searching { get; private set; }
    public bool Unreachable => Exhaustion != null;
    public NavigationRegionExhaustion? Exhaustion { get; private set; }
    public NavigationPath? Path { get; private set; }
    public (long Id, DateTime StartedUtc, Vector3 Target, int Slices, double ComputeMs, int Expanded,
        int Projections, int MissingGround, GridSearchStatus Status, string? Failure) SearchProgress =>
        (_searchId, _searchStarted, _searchTarget, _slices, _computeMs, _search.Expanded,
            _projections, _missingGround, _search.Status, _search.Failure);
    private long _searchId;
    private DateTime _searchStarted;
    private int _slices, _projections, _missingGround;
    private double _computeMs;

    // The interval lattice ranks cells by a cheap lower bound. Only reachable
    // finalists and simulated endpoints request ground and sight observations.
    internal float Distance(Vector2 point, float distanceToApproach, bool lowerBound)
    {
        var range = Holding || (_fallback && Path != null) ? _hold : _approach;
        float outside = Math.Max(0, Vector2.Distance(point, new(Target.X, Target.Z)) - range);
        if (lowerBound) return outside;
        if (outside > 0) return Math.Max(outside, distanceToApproach);
        if (!_accepted.TryGetValue(point, out var accepted))
        {
            var current = point == new Vector2(_player.X, _player.Z);
            var stand = current ? _player : ground(_terrain!, new(point.X, _player.Y, point.Y));
            accepted = stand is { } p && Accept(p, Target, range, current ? 0 : StandClearance) == true;
            _accepted.Add(point, accepted);
        }
        return accepted ? 0 : Math.Max(distanceToApproach, NavigationFollower.Arrival);
    }

    public void Observe(ulong id, Vector3 target, float approach, float hold, float sightOffset = 0)
    {
        if (TargetId != id || approach != _approach || hold != _hold) Reset();
        else if (sightOffset != _sightOffset) Invalidate();
        else if (target != Target) Exhaustion = null;
        TargetId = id;
        Target = target;
        _approach = approach;
        _hold = hold;
        _sightOffset = sightOffset;
    }

    public void Reset()
    {
        TargetId = 0;
        _terrain = null;
        _rejected.Clear();
        Invalidate();
    }

    internal void Invalidate()
    {
        Holding = Searching = _fallback = false;
        Exhaustion = null;
        Path = null;
        _nextCheck = default;
    }

    public void Reject(Vector3 stand)
    {
        _rejected.Add(stand);
        Invalidate();
    }

    public void ClearRejected()
    {
        _rejected.Clear();
        Invalidate();
    }

    private bool? Accept(Vector3 point, Vector3 target, float range, float clearance)
    {
        foreach (var rejected in _rejected)
            if (NavigationRange.Point.Contains(rejected, point)) return false;
        return canStand(point, target, range, clearance);
    }

    // Check the actual position first: an already satisfied goal needs no terrain search.
    internal bool ObservePlayer(INavigationGrid? map, Vector3 player, DateTime now)
    {
        _accepted.Clear();
        _player = player;
        if (!ReferenceEquals(map?.Topology, _terrain?.Topology))
        {
            Invalidate();
            _terrain = map;
        }
        if (Exhaustion is { } exhausted && !exhausted.AppliesTo(map, player, Target, _sightOffset))
        {
            Exhaustion = null;
            _nextCheck = default;
        }
        bool atEnd = Path is { } pending && NavigationRange.Point.HasArrived(player, pending.Points[^1]);
        if (now < _nextCheck && !atEnd) return false;
        _nextCheck = now.AddMilliseconds(100);
        float range = Holding || (_fallback && Path != null) ? _hold : _approach;
        Holding = Accept(player, Target, range, 0) ?? Holding;
        if (Holding)
        {
            Path = null;
            Searching = _fallback = false;
            Exhaustion = null;
        }
        return true;
    }

    // Resolve against the full physical map even when the timed solver uses a local window.
    internal void Resolve(INavigationGrid map, Vector3 player, bool refresh, DateTime now, CancellationToken token)
    {
        if (Holding || Unreachable) return;
        if (refresh)
        {
            if (Path is { } path)
            {
                var end = path.Points[^1];
                if (!NavigationRange.Point.HasArrived(player, end))
                {
                    if (Accept(end, Target, _fallback ? _hold : _approach, StandClearance) == true)
                    {
                        Searching = false;
                        return;
                    }
                    // A still-usable fallback stand buys time for the incremental
                    // search. If both ranges fail, connect the remaining corridor to
                    // a verified live stand before discarding its traversable prefix.
                    if (_hold <= _approach || Accept(end, Target, _hold, StandClearance) != true)
                    {
                        if (Target != _pathTarget && TryReconnect(map, player, path, _pathTarget))
                        {
                            Searching = false;
                            return;
                        }
                        Path = null;
                    }
                }
                else Path = null;
            }
            if (TryDirect(map, player))
            {
                Searching = false;
                return;
            }
            if (!Searching) Begin(map, player, now);
        }
        if (!Searching) return;
        var stamp = Stopwatch.GetTimestamp();
        var status = _search.Step(int.MaxValue, SearchSliceMilliseconds, token);
        _computeMs += Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
        ++_slices;
        if (status == GridSearchStatus.Running) return;
        Searching = false;
        if (status == GridSearchStatus.Found)
        {
            _search.CopyPath(_points);
            var end = _points[^1];
            var range = _search.UsedFallback ? _hold : _approach;
            if (Accept(player, Target, range, 0) == true)
            {
                Holding = true;
                Path = null;
            }
            else if (!NavigationRange.Point.HasArrived(player, end)
                && Accept(end, Target, range, StandClearance) == true)
            {
                _pathTarget = Target;
                _fallback = _search.UsedFallback;
                Path = new(map, _points.ToArray());
            }
            else if (Target != _searchTarget)
            {
                // A time-sliced search can finish after its target has moved.
                TryReconnect(map, player, new(map, _points.ToArray()), _searchTarget);
            }
        }
        if (status == GridSearchStatus.Exhausted && Target == _searchTarget && Path == null)
        {
            var exhausted = new NavigationRegionExhaustion(map, _searchTarget, _sightOffset, _search.CaptureExhaustedCells());
            if (exhausted.AppliesTo(map, player, Target, _sightOffset)) Exhaustion = exhausted;
        }
    }

    private bool TryReconnect(INavigationGrid map, Vector3 player, NavigationPath path, Vector3 previousTarget)
    {
        var points = path.Points;
        var seed = points[^1] + (Target - previousTarget);
        if (ground(map, seed) is not { } stand || Accept(stand, Target, _approach, StandClearance) != true)
            return false;

        // Only reuse the part ahead of the player. Prefer the shortest retained
        // prefix, so a patrol never leaves a growing trail of obsolete endpoints.
        int join = points.Length - 1;
        while (join >= 0 && !map.CanTraverse(player, points[join])) --join;
        if (map.CanTraverse(player, stand))
        {
            Path = new(map, [player, stand]);
        }
        else
        {
            if (join < 0) return false;
            int exit = join;
            while (!map.CanTraverse(points[exit], stand))
            {
                if (exit + 1 == points.Length || !map.CanTraverse(points[exit], points[exit + 1]))
                    return false;
                ++exit;
            }
            var route = new Vector3[exit - join + 3];
            route[0] = player;
            Array.Copy(points, join, route, 1, exit - join + 1);
            route[^1] = stand;
            Path = new(map, route);
        }
        _pathTarget = Target;
        _fallback = false;
        return true;
    }

    private bool TryDirect(INavigationGrid map, Vector3 player)
    {
        var delta = new Vector2(player.X - Target.X, player.Z - Target.Z);
        var range = _approach - StandClearance;
        var radiusSquared = horizontalRadiusSquared?.Invoke(player, Target, range) ?? range * range;
        if (radiusSquared <= 0 || delta.LengthSquared() <= radiusSquared) return false;
        // Follow a clear lane into the region. The player's own range check
        // stops the follower at entry, even when this interior waypoint is farther away.
        var offset = Vector2.Normalize(delta) * (MathF.Sqrt(radiusSquared) * .5f);
        var seed = new Vector3(Target.X + offset.X, player.Y, Target.Z + offset.Y);
        if (ground(map, seed) is not { } end || !map.CanTraverse(player, end)
            || Accept(end, Target, _approach, StandClearance) != true) return false;
        _pathTarget = Target;
        _fallback = false;
        Path = new(map, [player, end]);
        return true;
    }

    private void Begin(INavigationGrid map, Vector3 player, DateTime now)
    {
        _searchTarget = Target;
        ++_searchId;
        _searchStarted = now;
        _slices = _projections = _missingGround = 0;
        _computeMs = 0;
        Searching = true;
        float Distance(Vector3 p) => Vector2.Distance(new(p.X, p.Z), new(_searchTarget.X, _searchTarget.Z));
        bool? Candidate(Vector3 point, float range) => NavigationRange.Point.HasArrived(player, point)
            ? Accept(player, _searchTarget, range, 0) : Accept(point, _searchTarget, range, StandClearance);
        Vector3? Project(Vector3 point)
        {
            ++_projections;
            var projected = ground(map, point);
            if (projected == null) ++_missingGround;
            return projected;
        }
        var stamp = Stopwatch.GetTimestamp();
        _search.Begin(map, player, p => Candidate(p, _approach),
            p => Math.Max(0, Distance(p) - _approach), Project,
            p => Distance(p) <= _hold, _hold > _approach ? p => Candidate(p, _hold) : null);
        _computeMs = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
    }
}
