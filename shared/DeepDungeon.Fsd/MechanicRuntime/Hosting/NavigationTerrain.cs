using System.Numerics;

namespace MasterPlayer.MechanicRuntime;

public interface INavigationGrid : INavigationTerrain
{
    INavigationGrid Topology => this;
    int Width { get; }
    int Height { get; }
    (int x, int y) WorldToGrid(Vector3 point);
    Vector3 GridToWorld(int x, int y);
    bool IsFree(int x, int y);
}

/// <summary>A spatial route already found on this terrain; passed unchanged to the follower.</summary>
public sealed record NavigationPath(INavigationGrid Terrain, Vector3[] Points);

public static class NavigationGrid
{
    public static readonly (int X, int Z)[] Directions =
        [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, 1), (-1, -1), (1, -1)];
    public static bool Connected(INavigationGrid map, int x, int y, int dx, int dy) =>
        map.IsFree(x + dx, y + dy) && (dx == 0 || dy == 0 || map.IsFree(x + dx, y) && map.IsFree(x, y + dy));

    /// <summary>Nearest free cell centre by world XZ distance. This is a point
    /// lookup, not a path query: the starting cell and connecting segment may be blocked.</summary>
    public static Vector3? NearestFreeXZ(INavigationGrid map, Vector3 point, System.Threading.CancellationToken token)
    {
        if (map.Width <= 0 || map.Height <= 0) return null;
        var (sx, sy) = map.WorldToGrid(point);
        sx = Math.Clamp(sx, 0, map.Width - 1); sy = Math.Clamp(sy, 0, map.Height - 1);
        var centre = map.GridToWorld(sx, sy);
        var stepX = Math.Abs(map.GridToWorld(1, 0).X - map.GridToWorld(0, 0).X);
        var stepZ = Math.Abs(map.GridToWorld(0, 1).Z - map.GridToWorld(0, 0).Z);
        var offsetX = Math.Abs(point.X - centre.X); var offsetZ = Math.Abs(point.Z - centre.Z);
        Vector3? best = null;
        var bestDistance = float.PositiveInfinity;
        void Check(int x, int y)
        {
            if (!map.IsFree(x, y)) return;
            var p = map.GridToWorld(x, y);
            var dx = p.X - point.X; var dz = p.Z - point.Z;
            var distance = dx * dx + dz * dz;
            if (distance < bestDistance) { bestDistance = distance; best = p; }
        }
        Check(sx, sy);
        var maximum = Math.Max(Math.Max(sx, map.Width - 1 - sx), Math.Max(sy, map.Height - 1 - sy));
        for (var radius = 0; radius <= maximum; ++radius)
        {
            token.ThrowIfCancellationRequested();
            if (radius > 0)
            {
                var left = Math.Max(0, sx - radius); var right = Math.Min(map.Width - 1, sx + radius);
                if (sy - radius >= 0) for (var x = left; x <= right; ++x) Check(x, sy - radius);
                if (sy + radius < map.Height) for (var x = left; x <= right; ++x) Check(x, sy + radius);
                for (var y = Math.Max(0, sy - radius + 1); y <= Math.Min(map.Height - 1, sy + radius - 1); ++y)
                {
                    if (sx - radius >= 0) Check(sx - radius, y);
                    if (sx + radius < map.Width) Check(sx + radius, y);
                }
            }
            // All unvisited centres lie beyond this distance on at least one
            // grid axis. Finish only when none can beat the best Euclidean distance.
            var lowerBound = Math.Min((radius + 1) * stepX - offsetX, (radius + 1) * stepZ - offsetZ);
            if (best != null && lowerBound >= 0 && bestDistance <= lowerBound * lowerBound) break;
        }
        return best;
    }
}

/// <summary>Local planning lattice backed by the duty's physical cells.</summary>
internal sealed class LocalNavigationGrid : INavigationGrid
{
    private readonly INavigationGrid _map;
    private readonly BossMod.WPos _center;
    private readonly float _radius;
    private readonly int _left, _top;
    public int Width { get; }
    public int Height { get; }

    public LocalNavigationGrid(INavigationGrid map, BossMod.WPos center, float radius)
    {
        _map = map;
        _center = center;
        _radius = radius;
        var (x0, z0) = map.WorldToGrid(new(center.X - radius, 0, center.Z - radius));
        var (x1, z1) = map.WorldToGrid(new(center.X + radius, 0, center.Z + radius));
        _left = Math.Max(0, Math.Min(x0, x1));
        _top = Math.Max(0, Math.Min(z0, z1));
        Width = Math.Max(0, Math.Min(map.Width - 1, Math.Max(x0, x1)) - _left + 1);
        Height = Math.Max(0, Math.Min(map.Height - 1, Math.Max(z0, z1)) - _top + 1);
    }

