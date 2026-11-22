using BossMod;
namespace MasterPlayer.MechanicRuntime;

internal static class EncounterHazardRules
{
    public static float Clearance(in EncounterHazard hazard, Vector2 position)
    {
        var p = new WPos(position.X, position.Y);
        var distance = hazard.Shape.Distance(p);
        // Some upstream polygons saturate at zero throughout their interior.
        if (distance == 0 && hazard.ClearanceMargin == 0 && hazard.Shape.Contains(p))
            return -ShapeDistance.Epsilon;
        return distance - hazard.ClearanceMargin;
    }
    public static EncounterTransitionResult Evaluate(ReadOnlySpan<EncounterHazard> zones,
        Vector2 from, Vector2 to, double t0, double t1, DateTime now, bool includeStartImpacts = true,
        ReadOnlySpan<bool> initialExposure = default, bool allowExistingExposure = false)
    {
        var duration = t1 - t0;
        var samples = Math.Max(1, Math.Max((int)Math.Ceiling(duration / .05),
            (int)MathF.Ceiling(Vector2.Distance(from, to) / .0625f)));
        var hits = 0;
        var exposure = 0f;
        var marginSquared = 0d; var marginShortfall = 0d;
        var startsExposed = false;
        var length = Vector2.Distance(from, to);
        for (var h = 0; h < zones.Length; ++h)
        {
            ref readonly var zone = ref zones[h];
            if (zone.Activation == DateTime.MaxValue) continue;
            var activation = (zone.Activation - now).TotalSeconds;
            if (zone.Kind == EncounterHazardKind.Impact)
            {
                // One damage check at its supplied timestamp, including an
                // impact between sample ticks. A completed impact is not a wall.
                if (activation < t0 - .000001 || activation > t1 + .000001) continue;
                if (!includeStartImpacts && activation <= t0 + .000001) continue;
                var v = Vector2.Lerp(from, to, (float)Math.Clamp((activation - t0) / duration, 0, 1));
                var d = Clearance(zone, v);
                if (!float.IsFinite(d)) return new(false, 0, 0, startsExposed);
                var shortfall = Math.Max(0, .65f - zone.ClearanceMargin - d);
                marginShortfall = Math.Max(marginShortfall, shortfall);
                marginSquared += shortfall * shortfall;
                if (d < 0)
                {
                    ++hits;
                    startsExposed |= activation <= t0 + .000001;
                }
                continue;
            }
            var finish = Math.Min(t1, EncounterHintEvaluator.EndSeconds(zone, now));
            if (activation > finish || finish <= t0) continue;
            var startDistance = Clearance(zone, from);
            if (!float.IsFinite(startDistance)) return new(false, 0, 0, startsExposed);
            // These two distance fields are 1-Lipschitz. If either endpoint
            // has more clearance than the entire edge length, no interior
            // sample can be inside. Complex hint shapes still use full sampling.
            if (zone.Shape is SDCircle or SDDonut)
            {
                if (startDistance > length + .0001f) continue;
                var endDistance = Clearance(zone, to);
                if (!float.IsFinite(endDistance)) return new(false, 0, 0, startsExposed);
                if (endDistance > length + .0001f) continue;
            }
            var coveredStart = activation <= t0 && startDistance < 0;
            startsExposed |= coveredStart;
            var exiting = coveredStart && (allowExistingExposure || !initialExposure.IsEmpty && initialExposure[h]);
            var violated = false;
            var begin = Math.Max(t0, activation);
            for (var i = 0; i <= samples; ++i)
            {
                var seconds = begin + (finish - begin) * i / samples;
                var v = Vector2.Lerp(from, to, (float)((seconds - t0) / duration));
                var distance = Clearance(zone, v);
                if (!float.IsFinite(distance)) return new(false, 0, 0, startsExposed);
                if (distance >= 0) { exiting = false; continue; }
                violated |= !exiting;
                exposure += -distance * (float)((finish - begin) / samples) * (i == 0 || i == samples ? .5f : 1f);
            }
            if (violated) ++hits;
        }
        return new(true, hits, exposure, startsExposed, marginSquared, marginShortfall);
    }
}
