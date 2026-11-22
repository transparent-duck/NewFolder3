using System;
using System.Numerics;
using BossMod;

namespace MasterPlayer.MechanicRuntime;

/// <summary>Shared synchronous hint checks. Hits count violated navigation
/// constraints, not damage. Chaser facts are not applied a second time.</summary>
public static class EncounterHintEvaluator
{
    public static string? Validate(in EncounterPlanningSnapshot snapshot)
    {
        if (snapshot.Arena == null || !float.IsFinite(snapshot.Arena.Radius) || snapshot.Arena.Radius <= 0
            || !float.IsFinite(snapshot.ArenaCenter.X) || !float.IsFinite(snapshot.ArenaCenter.Z))
            return "invalid-hint-arena";
        foreach (var zone in snapshot.Hazards)
            if (zone.Shape == null || zone.ExpiresAt is { } end && end < zone.Activation) return "invalid-hint-shape";
        foreach (var chase in snapshot.ChasingHazards)
            if (!double.IsFinite(chase.Interval) || chase.Interval <= 0 || chase.Remaining is < 0 or > 64
                || !float.IsFinite(chase.Radius) || chase.Radius < 0
                || !float.IsFinite(chase.NextStepDistance) || chase.NextStepDistance < 0
                || !float.IsFinite(chase.StepDistance) || chase.StepDistance < 0
                || !float.IsFinite(chase.PreviousPosition.X) || !float.IsFinite(chase.PreviousPosition.Z))
                return "invalid-chase-rule";
        if (snapshot.PathMechanic is { Valid: false })
            return "invalid-residence-rule";
        return null;
    }

    public static bool GeometryLegal(in EncounterPlanningSnapshot snapshot, Vector2 from, Vector2 to) =>
        EncounterGeometry.GeometryLegal(snapshot, from, to);

    public static bool Active(in EncounterHazard zone, DateTime now, double seconds) =>
        zone.Activation != DateTime.MaxValue
        && (zone.Kind == EncounterHazardKind.Persistent
            ? (zone.Activation - now).TotalSeconds <= seconds && seconds < EndSeconds(zone, now)
            : Math.Abs((zone.Activation - now).TotalSeconds - seconds) <= .000001);

    public static double EndSeconds(in EncounterHazard zone, DateTime now) =>
        zone.ExpiresAt is { } end ? (end - now).TotalSeconds : double.PositiveInfinity;

    public static int Violations(in EncounterPlanningSnapshot snapshot, Vector2 position,
        DateTime now, double seconds, out float clearance)
    {
        var count = 0;
        clearance = float.PositiveInfinity;
        foreach (var zone in snapshot.Hazards)
        {
            if (!Active(zone, now, seconds)) continue;
            var distance = EncounterHazardRules.Clearance(zone, position);
            if (!float.IsFinite(distance)) { ++count; clearance = float.NegativeInfinity; continue; }
            clearance = MathF.Min(clearance, distance);
            if (distance < 0) ++count;
        }
        return count;
    }

    public static EncounterTransitionResult EvaluateSegment(in EncounterPlanningSnapshot snapshot,
        Vector2 from, Vector2 to, double t0, double t1, DateTime now, bool geometryKnownLegal = false,
        bool includeStartImpacts = true)
    {
        if (!(t1 > t0) || !geometryKnownLegal && !GeometryLegal(snapshot, from, to))
            return new(false, 0, 0, false);
        return EncounterHazardRules.Evaluate(snapshot.Hazards, from, to, t0, t1, now, includeStartImpacts);

    }
}
