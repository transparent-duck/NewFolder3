using DeepDungeon.MechanicPlanning;
using BossMod;
using System.Threading;

namespace MasterPlayer.MechanicRuntime;

// The live hint model behind the same search boundary used by the offline
// harness. This owns mechanic state; the shared tree never reads AIHints.
internal sealed class EncounterSimulation
{
    internal readonly EncounterRouteRequest Request;
    readonly double _start, _step;
    readonly double _moveFrom = double.PositiveInfinity, _moveUntil = double.PositiveInfinity;
    readonly CancellationToken _token;
    readonly INavigationTerrain _terrain;
    readonly TimedTransition[] _events;
    readonly (EncounterHazard Hazard, double At, double End)[] _persistent;
    int _advances;
    internal List<EncounterHazard>? ChaseConstraints { get; }
    internal List<EncounterHazard>? ResidenceConstraints { get; }
    readonly record struct TimedTransition(double At, int Chase, float Step, EncounterHazard? Impact, int Knockback = -1, bool ResolveMechanic = false, StackPositionGoal[]? Stacks = null);

    public double StartTime => _start;
    public float Speed => Request.Speed;
    public double[] Ends { get; }
    public double End => Ends[^1];
    public bool RequiresHistory { get; }
    public ReadOnlySpan<Vector2> ControlTargets => [];
    internal IEnumerable<double> DecisionTimes => _events.Select(e => e.At)
        .Concat(_persistent.SelectMany(h => new[] { h.At, h.End })).Append(_moveFrom).Append(_moveUntil);

    internal bool RequiresMovement(double from, double until) => _moveFrom < until - 1e-8 && from < _moveUntil - 1e-8;

    internal (WPos Previous, float Step, float Radius)? ChaseAt(EncounterForecastState state, double at)
    {
        for (var i = state.EventIndex; i < _events.Length && _events[i].At <= at + 1e-7; ++i)
        {
            var e = _events[i];
            if (e.Chase < 0 || e.At < at - 1e-7) continue;
            var chase = Request.Snapshot.ChasingHazards[e.Chase];
            if (chase.TargetId != Request.PlayerId) continue;
            var center = state.Centers[e.Chase];
            return (new(center.X, center.Y), e.Step, EncounterChaseRules.Radius(chase.Radius, Speed));
        }
        return null;
    }

    internal static bool NeedsHistory(EncounterPlanningSnapshot snapshot) => snapshot.PathMechanic != null
        || snapshot.ChasingHazards.Any(c => c.Remaining > 0)
        || snapshot.Hints is { KeepMoving: true } or { Mode: AIHints.SpecialMode.Freezing };

    internal EncounterSimulation(EncounterRouteRequest request, double start, double horizon, CancellationToken token, bool collectConstraints = false)
    {
        if (collectConstraints) { ChaseConstraints = new(); ResidenceConstraints = new(); }
        _terrain = request.Terrain ?? new EncounterTerrain(request.Snapshot);
        Request = request; _start = start; _step = .05; _token = token;
        RequiresHistory = NeedsHistory(request.Snapshot);
        if (request.Snapshot.Hints is { KeepMoving: true }) _moveFrom = double.NegativeInfinity;
        else if (request.Snapshot.Hints is { Mode: AIHints.SpecialMode.Freezing } freezing)
        {
            _moveFrom = start + (freezing.ModeActivation - request.Now).TotalSeconds - EncounterHintPlanner.ImpactLeadSeconds;
            if (freezing.ModeFinish != default) _moveUntil = start + (freezing.ModeFinish - request.Now).TotalSeconds;
        }
        Ends = Enumerable.Range(1, (int)Math.Ceiling(horizon / ControlMath.Knot))
            .Select(k => start + Math.Min(horizon, k * ControlMath.Knot)).ToArray();
        var events = new List<TimedTransition>();
        var persistent = new List<(EncounterHazard, double, double)>();
        foreach (var hazard in request.Snapshot.Hazards)
        {
            if (hazard.Activation == DateTime.MaxValue) continue;
            var at = start + (hazard.Activation - request.Now).TotalSeconds;
            if (at > End) continue;
            if (hazard.Kind == EncounterHazardKind.Persistent) persistent.Add((hazard, at, start + EncounterHintEvaluator.EndSeconds(hazard, request.Now)));
            else if (at >= start - .000001) events.Add(new(Math.Max(start, at), -1, 0, hazard));
        }
        if (request.Snapshot.Hints is { StackGoals.Length: > 0 } hints)
            foreach (var group in hints.StackGoals.GroupBy(g => g.Activation))
            {
                var at = group.Key == default ? End : start + (group.Key - request.Now).TotalSeconds;
                if (at >= start && at <= End) events.Add(new(at, -1, 0, null, Stacks: group.ToArray()));
            }
        _persistent = persistent.ToArray();
        for (var c = 0; c < request.Snapshot.ChasingHazards.Length; ++c)
        {
            var chase = request.Snapshot.ChasingHazards[c];
            var at = start + (chase.NextActivation - request.Now).TotalSeconds;
            for (var n = 0; n < Math.Min(chase.Remaining, 64) && at <= End; ++n, at += chase.Interval)
                events.Add(new(Math.Max(start, at), c, n == 0 ? chase.NextStepDistance : chase.StepDistance, null));
        }
        if (request.Snapshot.PathMechanic?.ResolutionAt is { } resolutionAt)
        {
            var at = start + (resolutionAt - request.Now).TotalSeconds;
            if (at >= start - 1e-6 && at <= End) events.Add(new(Math.Max(start, at), -1, 0, null, ResolveMechanic: true));
        }
        for (var k = 0; k < request.Snapshot.Knockbacks.Length; ++k)
        {
            var at = start + (request.Snapshot.Knockbacks[k].Source.Activation - request.Now).TotalSeconds;
            if (at >= start - 1e-6 && at <= End) events.Add(new(Math.Max(start, at), -1, 0, null, k));
        }
        // Launch-position restrictions apply before the corresponding displacement.
        _events = events.OrderBy(e => e.At).ThenBy(e => e.Knockback >= 0 ? 1 : 0).ToArray();
    }

