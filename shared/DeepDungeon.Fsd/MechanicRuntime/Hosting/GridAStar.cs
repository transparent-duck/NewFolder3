using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace MasterPlayer.MechanicRuntime;

public enum GridSearchStatus { Running, Found, Exhausted, Cancelled, Unavailable }

/// <summary>One eight-connected A* implementation for fixed destinations and goal regions.</summary>
public static class GridAStar
{
    public static bool TryFindPath(INavigationGrid map, Vector3 start,
        Vector3 end, List<Vector3> path, List<Vector3>? rawPath = null, Search? search = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        path.Clear(); rawPath?.Clear();
        var (ex, ey) = map.WorldToGrid(end);
        (ex, ey) = NearestFree(map, ex, ey);
        if (ex < 0) return false;
        var snapped = map.GridToWorld(ex, ey);
        search ??= new Search();
        search.Begin(map, start, p => p.X == snapped.X && p.Z == snapped.Z,
            p =>
            {
                float dx = Math.Abs(p.X - snapped.X), dz = Math.Abs(p.Z - snapped.Z);
                return Math.Max(dx, dz) + .4142136f * Math.Min(dx, dz);
            });
        while (search.Status == GridSearchStatus.Running)
            search.Step(int.MaxValue, double.PositiveInfinity, token);
        token.ThrowIfCancellationRequested();
        if (search.Status != GridSearchStatus.Found) return false;
        search.CopyPath(path, rawPath);
        // Keep the reachable grid endpoint if the exact destination is obstructed.
        if (path.Count > 0 && Vector3.DistanceSquared(path[^1], end) > 1e-8f
            && map.CanTraverse(path[^1], end)) path.Add(end);
        return true;
    }

    /// <summary>Retains the frontier between frames. Native goal/ground queries
    /// run on the caller's thread. A time slice never changes Running to failure.</summary>
    public sealed class Search
    {
        private INavigationGrid? _map;
        private Func<Vector3, bool?>? _goal;
        private Func<Vector3, bool?>? _fallbackGoal;
        private Func<Vector3, float>? _estimate;
        private Func<Vector3, Vector3?>? _ground;
        private Func<Vector3, bool>? _goalBounds;
        private readonly PriorityQueue<int, float> _open = new();
        private readonly List<int> _chain = new();
        private float[] _cost = [];
        private int[] _parent = [];
        private byte[] _state = [];
        private Vector3[] _positions = [];
        private Vector3 _start;
        private float _cellSize;
        private int _endpoint = -1;
        private int _fallbackEndpoint = -1;
        private float _fallbackCost;
        private bool _fallbackAtStart, _unresolvedFallback;
        private bool _startChecked, _unresolvedGround, _unresolvedGoal;
        public GridSearchStatus Status { get; private set; } = GridSearchStatus.Cancelled;
        public int Expanded { get; private set; }
        public string? Failure { get; private set; }
        public bool UsedFallback { get; private set; }

        internal BitArray CaptureExhaustedCells()
        {
            var cells = new BitArray(_state.Length);
            for (int i = 0; i < _state.Length; ++i)
                cells[i] = _state[i] == 2;
            return cells;
        }

        public void Begin(INavigationGrid map, Vector3 start, Func<Vector3, bool?> goal,
            Func<Vector3, float>? estimate = null, Func<Vector3, Vector3?>? ground = null,
            Func<Vector3, bool>? goalBounds = null, Func<Vector3, bool?>? fallbackGoal = null)
        {
            _map = map; _start = start; _goal = goal; _estimate = estimate; _ground = ground; _goalBounds = goalBounds;
            _fallbackGoal = fallbackGoal; _fallbackEndpoint = -1; _fallbackCost = float.PositiveInfinity;
            _fallbackAtStart = _unresolvedFallback = UsedFallback = false;
            _open.Clear(); _chain.Clear(); _endpoint = -1; Expanded = 0;
            _startChecked = _unresolvedGround = _unresolvedGoal = false; Failure = null;
            var count = checked(map.Width * map.Height);
            if (_state.Length != count)
            { _state = new byte[count]; _parent = new int[count]; _cost = new float[count]; _positions = new Vector3[count]; }
            else Array.Clear(_state);
            _cellSize = Vector2.Distance(XZ(map.GridToWorld(0, 0)), XZ(map.GridToWorld(1, 0)));
            if (!float.IsFinite(_cellSize) || _cellSize <= 0)
            { End(GridSearchStatus.Unavailable, "invalid-navigation-grid"); return; }
            var (sx, sy) = map.WorldToGrid(start);
            (sx, sy) = NearestFree(map, sx, sy);
            if (sx < 0)
            { End(GridSearchStatus.Unavailable, "start-outside-navigation-grid"); return; }
            var first = map.GridToWorld(sx, sy); first.Y = start.Y;
            if (!map.CanTraverse(start, first))
            { End(GridSearchStatus.Unavailable, "start-not-connected-to-grid"); return; }
            int index = sy * map.Width + sx;
            _positions[index] = first; _parent[index] = -1; _cost[index] = 0; _state[index] = 1;
            _open.Enqueue(index, Estimate(first));
            Status = GridSearchStatus.Running;
        }

