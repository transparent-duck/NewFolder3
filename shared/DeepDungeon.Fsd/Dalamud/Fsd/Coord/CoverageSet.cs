using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// The floor's coverage truth: a set of anchors (room centers from the wall
/// data — exact positions, not estimates) marked visited as the player walks
/// within range. "All anchors visited" = "floor covered" — the guarantee the
/// old room-context snapshot tried to fake. Tasks don't live here; this set
/// only answers "where haven't we been".
/// </summary>
internal sealed class CoverageSet
{
    private readonly List<(int room, Vector3 pos)> _anchors;
    private readonly int[] _order; // skeleton: first-unvisited index lookup
    private readonly bool[] _visited;
    private int _visitedCount;

    /// <summary>How close the player must come to count an anchor visited —
    /// comfortably inside the entity load radius so anything in the room is
    /// observable by the time the anchor is marked.</summary>
    internal const float VisitRadius = 8f;

    internal CoverageSet(List<(int room, Vector3 pos)> anchors,
        ushort[]? roomFlags = null, int startRoom = -1)
    {
        _anchors = anchors;
        _visited = new bool[anchors.Count];
        _order = BuildOrder(anchors, roomFlags, startRoom);
    }

    /// <summary>
    /// Global skeleton: rooms in BFS order over the flag-adjacency graph
    /// (from the player's start room), anchors within each room chained by
    /// nearest-neighbor from the previous anchor. Coverage order comes from
    /// graph structure, not per-tick greed — the two-layer design's static
    /// layer.
    /// </summary>
    private static int[] BuildOrder(List<(int room, Vector3 pos)> anchors,
        ushort[]? roomFlags, int startRoom)
    {
        // room visit order via BFS over connection flags
        var roomRank = new int[25];
        for (var i = 0; i < 25; i++) roomRank[i] = int.MaxValue;
        if (roomFlags != null && startRoom >= 0 && startRoom < 25)
        {
            var q = new Queue<int>();
            roomRank[startRoom] = 0;
            q.Enqueue(startRoom);
            while (q.Count > 0)
            {
                var r = q.Dequeue();
                var f = (InstanceContentDeepDungeon.RoomFlags)roomFlags[r];
                void Try(int n, InstanceContentDeepDungeon.RoomFlags a, InstanceContentDeepDungeon.RoomFlags b)
                {
                    if (n is < 0 or >= 25 || roomFlags[n] == 0) return;
                    var nf = (InstanceContentDeepDungeon.RoomFlags)roomFlags[n];
                    if (f.HasFlag(a) && nf.HasFlag(b) && roomRank[n] == int.MaxValue)
                    {
                        roomRank[n] = roomRank[r] + 1;
                        q.Enqueue(n);
                    }
                }
                Try(r - 5, InstanceContentDeepDungeon.RoomFlags.ConnectionN, InstanceContentDeepDungeon.RoomFlags.ConnectionS);
                Try(r + 5, InstanceContentDeepDungeon.RoomFlags.ConnectionS, InstanceContentDeepDungeon.RoomFlags.ConnectionN);
                Try(r + 1, InstanceContentDeepDungeon.RoomFlags.ConnectionE, InstanceContentDeepDungeon.RoomFlags.ConnectionW);
                Try(r - 1, InstanceContentDeepDungeon.RoomFlags.ConnectionW, InstanceContentDeepDungeon.RoomFlags.ConnectionE);
            }
        }
        // order: room rank, then nearest-neighbor chain inside the room
        var sorted = anchors
            .Select((a, i) => (a, i))
            .OrderBy(t => roomRank[Math.Clamp(t.a.room, 0, 24)] == int.MaxValue ? int.MaxValue : roomRank[Math.Clamp(t.a.room, 0, 24)])
            .ThenBy(t => t.a.room)
            .ToList();
        var order = new int[sorted.Count];
        var pos = startRoom >= 0 && startRoom < 25 && anchors.Count > 0
            ? anchors.MinBy(a => (a.pos - Vector3.Zero).LengthSquared()).pos
            : Vector3.Zero;
        var used = new bool[sorted.Count];
        for (var k = 0; k < sorted.Count; k++)
        {
            var best = -1;
            var bestRank = int.MaxValue;
            var bestD2 = float.MaxValue;
            for (var i = 0; i < sorted.Count; i++)
            {
                if (used[i]) continue;
                var rank = roomRank[Math.Clamp(sorted[i].a.room, 0, 24)];
                var d = sorted[i].a.pos - pos;
                var d2 = d.X * d.X + d.Z * d.Z;
                if (rank < bestRank || (rank == bestRank && d2 < bestD2))
                { best = i; bestRank = rank; bestD2 = d2; }
            }
            order[k] = sorted[best].i;
            used[best] = true;
            pos = sorted[best].a.pos;
        }
        return order;
    }

    internal int Total => _anchors.Count;
    internal int VisitedCount => _visitedCount;
    internal bool AllVisited => _visitedCount >= _anchors.Count;

    /// <summary>Mark any anchor within range of the position as visited.
    /// Called every tick — proximity IS the coverage semantics.</summary>
    internal void Update(Vector3 playerPos)
    {
        for (var i = 0; i < _anchors.Count; i++)
        {
            if (_visited[i])
                continue;
            var d = _anchors[i].pos - playerPos;
            if (d.X * d.X + d.Z * d.Z <= VisitRadius * VisitRadius)
            {
                _visited[i] = true;
                _visitedCount++;
            }
        }
    }

    /// <summary>First unvisited anchor in skeleton order — the global route,
    /// not per-tick greed. Local entity ops still interject via arbitration;
    /// this is only the coverage traversal order.</summary>
    internal Vector3? NextUnvisited(Vector3 playerPos)
    {
        foreach (var i in _order)
            if (!_visited[i])
                return _anchors[i].pos;
        return null;
    }

    /// <summary>First unvisited anchor outside the given room, in skeleton
    /// order — used when a room finish would otherwise park the player.</summary>
    internal Vector3? NextUnvisitedOutsideRoom(Vector3 playerPos, int roomIndex)
    {
        foreach (var i in _order)
            if (!_visited[i] && _anchors[i].room != roomIndex)
                return _anchors[i].pos;
        return null;
    }

    internal bool IsRoomCovered(int roomIndex)
    {
        for (var i = 0; i < _anchors.Count; i++)
            if (_anchors[i].room == roomIndex && !_visited[i])
                return false;
        return true;
    }

    internal void MarkRoomVisited(int roomIndex)
    {
        for (var i = 0; i < _anchors.Count; i++)
            if (_anchors[i].room == roomIndex && !_visited[i])
            {
                _visited[i] = true;
                _visitedCount++;
            }
    }

    /// <summary>Mark the anchor nearest to the position as visited — the
    /// AdvanceWaypoint equivalent for op-recorded progress.</summary>
    internal void MarkNearestVisited(Vector3 pos)
    {
        var best = -1;
        var bestD2 = float.MaxValue;
        for (var i = 0; i < _anchors.Count; i++)
        {
            if (_visited[i])
                continue;
            var d = _anchors[i].pos - pos;
            var d2 = d.X * d.X + d.Z * d.Z;
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = i;
            }
        }
        if (best >= 0)
        {
            _visited[best] = true;
            _visitedCount++;
        }
    }

    internal Vector3? RoomAnchor(int roomIndex)
    {
        for (var i = 0; i < _anchors.Count; i++)
            if (_anchors[i].room == roomIndex)
                return _anchors[i].pos;
        return null;
    }
}
