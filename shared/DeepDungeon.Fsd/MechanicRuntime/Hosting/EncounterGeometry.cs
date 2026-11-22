using System;
using System.Numerics;
using BossMod;

namespace MasterPlayer.MechanicRuntime;

public readonly record struct EncounterTransitionResult(
    bool GeometryValid, int Hits, float Exposure, bool StartsExposed, double ImpactMarginSquared = 0, double ImpactMarginShortfall = 0);

/// <summary>Shared arena and static-obstacle checks for search and prediction.</summary>
public static class EncounterGeometry
{
    public const float SegmentSpacing = .25f;
    public const float CollisionMargin = .35f;

    public static bool GeometryLegal(in EncounterPlanningSnapshot snapshot, Vector2 from, Vector2 to)
    {
        if (!ArenaSegmentLegal(snapshot, from, to)) return false;
        foreach (var obstacle in from == to ? Array.Empty<ShapeDistance>() : snapshot.TemporaryObstacles)
        {
            var samples = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(from, to) / .25f));
            // Clearance is part of this barrier too, so its own margin cannot
            // strand a player who starts inside it. Once outside, no re-entry.
            var start = obstacle.Distance(new(from.X, from.Y));
            var floor = Math.Clamp(start, 0, EncounterHintPlanner.HintMargin);
            var escaped = start >= floor;
            for (var i = 1; i <= samples; ++i)
            {
                var p = Vector2.Lerp(from, to, (float)i / samples);
                var d = obstacle.Distance(new(p.X, p.Y));
                if (!float.IsFinite(d)) return false;
                var inside = d < floor;
                if (escaped && inside) return false;
                escaped |= !inside;
            }
        }
        if (snapshot.Hints is not { ObstacleMap.Bitmap: { } bitmap } hints) return true;
        var region = hints.ObstacleMap;
        if (!region.HasObstacleMapLineOfSight(snapshot.ArenaCenter, new(from.X, from.Y), new(to.X, to.Y)))
            return false;
        var n = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(from, to) / 0.25f));
        for (var i = 0; i <= n; ++i)
        {
            var p = Vector2.Lerp(from, to, (float)i / n);
            // Include clearance around a cell rather than checking its centre only.
            for (var offset = 0; offset < 5; ++offset)
            {
                var sample = new WPos(p.X + (offset == 1 ? .35f : offset == 2 ? -.35f : 0),
                    p.Y + (offset == 3 ? .35f : offset == 4 ? -.35f : 0));
                if (region.TryWorldToBitmapCell(snapshot.ArenaCenter, sample, out var x, out var z)
                    && bitmap[x, z]) return false;
            }
        }
        return true;
    }

    private static bool ArenaSegmentLegal(in EncounterPlanningSnapshot snap, Vector2 from, Vector2 to)
    {
        var edgeLen = (to - from).Length();
        var samples = Math.Max(1, (int)Math.Ceiling(edgeLen / SegmentSpacing));
        var initialOffset = new WPos(from.X, from.Y) - snap.ArenaCenter;
        var entered = snap.Arena.Contains(initialOffset);
        var lastOutsideDistance = entered ? 0 : (initialOffset - snap.Arena.ClampToBounds(initialOffset)).Length();
        for (var s = 0; s <= samples; ++s)
        {
            var p = Vector2.Lerp(from, to, s / (float)samples);
            var wp = new WPos(p.X, p.Y);
            var offset = wp - snap.ArenaCenter;
            if (snap.Arena.Contains(offset)) entered = true;
            else
            {
                if (entered) return false;
                var distance = (offset - snap.Arena.ClampToBounds(offset)).Length();
                if (distance > lastOutsideDistance + .001f) return false;
                lastOutsideDistance = distance;
            }
            var st = snap.StaticObstacles ?? [];
            for (var k = 0; k < st.Length; ++k)
            {
                if (st[k].Distance(wp) < CollisionMargin)
                    return false;
            }
        }
        return true;
    }
}
