using System.Numerics;
using System.Threading;

namespace MasterPlayer.MechanicRuntime;

/// <summary>Reusable, distance-bounded ground search for one walking interval.</summary>
internal sealed class EncounterReachablePaths
{
    internal const int SectorCount = 8;

    internal readonly record struct Endpoint(int Cell, Vector2 Position, float Distance, int Sector);

    private readonly PriorityQueue<int, float> _frontier = new();
    private readonly List<Endpoint> _endpoints = new();
    private readonly List<Vector2> _chain = new();
    private readonly List<Vector2> _pulled = new();
    private int[] _stamp = [], _parent = [], _sector = [];
    private float[] _distance = [];
    private int _generation;
    private Vector2 _start;
    private INavigationGrid _grid = null!;
    private INavigationTerrain _terrain = null!;
    private Vector2[]? _arenaPositions;

    private Vector2 Position(int cell)
    {
        if (_arenaPositions != null) return _arenaPositions[cell];
        var p = _grid.GridToWorld(cell % _grid.Width, cell / _grid.Width);
        return new(p.X, p.Z);
    }

    internal List<Endpoint> Search(INavigationGrid grid, INavigationTerrain terrain, Vector2 start, float range,
        CancellationToken token)
    {
        _grid = grid;
        _terrain = terrain;
        _start = start;
        _frontier.Clear();
        _endpoints.Clear();
        var count = grid.Width * grid.Height;
        if (_stamp.Length < count)
        {
            _stamp = new int[count];
            _parent = new int[count];
            _sector = new int[count];
            _distance = new float[count];
            _frontier.EnsureCapacity(count);
            _endpoints.EnsureCapacity(count);
        }
        if (++_generation == int.MaxValue)
        {
            Array.Clear(_stamp);
            _generation = 1;
        }
        var (sx, sz) = grid.WorldToGrid(new(start.X, 0, start.Y));
        var cellSize = Vector3.Distance(grid.GridToWorld(0, 0), grid.GridToWorld(1, 0));
        var diagonal = cellSize * MathF.Sqrt(2);
        var headingDistance = Math.Min(range * .5f, cellSize * 3);
        var arena = grid.Topology as EncounterTerrainGrid;
        bool staticEdges = arena != null && ReferenceEquals(grid, terrain) &&
            (ReferenceEquals(grid, arena) || grid is MechanicNavigationGrid { UsesOnlyArenaGeometry: true });
        _arenaPositions = staticEdges ? arena!.Positions : null;
        var connectedGrid = staticEdges ? arena! : grid;
        for (var z = sz - 1; z <= sz + 1; ++z)
            for (var x = sx - 1; x <= sx + 1; ++x)
            {
                if (!connectedGrid.IsFree(x, z)) continue;
                var cell = z * grid.Width + x;
                var point = Position(cell);
                var cost = Vector2.Distance(start, point);
                if (cost <= range && terrain.CanTraverseXZ(start, point))
                    Offer(cell, cost, -1, -1);
            }

        var expanded = 0;
        while (_frontier.TryDequeue(out var cell, out var cost))
        {
            if (cost != _distance[cell]) continue;
            if ((++expanded & 63) == 1) token.ThrowIfCancellationRequested();
            var point = Position(cell);
            var sector = _sector[cell];
            if (sector < 0 && cost >= headingDistance) sector = Sector(point - start);
            if (cost >= range * .50f)
                _endpoints.Add(new(cell, point, cost, sector));

            var x = cell % grid.Width;
            var z = cell / grid.Width;
            for (var i = 0; i < NavigationGrid.Directions.Length; ++i)
            {
                var (dx, dz) = NavigationGrid.Directions[i];
                var nextCost = cost + ((dx == 0 || dz == 0) ? cellSize : diagonal);
                if (nextCost > range + 1e-5f) continue;
                var next = (z + dz) * grid.Width + x + dx;
                if (staticEdges)
                {
                    if ((arena!.Edges[cell] & (1 << i)) == 0) continue;
                }
                else if (!NavigationGrid.Connected(grid, x, z, dx, dz)) continue;
                if (_stamp[next] == _generation && nextCost >= _distance[next]) continue;
                if (!staticEdges && !terrain.CanTraverseXZ(point, Position(next))) continue;
                Offer(next, nextCost, cell, sector);
            }
        }
        return _endpoints;
    }

    private void Offer(int cell, float distance, int parent, int sector)
    {
        _stamp[cell] = _generation;
        _distance[cell] = distance;
        _parent[cell] = parent;
        _sector[cell] = sector;
        _frontier.Enqueue(cell, distance);
    }

    internal static int Sector(Vector2 heading) =>
        (int)MathF.Round(MathF.Atan2(heading.Y, heading.X) * SectorCount / (2 * MathF.PI)) & (SectorCount - 1);

    internal Vector2[] StringPull(int cell)
    {
        _chain.Clear();
        _pulled.Clear();
        for (var node = cell; node >= 0; node = _parent[node]) _chain.Add(Position(node));
        var anchor = _start;
        for (var i = _chain.Count - 2; i >= 0; --i)
        {
            if (_terrain.CanTraverseXZ(anchor, _chain[i])) continue;
            anchor = _chain[i + 1];
            _pulled.Add(anchor);
        }
        _pulled.Add(_chain[0]);
        return _pulled.Count > 1 ? _pulled.ToArray() : [];
    }
}
