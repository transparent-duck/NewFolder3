using System;
using System.Collections.Generic;
using System.Numerics;
using BossMod;
using Dalamud.Bindings.ImGui;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Encounter;

// World-space overlay for captured mechanic draw commands (draw-only stage:
// shows predicted zones without any movement ownership). Primitives are
// built in world space first, then EACH ORIGINAL EDGE is clipped against
// the camera front half-space through the same 3-arg WorldToScreen +
// bounded 16-step crossing math as WorldDrawHelper.TryClipWorldLineToScreen
// — vertex-dropping projection is gone, so partially offscreen shapes keep
// their true silhouette. Fills tessellate world-space primitives into
// triangles and clip each triangle the same way (annuli are quads split
// into triangles, never an opaque inner disk; concave rings never reach
// PathFillConvex). World forward convention: (sin r, 0, cos r).
public sealed partial class EncounterMechanicHost
{
    private const int CircleSegments = 128;
    private static readonly Vector2[] CircleDirections = BuildCircleDirections();
    private static Vector2[] BuildCircleDirections()
    {
        var result = new Vector2[CircleSegments];
        for (var i = 0; i < result.Length; ++i)
        {
            var angle = i * MathF.Tau / CircleSegments;
            result[i] = new(MathF.Sin(angle), MathF.Cos(angle));
        }
        return result;
    }

    public void DrawOverlay()
    {
        var commands = LastDrawCommands;
        if (commands.Count == 0)
            return;
        var player = Service.LocalPlayer;
        if (player == null)
            return;
        var y = player.Position.Y + 0.15f;
        var draw = ImGui.GetBackgroundDrawList();
        foreach (var cmd in commands)
        {
            try
            {
                EmitCommand(draw, cmd, y);
            }
            catch
            {
            }
        }
    }

    private static void EmitCommand(ImDrawListPtr draw, MechanicDrawCommand cmd, float y)
    {
        switch (cmd)
        {
            case MechanicDrawZoneCircle c:
                EmitCircle(draw, c.Center, c.Radius, y, c.Color, !c.Outline, c.Thickness);
                break;
            case MechanicDrawZoneDonut d:
                EmitAnnulus(draw, d.Center, d.InnerRadius, d.OuterRadius, y, d.Color, !d.Outline, d.Thickness);
                break;
            case MechanicDrawZoneCone c:
                EmitCone(draw, c.Center, c.InnerRadius, c.OuterRadius, c.Direction.Rad, c.HalfAngle.Rad, y, c.Color, !c.Outline, c.Thickness);
                break;
            case MechanicDrawZoneRect r:
            {
                var dir = r.Direction.Normalized();
                var n = new WDir(-dir.Z, dir.X);
                var a = r.Origin - dir * r.LenBack - n * r.HalfWidth;
                var b = r.Origin + dir * r.LenFront - n * r.HalfWidth;
                var cc = r.Origin + dir * r.LenFront + n * r.HalfWidth;
                var d = r.Origin - dir * r.LenBack + n * r.HalfWidth;
                EmitPoly(draw, new[] { a, b, cc, d }, y, r.Color, !r.Outline, true, r.Thickness);
                break;
            }
            case MechanicDrawZoneCapsule cap:
            {
                var dir = cap.Direction.Normalized();
                var n = new WDir(-dir.Z, dir.X);
                var e = cap.Start + dir * cap.Length;
                EmitPoly(draw, new[] { cap.Start - n * cap.Radius, e - n * cap.Radius, e + n * cap.Radius, cap.Start + n * cap.Radius }, y, cap.Color, !cap.Outline, true, cap.Thickness);
                EmitCircle(draw, cap.Start, cap.Radius, y, cap.Color, !cap.Outline, cap.Thickness);
                EmitCircle(draw, e, cap.Radius, y, cap.Color, !cap.Outline, cap.Thickness);
                break;
            }
            case MechanicDrawPolyline p:
                EmitPoly(draw, p.Vertices, y, p.Color, false, p.Closed, p.Thickness);
                break;
            case MechanicDrawTriangle t:
                EmitPoly(draw, new[] { t.A, t.B, t.C }, y, t.Color, t.Filled, true, t.Thickness);
                break;
            case MechanicDrawQuad q:
                EmitPoly(draw, new[] { q.A, q.B, q.C, q.D }, y, q.Color, true, true, q.Thickness);
                break;
            case MechanicDrawLine l:
                EmitPoly(draw, new[] { l.A, l.B }, y, l.Color, false, false, l.Thickness);
                break;
            case MechanicDrawZoneTriangles triangles:
                for (var i = 0; i + 2 < triangles.Vertices.Length; i += 3)
                    FillTriangle(draw, ToV3(triangles.Vertices[i], y),
                        ToV3(triangles.Vertices[i + 1], y), ToV3(triangles.Vertices[i + 2], y),
                        WithAlpha(triangles.Color, 0.35f));
                break;
            case MechanicDrawComplexPoly { Outline: true } poly:
                foreach (var part in poly.Parts)
                {
                    EmitPoly(draw, part.Exterior, y, poly.Color, false, true, poly.Thickness);
                    foreach (var hole in part.Holes)
                        EmitPoly(draw, hole, y, poly.Color, false, true, poly.Thickness);
                }
                break;
            case MechanicDrawTextWorld t:
                if (Service.GameGui.WorldToScreen(ToV3(t.Position, y), out var sp, out _) &&
                    float.IsFinite(sp.X) && float.IsFinite(sp.Y))
                    draw.AddText(sp, t.Color, t.Text);
                break;
        }
    }