        public GridSearchStatus Step(int maximumExpanded, double milliseconds, CancellationToken token)
        {
            if (Status != GridSearchStatus.Running) return Status;
            if (token.IsCancellationRequested) return End(GridSearchStatus.Cancelled, "cancelled");
            var timeSliced = milliseconds != double.PositiveInfinity;
            var stamp = timeSliced ? Stopwatch.GetTimestamp() : 0;
            var map = _map!;
            if (!_startChecked)
            {
                _startChecked = true;
                var initial = _goal!(_start);
                if (initial == true) return End(GridSearchStatus.Found, null);
                _unresolvedGoal |= initial == null;
                if (_fallbackGoal != null)
                {
                    var fallback = _fallbackGoal(_start);
                    if (fallback == true) { _fallbackAtStart = true; _fallbackCost = 0; }
                    _unresolvedFallback |= fallback == null;
                }
            }
            for (int n = 0; n < maximumExpanded && _open.Count != 0; ++n)
            {
                if (timeSliced && Stopwatch.GetElapsedTime(stamp).TotalMilliseconds >= milliseconds) return Status;
                if (token.IsCancellationRequested) return End(GridSearchStatus.Cancelled, "cancelled");
                int current = _open.Dequeue();
                if (_state[current] == 2) continue;
                _state[current] = 2; ++Expanded;
                var point = _positions[current];
                bool groundKnown = true;
                if (_ground != null)
                {
                    if (_ground(point) is { } projected) point = _positions[current] = projected;
                    else { groundKnown = false; _unresolvedGround |= _goalBounds?.Invoke(point) ?? true; }
                }
                if (groundKnown)
                {
                    var goal = _goal!(point);
                    if (goal == true) { _endpoint = current; return End(GridSearchStatus.Found, null); }
                    _unresolvedGoal |= goal == null;
                    // Prefer an inner goal throughout this search. A verified
                    // outer candidate remains usable if no inner goal is found.
                    if (_cost[current] < _fallbackCost && _fallbackGoal != null)
                    {
                        var fallback = _fallbackGoal(point);
                        if (fallback == true) { _fallbackEndpoint = current; _fallbackCost = _cost[current]; }
                        _unresolvedFallback |= fallback == null;
                    }
                }
                int x = current % map.Width, y = current / map.Width;
                foreach (var (dx, dy) in NavigationGrid.Directions)
                {
                    if (!NavigationGrid.Connected(map, x, y, dx, dy)) continue;
                    int next = (y + dy) * map.Width + x + dx;
                    if (_state[next] == 2) continue;
                    var position = map.GridToWorld(x + dx, y + dy); position.Y = point.Y;
                    if (!map.CanTraverse(point, position)) continue;
                    float cost = _cost[current] + (dx != 0 && dy != 0 ? 1.4142136f : 1f) * _cellSize;
                    if (_state[next] != 0 && cost >= _cost[next]) continue;
                    _cost[next] = cost; _parent[next] = current; _positions[next] = position; _state[next] = 1;
                    _open.Enqueue(next, cost + Estimate(position));
                }
            }
            if (_open.Count != 0) return Status;
            // Unknown observations elsewhere do not invalidate this known,
            // reachable candidate. They only prevent declaring no route below.
            if (_fallbackAtStart || _fallbackEndpoint >= 0)
            {
                _endpoint = _fallbackAtStart ? -1 : _fallbackEndpoint;
                UsedFallback = true;
                return End(GridSearchStatus.Found, null);
            }
            return _unresolvedGround || _unresolvedGoal || _unresolvedFallback
                ? End(GridSearchStatus.Unavailable, "reachable-position-observation-unavailable")
                : End(GridSearchStatus.Exhausted, "no-reachable-goal");
        }

        public void CopyPath(List<Vector3> path, List<Vector3>? rawPath = null)
        {
            path.Clear(); rawPath?.Clear();
            if (Status != GridSearchStatus.Found) return;
            _chain.Clear();
            for (int i = _endpoint; i >= 0; i = _parent[i]) _chain.Add(i);
            _chain.Reverse();
            path.Add(_start); rawPath?.Add(_start);
            var anchor = _start;
            for (int i = 0; i < _chain.Count; ++i)
            {
                var point = _positions[_chain[i]];
                rawPath?.Add(point);
                if (i > 0 && !_map!.CanTraverse(anchor, point))
                { anchor = _positions[_chain[i - 1]]; path.Add(anchor); }
            }
            if (_endpoint >= 0 && Vector3.DistanceSquared(path[^1], _positions[_endpoint]) > 1e-8f)
                path.Add(_positions[_endpoint]);
        }

        private float Estimate(Vector3 point) => Math.Max(0, _estimate?.Invoke(point) ?? 0);
        private GridSearchStatus End(GridSearchStatus status, string? failure)
        { Status = status; Failure = failure; return status; }
        private static Vector2 XZ(Vector3 p) => new(p.X, p.Z);
    }

    public static (int x, int y) NearestFree(INavigationGrid map, int x, int y, int maxRadius = 40)
    {
        if (map.IsFree(x, y)) return (x, y);
        for (int r = 1; r <= maxRadius; ++r)
            for (int dy = -r; dy <= r; ++dy)
                for (int dx = -r; dx <= r; ++dx)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && map.IsFree(x + dx, y + dy))
                        return (x + dx, y + dy);
        return (-1, -1);
    }
}
