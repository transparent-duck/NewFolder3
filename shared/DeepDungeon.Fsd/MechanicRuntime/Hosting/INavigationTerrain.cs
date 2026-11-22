using System;
using System.Numerics;

namespace MasterPlayer.MechanicRuntime;

/// <summary>
/// Agent-bound ground traversal. The adapter owns body clearance, raster error
/// and tracking tolerance; consumers must not inflate its result again.
/// XZ queries project onto the adapter's selected floor/surface. A 3D query can
/// retain actual starting feet for directed recovery from existing contact.
/// Height lookup, attack sight and airborne trajectories are separate queries.
/// </summary>
public interface INavigationTerrain
{
    bool CanStand(Vector3 position);
    bool CanTraverse(Vector3 from, Vector3 to);
    bool CanStandXZ(Vector2 position) => CanStand(new Vector3(position.X, 0, position.Y));
    bool CanTraverseXZ(Vector2 from, Vector2 to) =>
        CanTraverse(new Vector3(from.X, 0, from.Y), new Vector3(to.X, 0, to.Y));
    bool CanTraverseFromGrounded(Vector3 from, Vector2 to) => CanTraverse(from, new Vector3(to.X, from.Y, to.Y));
}

/// <summary>Physical clearance is applied once per geometry source, not once per consumer.</summary>
public readonly record struct NavigationClearance(float BodyRadius, float TrackingTolerance)
{
    public float Radius => BodyRadius + TrackingTolerance;
    public float RasterPadding(float pixelSize, float bakedBodyRadius = 0) =>
        MathF.Max(0, BodyRadius - bakedBodyRadius) + TrackingTolerance + pixelSize * .7071068f;
    // Square-cell dilation uses an axis-wise bound; the half diagonal above is
    // for testing a continuous obstacle against cell centers instead.
    public int WholeCellBorder(float pixelSize, float bakedBodyRadius) =>
        (int)MathF.Ceiling((MathF.Max(0, BodyRadius - bakedBodyRadius) + TrackingTolerance) / pixelSize + .5f);
}

/// <summary>Shared conservative supercover, including grid lines and corner ties.</summary>
public static class NavigationGridTraversal
{
    public static bool CanTraverse(Vector2 from, Vector2 to, Vector2 origin, float pixelSize,
        int width, int height, Func<int, int, bool> cellFree)
    {
        if (!float.IsFinite(pixelSize) || pixelSize <= 0) return false;
        double sx = ((double)from.X - origin.X) / pixelSize;
        double sy = ((double)from.Y - origin.Y) / pixelSize;
        double ex = ((double)to.X - origin.X) / pixelSize;
        double ey = ((double)to.Y - origin.Y) / pixelSize;
        if (!double.IsFinite(sx) || !double.IsFinite(sy) || !double.IsFinite(ex) || !double.IsFinite(ey)
            || sx < 0 || sy < 0 || ex < 0 || ey < 0 || sx >= width || ex >= width || sy >= height || ey >= height)
            return false;
        bool PointFree(double px, double py)
        {
            int cx = (int)Math.Floor(px), cy = (int)Math.Floor(py);
            bool onX = px == cx, onY = py == cy;
            return cellFree(cx, cy) && (!onX || cellFree(cx - 1, cy))
                && (!onY || cellFree(cx, cy - 1)) && (!(onX && onY) || cellFree(cx - 1, cy - 1));
        }
        if (!PointFree(sx, sy) || !PointFree(ex, ey)) return false;
        double dx = ex - sx, dy = ey - sy;
        if (dx == 0 && dy == 0) return true;
        int x = (int)Math.Floor(sx), y = (int)Math.Floor(sy);
        int stepX = Math.Sign(dx), stepY = Math.Sign(dy);
        bool alongX = stepX == 0 && sx == x, alongY = stepY == 0 && sy == y;
        bool CellFree(int cx, int cy) => cellFree(cx, cy)
            && (!alongX || cellFree(cx - 1, cy)) && (!alongY || cellFree(cx, cy - 1));
        double deltaX = stepX == 0 ? double.PositiveInfinity : 1 / Math.Abs(dx);
        double deltaY = stepY == 0 ? double.PositiveInfinity : 1 / Math.Abs(dy);
        double nextX = stepX > 0 ? (x + 1 - sx) / dx : stepX < 0 ? (sx - x) / -dx : double.PositiveInfinity;
        double nextY = stepY > 0 ? (y + 1 - sy) / dy : stepY < 0 ? (sy - y) / -dy : double.PositiveInfinity;
        for (long remaining = (long)width + height + 2; remaining > 0; --remaining)
        {
            if (!CellFree(x, y)) return false;
            if (Math.Min(nextX, nextY) > 1) return true;
            if (Math.Abs(nextX - nextY) <= 1e-12)
            {
                if (!CellFree(x + stepX, y) || !CellFree(x, y + stepY)) return false;
                x += stepX; y += stepY; nextX += deltaX; nextY += deltaY;
            }
            else if (nextX < nextY) { x += stepX; nextX += deltaX; }
            else { y += stepY; nextY += deltaY; }
        }
        return false;
    }
}