    public EncounterForecastState CreateState()
    {
        var s = new EncounterForecastState
        {
            P = Request.Position, Velocity = Request.Velocity, Time = _start,
            Centers = Request.Snapshot.ChasingHazards.Select(c => new Vector2(c.PreviousPosition.X, c.PreviousPosition.Z)).ToArray(),
            Unsafe = new bool[_persistent.Length]
        };
        for (var i = 0; i < _persistent.Length; ++i)
        {
            var (hazard, at, end) = _persistent[i];
            s.Unsafe[i] = at <= _start && _start < end && EncounterHazardRules.Clearance(hazard, s.P) < 0;
            s.StartsExposed |= s.Unsafe[i];
        }
        if (Request.Displacement is { } displacement && displacement.End > Request.Now)
        {
            s.Forced = displacement.Transition(s.P, Request.Now, _start);
            s.Velocity = Vector2.Zero;
        }
        if (Request.Snapshot.PathMechanic?.Initialize(ref s.Mechanic, s.P, Request.Now, _start) == true)
            Hit(s, _start);

        foreach (var chase in Request.Snapshot.ChasingHazards)
            if (chase.Remaining > 0 && (chase.NextActivation - Request.Now).TotalSeconds < -EncounterHintPlanner.ImpactLeadSeconds)
            { s.StaleChase = true; Hit(s, _start); }
        ApplyEvents(s);
        return s;
    }
    public EncounterForecastState Copy(EncounterForecastState source)
    { var s = new EncounterForecastState(); CopyFrom(s, source); return s; }
    public void CopyFrom(EncounterForecastState target, EncounterForecastState source)
    {
        target.StartupRemaining = source.StartupRemaining;
        target.Forced = source.Forced;
        target.P = source.P; target.Velocity = source.Velocity; target.Time = source.Time; target.EventIndex = source.EventIndex;
        target.GeometryFailures = source.GeometryFailures;
        target.TimingUnresolved = source.TimingUnresolved;
        target.Failures = source.Failures; target.Soft = source.Soft; target.Travel = source.Travel;
        target.ImpactMarginShortfall = source.ImpactMarginShortfall; target.Exposure = source.Exposure;
        target.MinimumChaseClearance = source.MinimumChaseClearance;
        target.Mechanic = source.Mechanic; target.Outside = source.Outside;
        target.StackCost = source.StackCost;
        target.Stopped = source.Stopped; target.FirstViolation = source.FirstViolation;
        target.StaleChase = source.StaleChase; target.StartsExposed = source.StartsExposed;
        if (target.Centers.Length != source.Centers.Length) target.Centers = new Vector2[source.Centers.Length];
        source.Centers.CopyTo(target.Centers, 0);
        if (target.Unsafe.Length != source.Unsafe.Length) target.Unsafe = new bool[source.Unsafe.Length];
        source.Unsafe.CopyTo(target.Unsafe, 0);
    }
    public Vector2 Position(EncounterForecastState state) => state.P;
    public void Advance(EncounterForecastState s, Vector2 velocity, double until)
    {
        if (s.TimingUnresolved) return;
        if ((++_advances & 255) == 0) _token.ThrowIfCancellationRequested();
        // A control is one straight segment. Sample its geometry once, rather
        // than repeating the same boundary work at every integration substep.
        var projectedVelocity = s.Velocity;
        var initialStartup = s.StartupRemaining;
        var projectedStartup = initialStartup;
        var projected = s.P + Request.AdvanceWalking(ref projectedVelocity, ref projectedStartup, velocity, until - s.Time);
        var legal = float.IsFinite(velocity.X) && float.IsFinite(velocity.Y)
            && velocity.LengthSquared() <= Speed * Speed + .001f
            && (Request.Snapshot.Knockbacks.Length != 0 || _terrain.CanTraverse(new Vector3(s.P.X, 0, s.P.Y), new(projected.X, 0, projected.Y)));
        if (!legal) ++s.GeometryFailures;
        s.Outside = !legal;
        var origin = s.P;
        var startedAt = s.Time;
        var initialVelocity = s.Velocity;
        while (!s.TimingUnresolved && s.Time < until - 1e-9)
        {
            if (ApplyEvents(s))
            { origin = s.P; startedAt = s.Time; initialVelocity = s.Velocity; initialStartup = s.StartupRemaining; }
            if (s.TimingUnresolved) return;
            var next = Math.Min(until, s.Time + _step);
            if (s.EventIndex < _events.Length) next = Math.Min(next, _events[s.EventIndex].At);
            if (s.Forced is { } activeMotion) next = Math.Min(next, activeMotion.End);
            var dt = next - s.Time;
            // Evaluate from the control's origin. Repeated float additions can
            // drift a legal boundary endpoint outside the arena before a hold.
            var atVelocity = initialVelocity;
            s.StartupRemaining = initialStartup;
            var p = s.Forced is { } forced ? forced.Position(next)
                : origin + Request.AdvanceWalking(ref atVelocity, ref s.StartupRemaining, velocity, next - startedAt);
            if (s.Forced.HasValue) { atVelocity = Vector2.Zero; s.StartupRemaining = 0; }
            s.Velocity = atVelocity;
            if (Request.Snapshot.Knockbacks.Length != 0 && !_terrain.CanTraverse(new Vector3(s.P.X, 0, s.P.Y), new(p.X, 0, p.Y)))
                ++s.GeometryFailures;
            for (var k = 0; k < _persistent.Length; ++k)
            {
                var (hazard, activation, expiry) = _persistent[k];
                if (activation > next || expiry <= s.Time) continue;
                var begin = Math.Max(s.Time, activation);
                var sample = hazard;
                var check = EncounterHazardRules.Evaluate(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref sample, 1),
                    s.P, p, s.Time - _start, next - _start, Request.Now, allowExistingExposure: s.Unsafe[k]);
                if (!check.GeometryValid) ++s.GeometryFailures;
                if (check.Hits != 0) Hit(s, begin);
                s.Unsafe[k] = EncounterHazardRules.Clearance(hazard, p) < 0;
                s.Exposure += check.Exposure;
                s.Soft += EncounterScoring.ExposureCost(check.Exposure);
            }
            if (Request.Snapshot.PathMechanic is { } mechanic)
            {
                var outcome = mechanic.Advance(ref s.Mechanic, s.P, p, s.Time, next);
                s.Failures += outcome.Hits;
                s.FirstViolation = Math.Min(s.FirstViolation, outcome.FirstViolation - _start);
                s.StartsExposed |= outcome.FirstViolation <= _start + 1e-6;
                s.Exposure += outcome.Exposure; s.Soft += EncounterScoring.ExposureCost(outcome.Exposure);
                if (outcome.Hits > 0)
                    ResidenceConstraints?.Add(new(outcome.Region, Request.Now.AddSeconds(outcome.FirstViolation - _start),
                        EncounterHazardKind.Impact, Predicted: true));
            }
            var stopped = velocity.LengthSquared() < 1e-6f && RequiresMovement(s.Time, next);
            if (stopped && !s.Stopped) Hit(s, Math.Max(s.Time, _moveFrom));
            s.Stopped = stopped;
            s.Travel += Vector2.Distance(s.P, p); s.P = p; s.Time = next;
            if (ApplyEvents(s))
            { origin = s.P; startedAt = s.Time; initialVelocity = s.Velocity; initialStartup = s.StartupRemaining; }
        }
    }

    internal bool IsExposed(EncounterForecastState state)
    {
        for (var i = 0; i < _persistent.Length; ++i)
        {
            var (hazard, at, end) = _persistent[i];
            if (at <= state.Time && state.Time < end && EncounterHazardRules.Clearance(hazard, state.P) < 0)
                return true;
        }
        return false;
    }

    bool ApplyEvents(EncounterForecastState s)
    {
        var displaced = false;
        if (s.Forced is { } finished && s.Time >= finished.End - 1e-8)
        {
            s.P = finished.To; s.Forced = null;
            s.Velocity = Vector2.Zero; s.StartupRemaining = 0; displaced = true;
        }
        while (s.EventIndex < _events.Length && _events[s.EventIndex].At <= s.Time + 1e-8)
        {
            var e = _events[s.EventIndex++]; float clearance; float margin;
            if (e.Stacks != null)
            {
                var best = float.PositiveInfinity;
                foreach (var goal in e.Stacks)
                    best = Math.Min(best, goal.Cost(new(s.P.X, s.P.Y)));
                s.StackCost += Math.Max(0, best - NavigationFollower.Arrival);
                continue;
            }
            if (e.Knockback >= 0)
            {
                var prediction = Request.Snapshot.Knockbacks[e.Knockback];
                if (EncounterDisplacement.Transition(Request.Snapshot, prediction, s.P, s.Mechanic, s.Time) is not { } motion)
                {
                    s.TimingUnresolved = true;
                    return displaced;
                }
                if (motion.End == s.Time && motion.To == s.P) continue;
                // Native overlap/queue semantics are not established. Do not
                // silently replace a motion and certify that route as feasible.
                if (s.Forced.HasValue) { ++s.GeometryFailures; continue; }
                if (!prediction.AllowsLanding(motion.To) ||
                    !_terrain.CanTraverse(new Vector3(s.P.X, 0, s.P.Y), new(motion.To.X, 0, motion.To.Y)))
                    ++s.GeometryFailures;
                s.Forced = motion; s.Velocity = Vector2.Zero; s.StartupRemaining = 0; displaced = true;
                continue;
            }
            if (e.Chase >= 0)
            {
                var chase = Request.Snapshot.ChasingHazards[e.Chase];
                var previous = s.Centers[e.Chase];
                var target = chase.TargetId == Request.PlayerId ? s.P : new Vector2(chase.TargetPosition.X, chase.TargetPosition.Z);
                var quantized = EncounterChaseRules.Landing(new(previous.X, previous.Y), new(target.X, target.Y), e.Step);
                var landing = s.Centers[e.Chase] = new Vector2(quantized.X, quantized.Z);
                clearance = Vector2.Distance(s.P, landing) - EncounterChaseRules.Radius(chase.Radius, Speed);
                s.MinimumChaseClearance = Math.Min(s.MinimumChaseClearance, clearance);
                if (ChaseConstraints != null)
                {
                    var center = chase.TargetId == Request.PlayerId ? previous : landing;
                    var radius = chase.Radius + Speed * (float)EncounterHintPlanner.ImpactLeadSeconds
                        + (chase.TargetId == Request.PlayerId ? e.Step : 0);
                    ChaseConstraints.Add(new(new SDCircle(new(center.X, center.Y), radius), Request.Now.AddSeconds(e.At - _start),
                        EncounterHazardKind.Impact, Predicted: true));
                }
                // Keep the research tree's 2m total chase acceptance margin at
                // 6m/s; the existing live safety margin remains the hard bound.
                margin = Math.Max(0, 2 - Speed * (float)EncounterHintPlanner.ImpactLeadSeconds - EncounterHintPlanner.HintMargin);
            }
            else if (e.ResolveMechanic)
            {
                clearance = Request.Snapshot.PathMechanic!.Resolve(ref s.Mechanic, e.At) ? 1 : -1;
                margin = 0;
            }
            else
            {
                clearance = EncounterHazardRules.Clearance(e.Impact!.Value, s.P);
                margin = Math.Max(0, .65f - EncounterHintPlanner.HintMargin);
            }
            if (!float.IsFinite(clearance)) clearance = -1;
            if (clearance < 0) Hit(s, e.At);
            var shortfall = Math.Max(0, margin - clearance);
            s.ImpactMarginShortfall = Math.Max(s.ImpactMarginShortfall, shortfall);
            s.Soft += EncounterScoring.ImpactCost(shortfall);
        }
        return displaced;
    }

    void Hit(EncounterForecastState state, double at)
    {
        ++state.Failures;
        state.FirstViolation = Math.Min(state.FirstViolation, at - _start);
        state.StartsExposed |= at <= _start + 1e-6;
    }


}

internal sealed class EncounterForecastState
{
    internal ForcedDisplacement? Forced;
    public Vector2 P, Velocity;
    public float StackCost;
    public double StartupRemaining;
    public double FirstViolation = double.PositiveInfinity;
    public double MinimumChaseClearance = double.PositiveInfinity;
    public bool StaleChase, StartsExposed, TimingUnresolved;
    public double Time, Soft, Travel, Exposure, ImpactMarginShortfall;
    public MechanicBranchState Mechanic;
    public int EventIndex, Failures, GeometryFailures;
    public bool Outside, Stopped;
    public Vector2[] Centers = [];
    public bool[] Unsafe = [];
}
