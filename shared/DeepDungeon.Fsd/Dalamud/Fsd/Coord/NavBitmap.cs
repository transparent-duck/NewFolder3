using MasterPlayer.MechanicRuntime;
using DeepDungeon.Fsd.Core;
using System;
using System.Collections.Generic;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud.Map;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>vnavmesh terrain with the doors closed by the current floor layout.</summary>
internal sealed class NavBitmap : INavigationGrid
{
    private readonly byte[] _blocked;
    private readonly Func<int, int, bool> _cellFree;
    private readonly int _rowBytes;
    private readonly Vector3 _origin;   // world position of cell (0,0)
    private readonly float _pixelSize;  // world units per cell
    internal FloorMapKey Key { get; }
    internal ushort[] RoomFlags { get; }
    internal int ClosedBoxes { get; }

    /// <summary>Room anchors from planmap.lgb — exact room-center
    /// positions paired with room index. The coverage set's anchors.</summary>
    public List<(int Room, Vector3 Pos)> RoomAnchors { get; } = [];
    internal List<PassageSearchPoint> PassageObservationPoints { get; } = [];

    public int Width { get; }
    public int Height { get; }
    public float PixelSize => _pixelSize;

    private List<(Vector3 A, Vector3 B)>? _boundarySegments;
    /// <summary>Free↔blocked boundary edges of the FINAL bitmap — cached
    /// after walls and colliders are painted; one world-space segment
    /// per free cell edge facing a blocked cell.</summary>
    internal List<(Vector3 A, Vector3 B)> BoundarySegments =>
        _boundarySegments ??= BuildBoundarySegments();

