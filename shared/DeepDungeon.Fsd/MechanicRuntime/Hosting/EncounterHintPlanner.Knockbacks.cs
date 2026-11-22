using System.Numerics;
using System.Threading;
using BossMod;

namespace MasterPlayer.MechanicRuntime;

public sealed partial class EncounterHintPlanner
{
    // A known native prefix has no input choices. Check it once, then let the
    // same SIPP solver plan from its observed landing/release. This does not
    // turn ordinary knockbacks into a history/tree-search problem.
    private EncounterRoute? PlanAfterObservedDisplacement(EncounterRouteRequest q)
    {
        var observed = q.Displacement!.Value;
        var motion = observed.Transition(q.Position, q.Now, 0);
        if (!TerrainLegal(q, q.Position, motion.To)) return null;
        var forced = EncounterDisplacement.Assess(q.Snapshot, motion, q.Now);
        if (!forced.GeometryValid || forced.Hits != 0) return null;
        if (q.TimingBoundary is { } boundary && boundary.Source.Activation < observed.End)
            return null; // The known motion does not release control before the next launch.
        foreach (var prediction in q.Snapshot.Knockbacks)
            if (prediction.Source.Activation >= q.Now && prediction.Source.Activation < observed.End)
                return null; // unresolved native overlap semantics
        var horizon = _planningHorizon;
        try
        {
            _planningHorizon = Math.Max(.001, horizon - motion.End);
            var future = q with
            {
                Now = observed.End, Position = motion.To,
                Snapshot = q.Snapshot with { Displacement = null },
                Observation = new(motion.To, Vector2.Zero, observed.End, q.Motion),
                Continuation = null,
            };
            _positions[_count] = future.Position;
            if (!Prepare(future)) return null;
            var tail = PlanIntervals(future, CancellationToken.None);
            if (tail == null) return null;
            var points = new List<EncounterRoutePoint>(tail.Points.Length + 1)
            { new(q.Position, 0), new(motion.To, motion.End) { GameDriven = true, CommandVelocity = Vector2.Zero } };
            foreach (var point in tail.Points.AsSpan(1))
                points.Add(point with { Seconds = point.Seconds + motion.End });
            return tail with { OriginUtc = q.Now, Points = points.ToArray(), Assessment = null };
        }
        finally { _planningHorizon = horizon; _positions[_count] = q.Position; }
    }

    // Each launch separates two phases. A walking edge cannot cross the launch
    // time, even if both endpoints remain inside one ordinary safe interval.
    private void AddInterval(EncounterRouteRequest q, int node, double start, double end, int phase)
    {
        var events = q.Snapshot.Knockbacks;
        for (; phase < events.Length; ++phase)
        {
            var at = (events[phase].Source.Activation - q.Now).TotalSeconds;
            if (at >= end) break;
            if (at >= start)
            {
                _states.Add(new(node, new(start, at)) { Knockback = phase });
                start = at;
            }
        }
        _states.Add(new(node, new(start, end)) { Knockback = phase });
    }

    private bool TerminalClear(EncounterRouteRequest q, SafeState state, double arrival)
        => q.CanEndAt(IntervalPosition(state), _planningHorizon, default)
            && (state.ExactPosition is { } landing
                ? TerminalAssessment(q, landing, arrival).Hits == 0
                : TerminalClear(q, state.Node, arrival));

    private void ExpandKnockback(EncounterRouteRequest q, int parent, SafeState source)
    {
        var events = q.Snapshot.Knockbacks;
        var eventIndex = source.Knockback;
        var at = (events[eventIndex].Source.Activation - q.Now).TotalSeconds;
        if (at < source.Arrival || at > source.Window.End) return;
        var from = IntervalPosition(source);
        var waiting = at > source.Arrival
            ? AssessInterval(q, from, from, source.Arrival, at, source.Parent < 0)
            : new EncounterTransitionResult(true, 0, 0, false);
        if (!waiting.GeometryValid || waiting.Hits != 0
            || EncounterHintEvaluator.Violations(q.Snapshot, from, q.Now, at, out _) != 0) return;
        if (EncounterDisplacement.Transition(q.Snapshot, events[eventIndex], from, default, at) is not { } motion) return;
        var landing = motion.To;
        var next = eventIndex + 1;
        var end = next < events.Length ? (events[next].Source.Activation - q.Now).TotalSeconds : _planningHorizon;
        if (motion.End > end || !events[eventIndex].AllowsLanding(landing) || !TerrainLegal(q, from, landing)) return;
        var forced = EncounterDisplacement.Assess(q.Snapshot, motion, q.Now);
        if (!forced.GeometryValid || forced.Hits != 0) return;
        foreach (var hazard in q.Snapshot.Hazards)
        {
            if (hazard.Activation == DateTime.MaxValue || EncounterHazardRules.Clearance(hazard, landing) >= 0) continue;
            var begin = (hazard.Activation - q.Now).TotalSeconds;
            if (hazard.Kind == EncounterHazardKind.Persistent && begin <= motion.End && EncounterHintEvaluator.EndSeconds(hazard, q.Now) > motion.End) return;
            if (begin > motion.End) end = Math.Min(end, begin - TimeEpsilon);
        }
        var (x, z) = q.Grid!.WorldToGrid(new(landing.X, 0, landing.Y));
        if ((uint)x >= _width || (uint)z >= _height) return;
        var state = new SafeState(z * _width + x, new(motion.End, end))
        {
            Arrival = motion.End, Departure = at, Parent = parent, Knockback = next, ExactPosition = landing, Step = IntervalStep.Displacement,
            Travel = source.Travel, Soft = source.Soft + EncounterScoring.TransitionCost(waiting) + EncounterScoring.TransitionCost(forced),
            Exposure = source.Exposure + waiting.Exposure + forced.Exposure,
            Margin = Math.Max(source.Margin, Math.Max(waiting.ImpactMarginShortfall, forced.ImpactMarginShortfall)),
        };
        var key = new LandingState(landing, state.Window, next);
        if (_landings.TryGetValue(key, out var previous))
        {
            var existing = _states[previous];
            if (existing.Soft < state.Soft || existing.Soft == state.Soft && existing.Travel <= state.Travel) return;
        }
        var id = _states.Count;
        _landings[key] = id;
        _states.Add(state);
        EnqueueInterval(q, id);
    }
}