    private static void EmitCircle(ImDrawListPtr draw, WPos center, float radius, float y, uint color, bool fill, float thickness)
    {
        var ring = new Vector3[CircleSegments];
        for (var i = 0; i < CircleSegments; ++i)
        {
            var direction = CircleDirections[i];
            ring[i] = new Vector3(center.X + radius * direction.X, y, center.Z + radius * direction.Y);
        }
        if (fill)
        {
            var c = ToV3(center, y);
            for (var i = 0; i < CircleSegments; ++i)
                FillTriangle(draw, c, ring[i], ring[(i + 1) % CircleSegments], WithAlpha(color, 0.35f));
        }
        for (var i = 0; i < CircleSegments; ++i)
            EmitEdge(draw, ring[i], ring[(i + 1) % CircleSegments], WithAlpha(color, 0.9f), thickness);
    }

    /// <summary>Donut as annulus quads split into triangles — never an
    /// opaque inner disk, never a concave ring into PathFillConvex.</summary>
    private static void EmitAnnulus(ImDrawListPtr draw, WPos center, float inner, float outer, float y, uint color, bool filled, float thickness)
    {
        var innerRing = new Vector3[CircleSegments];
        var outerRing = new Vector3[CircleSegments];
        for (var i = 0; i < CircleSegments; ++i)
        {
            var s = CircleDirections[i].X;
            var c = CircleDirections[i].Y;
            innerRing[i] = new Vector3(center.X + inner * s, y, center.Z + inner * c);
            outerRing[i] = new Vector3(center.X + outer * s, y, center.Z + outer * c);
        }
        for (var i = 0; i < CircleSegments; ++i)
        {
            var j = (i + 1) % CircleSegments;
            if (filled)
            {
                var fill = WithAlpha(color, 0.35f);
                FillTriangle(draw, innerRing[i], outerRing[i], outerRing[j], fill);
                FillTriangle(draw, innerRing[i], outerRing[j], innerRing[j], fill);
            }
            var stroke = WithAlpha(color, 0.9f);
            EmitEdge(draw, outerRing[i], outerRing[j], stroke, thickness);
            EmitEdge(draw, innerRing[i], innerRing[j], stroke, thickness);
        }
    }