    private List<(Vector3 A, Vector3 B)> BuildBoundarySegments()
    {
        var segs = new List<(Vector3 A, Vector3 B)>(1024);
        var h = _pixelSize * 0.5f;
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                if (!IsFree(x, y))
                    continue;
                var c = GridToWorld(x, y);
                if (!IsFree(x + 1, y))
                    segs.Add((new Vector3(c.X + h, c.Y, c.Z - h),
                              new Vector3(c.X + h, c.Y, c.Z + h)));
                if (!IsFree(x - 1, y))
                    segs.Add((new Vector3(c.X - h, c.Y, c.Z - h),
                              new Vector3(c.X - h, c.Y, c.Z + h)));
                if (!IsFree(x, y + 1))
                    segs.Add((new Vector3(c.X - h, c.Y, c.Z + h),
                              new Vector3(c.X + h, c.Y, c.Z + h)));
                if (!IsFree(x, y - 1))
                    segs.Add((new Vector3(c.X - h, c.Y, c.Z - h),
                              new Vector3(c.X + h, c.Y, c.Z - h)));
            }
        return segs;
    }

    internal static unsafe NavBitmap Create(FloorMap map, InstanceContentDeepDungeon* dd)
    {
        var flags = new ushort[25];
        for (int i = 0; i < flags.Length; i++) flags[i] = (ushort)dd->MapData[i];
        return new NavBitmap(map, map.Key, flags);
    }

    internal NavBitmap(FloorMap map, FloorMapKey key, ushort[] flags)
    {
        Key = key;
        _cellFree = IsFree;
        Width = map.Width;
        Height = map.Height;
        _origin = map.Origin;
        _pixelSize = map.PixelSize;
        _rowBytes = map.RowBytes;
        _blocked = (byte[])map.Bits.Clone();
        RoomFlags = flags;
        for (int i = 0; i < RoomFlags.Length; i++)
        {
            if (RoomFlags[i] != 0 && map.Centers[i] is { } pos)
                RoomAnchors.Add((i, pos));
        }
        foreach (var door in map.Doors)
            if ((RoomFlags[door.Cell] & door.Connection) == 0)
            {
                PaintDoor(door.Box);
                ClosedBoxes++;
            }

        for (int i = 0; i < map.Doors.Length; i++)
        {
            var door = map.Doors[i];
            if ((RoomFlags[door.Cell] & door.Connection) == 0 || map.Centers[door.Cell] is not { } center)
                continue;
            var position = new Vector3(door.Box.M41, center.Y, door.Box.M43);
            if (Vector3.DistanceSquared(center, position) >= CoverageSet.VisitRadius * CoverageSet.VisitRadius)
                PassageObservationPoints.Add(new(door.Cell, position, i + 1));
        }
    }

    internal unsafe bool Matches(FloorMapKey? key, InstanceContentDeepDungeon* dd)
    {
        if (key != Key) return false;
        for (int i = 0; i < RoomFlags.Length; i++)
        {
            ushort now = (ushort)dd->MapData[i];
            if (((RoomFlags[i] ^ now) & 15) != 0 || (RoomFlags[i] == 0) != (now == 0))
                return false;
        }
        return true;
    }

    private void PaintDoor(Matrix4x4 box)
    {
        // Doors are unbaked: include body radius, tracking tolerance and the
        // cell half diagonal. A thin, rotated door must block clipped cells.
        var padding = FloorMap.Clearance.RasterPadding(_pixelSize);
        var x = new Vector2(box.M11, box.M13);
        var z = new Vector2(box.M31, box.M33);
        var area = MathF.Abs(x.X * z.Y - x.Y * z.X);
        if (area <= 1e-6f) return;
        var y = new Vector2(box.M21, box.M23);
        var sx = 1 + (padding * z.Length() + MathF.Abs(y.X * z.Y - y.Y * z.X)) / area;
        var sz = 1 + (padding * x.Length() + MathF.Abs(x.X * y.Y - x.Y * y.X)) / area;
        box.M11 *= sx; box.M13 *= sx;
        box.M31 *= sz; box.M33 *= sz;
        PaintQuad(box);
    }

    private void PaintQuad(Matrix4x4 m)
    {
        var ux = m.M11; var uz = m.M13;
        var vx = m.M31; var vz = m.M33;
        Span<Vector2> q = stackalloc Vector2[4];
        q[0] = new(ux - vx + m.M41, uz - vz + m.M43);
        q[1] = new(ux + vx + m.M41, uz + vz + m.M43);
        q[2] = new(-ux + vx + m.M41, -uz + vz + m.M43);
        q[3] = new(-ux - vx + m.M41, -uz - vz + m.M43);
        var minX = q[0].X; var maxX = q[0].X;
        var minZ = q[0].Y; var maxZ = q[0].Y;
        for (var i = 1; i < 4; i++)
        {
            minX = Math.Min(minX, q[i].X); maxX = Math.Max(maxX, q[i].X);
            minZ = Math.Min(minZ, q[i].Y); maxZ = Math.Max(maxZ, q[i].Y);
        }
        var x0 = (int)((minX - _origin.X) / _pixelSize);
        var x1 = (int)((maxX - _origin.X) / _pixelSize);
        var y0 = (int)((minZ - _origin.Z) / _pixelSize);
        var y1 = (int)((maxZ - _origin.Z) / _pixelSize);
        for (var y = Math.Max(0, y0); y <= Math.Min(Height - 1, y1); y++)
            for (var x = Math.Max(0, x0); x <= Math.Min(Width - 1, x1); x++)
            {
                var p = GridToWorld(x, y);
                // inside the convex quad iff on the same side of every edge
                var inside = true;
                int? sign = null;
                for (var i = 0; i < 4 && inside; i++)
                {
                    var a = q[i]; var b = q[(i + 1) & 3];
                    var cross = (b.X - a.X) * (p.Z - a.Y) - (b.Y - a.Y) * (p.X - a.X);
                    if (Math.Abs(cross) < 1e-6f) continue;
                    var s = cross > 0 ? 1 : -1;
                    if (sign.HasValue && s != sign.Value) inside = false;
                    else sign = s;
                }
                if (inside)
                    _blocked[y * _rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
    }

    internal bool ContainsWorld(Vector3 p) => p.X >= _origin.X && p.Z >= _origin.Z
        && p.X < _origin.X + Width * _pixelSize && p.Z < _origin.Z + Height * _pixelSize;

    public (int x, int y) WorldToGrid(Vector3 p) =>
        ((int)((p.X - _origin.X) / _pixelSize),
         (int)((p.Z - _origin.Z) / _pixelSize));

    public Vector3 GridToWorld(int x, int y) =>
        new(_origin.X + (x + 0.5f) * _pixelSize, 0,
            _origin.Z + (y + 0.5f) * _pixelSize);

    /// <summary>vnav bitmaps store 1 = blocked, 0 = free — so "free" is the
    /// unset cell.</summary>
    public bool IsFree(int x, int y) =>
        x >= 0 && y >= 0 && x < Width && y < Height && (_blocked[y * _rowBytes + (x >> 3)] & (0x80 >> (x & 7))) == 0;

    public bool CanStand(Vector3 position) => CanTraverse(position, position);
    public bool CanTraverse(Vector3 from, Vector3 to) => NavigationGridTraversal.CanTraverse(
        new(from.X, from.Z), new(to.X, to.Z), new(_origin.X, _origin.Z),
        _pixelSize, Width, Height, _cellFree);

    /// <summary>
    /// Sub-sample free cells into a coverage anchor net — one anchor per
    /// ~12m of walkable area, so a big room yields several anchors and the
    /// "big room coverage hole" can't exist. Each anchor keeps its room
    /// (nearest room center). Room centers themselves are anchors too.
    /// </summary>
    internal List<(int Room, Vector3 Pos)> SampleCoverageAnchors(
        List<(int Room, Vector3 Pos)> roomCenters)
    {
        const float spacing = 12f;
        var anchors = new List<(int Room, Vector3 Pos)>(roomCenters);
        var kept = new List<Vector3>(roomCenters.Count + 200);
        foreach (var c in roomCenters)
            kept.Add(c.Pos);
        var stepCells = Math.Max(1, (int)(spacing / _pixelSize / 4));
        for (var y = 0; y < Height; y += stepCells)
            for (var x = 0; x < Width; x += stepCells)
            {
                if (!IsFree(x, y))
                    continue;
                var p = GridToWorld(x, y);
                var nearest = -1;
                var nearD2 = float.MaxValue;
                for (var k = 0; k < kept.Count; k++)
                {
                    var d = kept[k] - p;
                    var d2 = d.X * d.X + d.Z * d.Z;
                    if (d2 < nearD2) { nearD2 = d2; nearest = k; }
                }
                if (nearD2 > spacing * spacing)
                {
                    // assign room by nearest room center
                    var room = 0;
                    var rd2 = float.MaxValue;
                    for (var k = 0; k < roomCenters.Count; k++)
                    {
                        var d = roomCenters[k].Pos - p;
                        var d2 = d.X * d.X + d.Z * d.Z;
                        if (d2 < rd2) { rd2 = d2; room = roomCenters[k].Room; }
                    }
                    anchors.Add((room, p));
                    kept.Add(p);
                }
            }
        return anchors;
    }
}