    public bool Matches(INavigationGrid map, BossMod.WPos center, float radius) =>
        ReferenceEquals(map, _map) && center == _center && radius == _radius;
    public (int x, int y) WorldToGrid(Vector3 point)
    {
        var (x, y) = _map.WorldToGrid(point);
        return (x - _left, y - _top);
    }
    public Vector3 GridToWorld(int x, int y) => _map.GridToWorld(x + _left, y + _top);
    public bool IsFree(int x, int y) =>
        (uint)x < Width && (uint)y < Height && _map.IsFree(x + _left, y + _top);
    public bool CanStand(Vector3 position) => _map.CanStand(position);
    public bool CanTraverse(Vector3 from, Vector3 to) => _map.CanTraverse(from, to);
}

internal sealed class EncounterTerrain(EncounterPlanningSnapshot snapshot) : INavigationTerrain
{
    public bool CanStand(Vector3 position) => snapshot.Arena.Contains(new BossMod.WPos(position.X, position.Z) - snapshot.ArenaCenter)
        && CanTraverse(position, position);
    public bool CanTraverse(Vector3 from, Vector3 to) =>
        EncounterGeometry.GeometryLegal(snapshot, new(from.X, from.Z), new(to.X, to.Z));
}

/// <summary>Cached static arena topology. Dynamic obstacles are queried against the current observation.</summary>
internal sealed class EncounterTerrainGrid : INavigationGrid
{
    private readonly EncounterPlanningSnapshot _topology;
    public int Width { get; }
    public int Height => Width;
    public float Resolution { get; }
    public float OriginX { get; }
    public float OriginZ { get; }
    public Vector2[] Positions { get; }
    public bool[] Walkable { get; }
    public byte[] Edges { get; }
    public EncounterTerrainGrid(EncounterPlanningSnapshot snapshot, System.Threading.CancellationToken token)
    {
        _topology = snapshot with { StaticObstacles = [], TemporaryObstacles = [], Hints = null };
        Resolution = MathF.Max(.5f, 2 * snapshot.Arena.Radius / 256);
        Width = (int)MathF.Ceiling(2 * snapshot.Arena.Radius / Resolution) + 3;
        OriginX = snapshot.ArenaCenter.X - (Width - 1) * Resolution * .5f;
        OriginZ = snapshot.ArenaCenter.Z - (Width - 1) * Resolution * .5f;
        var count = Width * Height;
        Positions = new Vector2[count + 1]; Walkable = new bool[count + 1]; Edges = new byte[count];
        for (var i = 0; i < count; ++i)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            var p = Positions[i] = new(OriginX + i % Width * Resolution, OriginZ + i / Width * Resolution);
            Walkable[i] = snapshot.Arena.Contains(new BossMod.WPos(p.X, p.Y) - snapshot.ArenaCenter)
                && EncounterGeometry.GeometryLegal(_topology, p, p);
        }
        for (var i = 0; i < count; ++i)
        {
            if (!Walkable[i]) continue;
            for (var d = 0; d < NavigationGrid.Directions.Length; ++d)
            {
                var (dx, dz) = NavigationGrid.Directions[d];
                var x = i % Width; var z = i / Width;
                if (NavigationGrid.Connected(this, x, z, dx, dz)
                    && CanTraverse(GridToWorld(x, z), GridToWorld(x + dx, z + dz)))
                    Edges[i] |= (byte)(1 << d);
            }
        }
    }
    public (int x, int y) WorldToGrid(Vector3 point) =>
        ((int)MathF.Round((point.X - OriginX) / Resolution), (int)MathF.Round((point.Z - OriginZ) / Resolution));
    public Vector3 GridToWorld(int x, int y) => new(OriginX + x * Resolution, 0, OriginZ + y * Resolution);
    public bool IsFree(int x, int y) => (uint)x < Width && (uint)y < Height && Walkable[y * Width + x];
    public bool CanStand(Vector3 position) => _topology.Arena.Contains(new BossMod.WPos(position.X, position.Z) - _topology.ArenaCenter)
        && CanTraverse(position, position);
    public bool CanTraverse(Vector3 from, Vector3 to) =>
        EncounterGeometry.GeometryLegal(_topology, new(from.X, from.Z), new(to.X, to.Z));
}