    /// <summary>Ordinary cone = center fan; annular cone = paired
    /// inner/outer arc strips (annulus segments, center fan only when the
    /// inner radius is effectively zero).</summary>
    private static void EmitCone(ImDrawListPtr draw, WPos center, float inner, float outer, float dirRad, float halfRad, float y, uint color, bool filled, float thickness)
    {
        var outerArc = new Vector3[CircleSegments + 1];
        for (var i = 0; i <= CircleSegments; ++i)
        {
            var a = dirRad - halfRad + i * (halfRad * 2f) / CircleSegments;
            outerArc[i] = new Vector3(center.X + outer * MathF.Sin(a), y, center.Z + outer * MathF.Cos(a));
        }
        var fill = WithAlpha(color, 0.35f);
        var stroke = WithAlpha(color, 0.9f);
        if (inner > 0.01f)
        {
            var innerArc = new Vector3[CircleSegments + 1];
            for (var i = 0; i <= CircleSegments; ++i)
            {
                var a = dirRad - halfRad + i * (halfRad * 2f) / CircleSegments;
                innerArc[i] = new Vector3(center.X + inner * MathF.Sin(a), y, center.Z + inner * MathF.Cos(a));
            }
            for (var i = 0; i < CircleSegments; ++i)
            {
                if (filled)
                {
                    FillTriangle(draw, innerArc[i], outerArc[i], outerArc[i + 1], fill);
                    FillTriangle(draw, innerArc[i], outerArc[i + 1], innerArc[i + 1], fill);
                }
                EmitEdge(draw, outerArc[i], outerArc[i + 1], stroke, thickness);
                EmitEdge(draw, innerArc[i], innerArc[i + 1], stroke, thickness);
            }
            EmitEdge(draw, innerArc[0], outerArc[0], stroke, thickness);
            EmitEdge(draw, innerArc[CircleSegments], outerArc[CircleSegments], stroke, thickness);
        }
        else
        {
            var c = ToV3(center, y);
            for (var i = 0; i < CircleSegments; ++i)
            {
                if (filled)
                    FillTriangle(draw, c, outerArc[i], outerArc[i + 1], fill);
                EmitEdge(draw, outerArc[i], outerArc[i + 1], stroke, thickness);
            }
            EmitEdge(draw, c, outerArc[0], stroke, thickness);
            EmitEdge(draw, c, outerArc[CircleSegments], stroke, thickness);
        }
    }

    private static void EmitPoly(ImDrawListPtr draw, WPos[] verts, float y, uint color, bool fill, bool closed, float thickness)
    {
        var world = new Vector3[verts.Length];
        for (var i = 0; i < verts.Length; ++i)
            world[i] = ToV3(verts[i], y);
        // Convex fan triangles — every edge of the ORIGINAL polygon is
        // also stroked; tessellation diagonals never are.
        if (fill && world.Length >= 3)
            for (var i = 1; i < world.Length - 1; ++i)
                FillTriangle(draw, world[0], world[i], world[i + 1], WithAlpha(color, 0.35f));
        var stroke = WithAlpha(color, 0.9f);
        for (var i = 1; i < world.Length; ++i)
            EmitEdge(draw, world[i - 1], world[i], stroke, thickness);
        if (closed && world.Length > 1)
            EmitEdge(draw, world[^1], world[0], stroke, thickness);
    }

    /// <summary>Stroke one ORIGINAL edge — clipped against the front
    /// half-space by the shared world-draw helper; a fully-behind
    /// segment emits nothing.</summary>
    private static void EmitEdge(ImDrawListPtr draw, Vector3 a, Vector3 b, uint color, float thickness)
    {
        if (DeepDungeon.Fsd.Dalamud.WorldDrawHelper.TryClipWorldLineToScreen(
                Service.GameGui, a, b, out var sa, out var sb))
            draw.AddLine(sa, sb, color, thickness);
    }

    /// <summary>Clip a world-space triangle to the front half-space and
    /// fill the visible portion — the shared helper owns the crossing
    /// search, 3-argument projection and whole-triangle rejection.</summary>
    private static void FillTriangle(ImDrawListPtr draw, Vector3 a, Vector3 b, Vector3 c, uint color) =>
        DeepDungeon.Fsd.Dalamud.WorldDrawHelper.DrawWorldTriangleFilled(
            draw, Service.GameGui, a, b, c, color);

    private static Vector3 ToV3(WPos p, float y) => new(p.X, y, p.Z);

    private static uint WithAlpha(uint color, float a)
    {
        var ab = (uint)(a * 255f) << 24;
        return (color & 0x00FFFFFFu) | ab;
    }
}
