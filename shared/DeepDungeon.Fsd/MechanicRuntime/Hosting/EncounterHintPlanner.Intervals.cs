using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using BossMod;

namespace MasterPlayer.MechanicRuntime;

public sealed partial class EncounterHintPlanner
{
    // Numerical separation only, not an assumed duration of a damage effect.
    private const double TimeEpsilon = 0.000001;
    private const int DirectCandidates = 32;
    private const int MaxIntervalExpansions = 30000;
    private const int MaxRecoveryExpansions = 6000;
    private double _planningHorizon;
    private Vector2 _intervalTarget;
    private bool _intervalUseHeuristic;
    private bool _allowExposedTerminal;
    private readonly record struct Window(double Start, double End);
    private readonly record struct WindowRange(int Start, int Count);
    private readonly record struct GuideCursor(int Index, int Last, int Direction);
    private readonly record struct TimedTarget(double At, int Phase, bool Launch);
    private enum IntervalStep { Walk, Wait, Displacement }
    private readonly record struct LandingState(Vector2 Position, Window Window, int Phase);
    private struct SafeState(int node, Window window)
    {
        public int Node = node;
        public Window Window = window;
        public double Arrival = double.PositiveInfinity;
        public double Departure;
        public int Parent = -1;
        public int Knockback;
        public Vector2? ExactPosition;
        public IntervalStep Step;
        public int Interval = -1;
        public double Travel, Soft, Margin;
        public float Exposure;
    }

    // Storage survives replans; contacts must not survive a new hint frame.
    private WindowRange[] _nodeIntervals = [];
    private int[] _bestLabels = [];
    private Window?[] _windows = [];
    private bool[] _initialExposure = [];
    private float[] _goalCosts = [];
    private readonly List<SafeState> _states = new();
    private readonly List<Window> _blocks = new();
    private readonly List<Window> _edgeWindows = new();
    private readonly Dictionary<(int From, int To), WindowRange> _edgeRanges = new();
    private readonly Dictionary<(int From, int To), bool> _edgeGeometry = new();
    private readonly PriorityQueue<int, (double Time, int Event, double Estimate)> _intervalQueue = new();
    private readonly Dictionary<LandingState, int> _landings = new();
    private readonly List<TimedTarget> _timedTargets = new();
    private readonly List<Vector2> _guidePositions = new();
    private readonly List<List<int>> _guideSites = new();
    private readonly List<List<WindowRange>> _guideRows = new();
    private readonly HashSet<(Vector2 Position, int Event)> _eventWaits = new();
    private double _lastEvent;
    private readonly PriorityQueue<GuideCursor, (float Distance, int Node)> _shortcutCandidates = new();
    private readonly PriorityQueue<int, (float Goal, float Distance)> _directCandidates = new();
    private readonly List<int> _chain = new();
    private readonly List<EncounterRoutePoint> _routePoints = new();
    public int IntervalExpanded { get; private set; }
    public bool IntervalBudgetReached { get; private set; }

    private void ResetIntervals()
    {
        _states.Clear(); _blocks.Clear(); _edgeWindows.Clear(); _edgeRanges.Clear();
        _edgeGeometry.Clear();
        _intervalQueue.Clear(); _directCandidates.Clear(); _chain.Clear(); _routePoints.Clear();
        _landings.Clear(); _timedTargets.Clear(); _guidePositions.Clear(); _eventWaits.Clear(); _shortcutCandidates.Clear();
        _lastEvent = 0;
        IntervalExpanded = 0; IntervalBudgetReached = false;
    }

    private EncounterRoute? PlanIntervals(EncounterRouteRequest q, CancellationToken token, bool recovery = false)
    {
        ResetIntervals();
        if (CandidateBudgetExpired()) return null;
        var knockbacks = q.Snapshot.Knockbacks;
        int firstKnockback = 0;
        while (firstKnockback < knockbacks.Length && knockbacks[firstKnockback].Source.Activation < q.Now) ++firstKnockback;
        bool displaced = firstKnockback < knockbacks.Length;
        var hasPositionGoal = HasPositionGoal(q);
        // Inside a persistent hazard at the root, a label can walk or wait
        // while still in that same exposure. Crossing back in is a new entry.
        var hasInitialExposure = false;
        _allowExposedTerminal = false;
        if (_initialExposure.Length < q.Snapshot.Hazards.Length)
            Array.Resize(ref _initialExposure, q.Snapshot.Hazards.Length);
        for (var h = 0; h < q.Snapshot.Hazards.Length; ++h)
        {
            var hazard = q.Snapshot.Hazards[h];
            var covered = _distances[h][_count] < 0 && EncounterHintEvaluator.Active(hazard, q.Now, 0);
            if (covered && hazard.Kind == EncounterHazardKind.Impact) return null;
            _initialExposure[h] = covered;
            hasInitialExposure |= covered;
        }
        if (_nodeIntervals.Length != _count + 1) _nodeIntervals = new WindowRange[_count + 1];
        if (_goalCosts.Length != _count + 1) _goalCosts = new float[_count + 1];
        if (_windows.Length < q.Snapshot.Hazards.Length) Array.Resize(ref _windows, q.Snapshot.Hazards.Length);
        for (var h = 0; h < q.Snapshot.Hazards.Length; ++h)
        {
            var zone = q.Snapshot.Hazards[h];
            var t = (zone.Activation - q.Now).TotalSeconds;
            _windows[h] = zone.Activation == DateTime.MaxValue ? null
                : zone.Kind == EncounterHazardKind.Persistent
                    ? new Window(Math.Max(0, t - TimeEpsilon), Math.Min(_planningHorizon, EncounterHintEvaluator.EndSeconds(zone, q.Now) + TimeEpsilon))
                : t < -TimeEpsilon || t > _planningHorizon + TimeEpsilon ? null
                : new Window(Math.Max(0, t - TimeEpsilon), t + TimeEpsilon);
            if (_windows[h] is { } activeWindow && (activeWindow.Start > _planningHorizon || activeWindow.End <= 0 || activeWindow.End < activeWindow.Start)) _windows[h] = null;
        }
        var minimumGoal = float.PositiveInfinity;
        var targetDistance = float.PositiveInfinity;
        var clearEndpointExists = false;
        var reach = _planningHorizon * q.Speed;
        var reachSquared = reach * reach;
        _intervalUseHeuristic = !recovery || _allowUnsafeRecovery;
        _intervalTarget = q.Position;
        for (var i = 0; i <= _count; ++i)
        {
            if ((i & 255) == 0)
            {
                token.ThrowIfCancellationRequested();
                if (CandidateBudgetExpired()) return null;
            }
            var start = _states.Count;
            if (_walk[i])
            {
                _blocks.Clear();
                for (var h = 0; h < q.Snapshot.Hazards.Length; ++h)
                    if (!_initialExposure[h] && _distances[h][i] < 0 && _windows[h] is { } window) _blocks.Add(window);
                MergeBlocks();
                double t = 0;
                foreach (var block in _blocks)
                {
                    if (block.Start > t) AddInterval(q, i, t, block.Start, firstKnockback);
                    t = Math.Max(t, block.End);
                }
                if (t < _planningHorizon) AddInterval(q, i, t, _planningHorizon, firstKnockback);
            }
            _nodeIntervals[i] = new(start, _states.Count - start);
            _goalCosts[i] = !_walk[i] ? float.PositiveInfinity : recovery
                ? Vector2.Distance(q.Position, _positions[i]) / q.Speed + (_allowUnsafeRecovery ? 1000 * _recoveryPenalty[i] : 0)
                : q.Snapshot.Hints is { StackGoals.Length: > 0 }
                    ? EncounterScoring.StackDistance(q, _positions[i])
                    : EncounterScoring.GoalCost(q, _positions[i], lowerBound: true);
            if (_states.Count > start && _states[^1].Window.End == _planningHorizon)
            {
                var distance = Vector2.DistanceSquared(q.Position, _positions[i]);
                if (hasInitialExposure && !clearEndpointExists && distance <= reachSquared)
                    clearEndpointExists = TerminalClear(q, i, _planningHorizon);
                if (!displaced && !TerminalClear(q, i, 0)) continue;
                if (_goalCosts[i] < minimumGoal || _goalCosts[i] == minimumGoal && distance < targetDistance)
                {
                    minimumGoal = _goalCosts[i];
                    targetDistance = distance;
                    _intervalTarget = _positions[i];
                }
            }
        }

        // If no terminal can satisfy all the hazards, preserve every entry
        // restriction and use the original intervals to avoid additional hits.
        // A terminal still covered by the original exposure is incomplete.
        _allowExposedTerminal = hasInitialExposure && !clearEndpointExists && !displaced;

        // Rank cheap endpoint checks first. Bound expensive whole-segment
        // attempts; failure here falls through to the connected-grid search.
        for (var i = 0; !displaced && i <= _count; ++i)
        {
            if ((i & 255) == 0 && CandidateBudgetExpired()) return null;
            var range = _nodeIntervals[i];
            if (range.Count != 1 || _states[range.Start].Window != new Window(0, _planningHorizon) || !TerminalClear(q, i, 0)) continue;
            var distance = Vector2.Distance(q.Position, _positions[i]);
            var arrival = distance / q.Speed + (distance > 0 && EncounterRouteRequest.IsStopped(q.Velocity) ? EncounterRouteRequest.StartupSeconds : 0);
            if (arrival > _planningHorizon) continue;
            if (recovery && !RecoveryTerminal(i, arrival, q.Position)) continue;
            _directCandidates.Enqueue(i, (hasInitialExposure ? 0 : _goalCosts[i], distance));
        }
        EncounterRoute? safeApproach = null;
        for (var attempt = 0; attempt < DirectCandidates && _directCandidates.TryDequeue(out var node, out _); ++attempt)
        {
            token.ThrowIfCancellationRequested();
            if (CandidateBudgetExpired()) return safeApproach;
            var distance = Vector2.Distance(q.Position, _positions[node]);
            var startup = distance > 0 && EncounterRouteRequest.IsStopped(q.Velocity) ? EncounterRouteRequest.StartupSeconds : 0;
            var duration = startup + distance / q.Speed;
            if (duration > _planningHorizon || !q.CanEndAt(_positions[node], _planningHorizon, default)) continue;
            EncounterTransitionResult check = new(true, 0, 0, false), starting = new(true, 0, 0, false);
            if (duration > 0)
            {
                if (!TerrainLegal(q, q.Position, _positions[node])) continue;
                if (startup > 0)
                {
                    starting = AssessInterval(q, q.Position, q.Position, 0, startup);
                    if (!starting.GeometryValid || starting.Hits != 0) continue;
                }
                check = AssessInterval(q, q.Position, _positions[node], startup, duration, startup == 0);
                if (!check.GeometryValid || check.Hits != 0) continue;
            }
            var tail = TerminalAssessment(q, _positions[node], duration);
            if (!tail.GeometryValid) continue;
            var assessment = EncounterScoring.StaticScore(q, _positions[node], Vector2.Distance(q.Position, _positions[node]),
                EncounterScoring.TransitionCost(starting) + EncounterScoring.TransitionCost(check) + EncounterScoring.TransitionCost(tail),
                Math.Max(starting.ImpactMarginShortfall, Math.Max(check.ImpactMarginShortfall, tail.ImpactMarginShortfall)),
                check.Hits + tail.Hits, starting.Exposure + check.Exposure + tail.Exposure) with
                { RecoveryPending = EndsExposed(q, node) };
            var target = _positions[node];
            var command = distance > 0 ? (target - q.Position) / distance * q.Speed : Vector2.Zero;
            var arrivalPoint = new EncounterRoutePoint(target, duration) { CommandVelocity = command };
            EncounterRoutePoint[] points = duration >= _planningHorizon ? [new(q.Position, 0), arrivalPoint]
                : duration > 0 ? [new(q.Position, 0), arrivalPoint, new(target, _planningHorizon)]
                : [new(q.Position, 0), new(q.Position, _planningHorizon)];
            var route = Route(q, EncounterRouteKind.Ready, points, assessment.Failures, (float)assessment.Exposure, 0, "hint-common-safe") with { Assessment = assessment };
            if (!hasPositionGoal || recovery || hasInitialExposure || _goalCosts[node] <= minimumGoal &&
                EncounterScoring.GoalDistance(q, target) == 0) return route;
            // A safe direct approach can still be outside range. Keep it
            // while the connected search tries to get to the actual goal.
            safeApproach ??= route;
        }

        var initial = _nodeIntervals[_count];
        if (initial.Count == 0 || _states[initial.Start].Window.Start != 0) return safeApproach;
        BuildTimedTargets(q, firstKnockback, token);
        var intervalCount = _states.Count;
        if (_bestLabels.Length < intervalCount)
            _bestLabels = new int[Math.Max(intervalCount, _bestLabels.Length * 2)];
        Array.Fill(_bestLabels, -1, 0, intervalCount);
        var root = _states[initial.Start];
        root.Arrival = 0;
        root.Interval = initial.Start;
        AddIntervalLabel(q, root);
        var best = -1;
        var partial = -1;
        var bestGoal = float.PositiveInfinity;
        while (_intervalQueue.TryDequeue(out var id, out _))
        {
            var state = _states[id];
            if (state.Interval >= 0 && _bestLabels[state.Interval] != id) continue;
            if (state.Step == IntervalStep.Displacement && state.ExactPosition is { } exact &&
                _landings[new(exact, state.Window, state.Knockback)] != id) continue;
            if (state.Parent >= 0 && state.Step == IntervalStep.Walk && (partial < 0 || _goalCosts[state.Node] < _goalCosts[_states[partial].Node]
                || _goalCosts[state.Node] == _goalCosts[_states[partial].Node] && state.Arrival > _states[partial].Arrival))
                partial = id;
            if ((++IntervalExpanded & 127) == 0) token.ThrowIfCancellationRequested();
            // All future constraints at this point are over; remaining here
            // is safe through the horizon, including waits after arrival.
            if (state.Knockback == knockbacks.Length && state.Window.End == _planningHorizon
                && TerminalClear(q, state, state.Arrival)
                && (!recovery || RecoveryTerminal(state.Node, state.Arrival, q.Position)))
            {
                var position = IntervalPosition(state);
                var cost = recovery ? _goalCosts[state.Node] : q.Snapshot.Hints is { StackGoals.Length: > 0 }
                    ? EncounterScoring.StackDistance(q, position) : EncounterScoring.GoalCost(q, position);
                if (best < 0 || cost < bestGoal)
                {
                    best = id;
                    bestGoal = cost;
                }
                // Continue through safe cells when a wall blocks the direct
                // attack approach. Stopping at the first safe node would hold
                // forever even though a route around that wall exists.
                if ((!hasPositionGoal && !recovery)
                    || (recovery && !_allowUnsafeRecovery) || bestGoal <= minimumGoal)
                    return BuildIntervalRoute(q, best);
            }
            if (!ConsumeExpansion() || IntervalExpanded >= (recovery ? MaxRecoveryExpansions : MaxIntervalExpansions))
            {
                IntervalBudgetReached = true;
                return best >= 0 ? BuildIntervalRoute(q, best) : safeApproach ?? (partial >= 0 ? BuildIntervalRoute(q, partial, complete: false) : null);
            }
            if (state.Knockback < knockbacks.Length) ExpandKnockback(q, id, state);
            var segment = NextTimedTarget(state);
            if (segment < _guidePositions.Count)
            {
                OfferWait(q, id, state, segment);
                if (state.Step != IntervalStep.Walk || state.Parent < 0 || NextTimedTarget(_states[state.Parent]) != segment)
                    OfferDirectEdges(q, id, state, segment, token);
            }
            if (state.Node == _count || state.ExactPosition.HasValue)
            {
                var position = IntervalPosition(state);
                var (x, z) = q.Grid!.WorldToGrid(new(position.X, 0, position.Y));
                if (!q.Grid.IsFree(x, z)) (x, z) = GridAStar.NearestFree(q.Grid, x, z);
                for (var dz = -2; dz <= 2; ++dz)
                    for (var dx = -2; dx <= 2; ++dx)
                        if ((uint)(x + dx) < _width && (uint)(z + dz) < _height)
                            RelaxInterval(q, id, (z + dz) * _width + x + dx);
            }
            else
                for (var d = 0; d < 8; ++d)
                    if ((_edges[state.Node] & (1 << d)) != 0)
                        RelaxInterval(q, id, state.Node + Directions[d].X + Directions[d].Z * _width);
        }
        return best >= 0 ? BuildIntervalRoute(q, best) : safeApproach ?? (partial >= 0 ? BuildIntervalRoute(q, partial, complete: false) : null);
    }

    private bool TerminalClear(EncounterRouteRequest q, int node, double arrival)
    {
        for (var h = 0; h < q.Snapshot.Hazards.Length; ++h)
            if ((!_allowExposedTerminal || !_initialExposure[h])
                && _windows[h] is { } window && window.End >= arrival && _distances[h][node] < 0) return false;
        return true;
    }

    private bool EndsExposed(EncounterRouteRequest q, int node)
    {
        if (!_allowExposedTerminal) return false;
        for (var h = 0; h < q.Snapshot.Hazards.Length; ++h)
            if (_initialExposure[h] && _distances[h][node] < 0 &&
                EncounterHintEvaluator.Active(q.Snapshot.Hazards[h], q.Now, _planningHorizon)) return true;
        return false;
    }

    private EncounterTransitionResult TerminalAssessment(EncounterRouteRequest q, Vector2 position, double arrival) =>
        arrival >= _planningHorizon ? new(true, 0, 0, false)
            : AssessInterval(q, position, position, arrival, _planningHorizon, includeStartImpacts: arrival == 0);

    private void RelaxInterval(EncounterRouteRequest q, int parent, int node)
    {
        var range = _nodeIntervals[node];
        if (range.Count == 0) return;
        var source = _states[parent];
        var from = IntervalPosition(source);
        var to = _positions[node];
        var distance = Vector2.Distance(from, to);
        var duration = distance / q.Speed;
        if (duration < TimeEpsilon) return;
        bool geometryLegal;
        if (source.ExactPosition.HasValue) geometryLegal = TerrainLegal(q, from, to);
        else if (!_edgeGeometry.TryGetValue((source.Node, node), out geometryLegal))
        {
            geometryLegal = TerrainLegal(q, from, to);
            // Temporary barriers permit escape but forbid re-entry.
            _edgeGeometry.Add((source.Node, node), geometryLegal);
        }
        if (!geometryLegal) return;
        var stopped = source.Step != IntervalStep.Walk || source.Parent < 0 && EncounterRouteRequest.IsStopped(q.Velocity);
        WindowRange? blockedDepartures = null;
        for (var i = range.Start; i < range.Start + range.Count; ++i)
        {
            var dest = _states[i];
            if (dest.Knockback != source.Knockback) continue;
            var previous = _bestLabels[i];
            var latest = previous < 0 ? dest.Window.End : _states[previous].Arrival;
            var earliest = Math.Max(source.Arrival, dest.Window.Start + TimeEpsilon - duration);
            var startup = stopped || earliest > source.Arrival + TimeEpsilon ? EncounterRouteRequest.StartupSeconds : 0;
            if (startup > 0)
                earliest = Math.Max(earliest, source.Arrival + startup + (stopped ? 0 : 2 * TimeEpsilon));
            if (earliest >= source.Window.End || earliest + duration >= latest) continue;

            // A legal immediate crossing needs no contact-field sampling. If it
            // fails, both short and long edges use the same delayed departure.
            var depart = earliest;
            if (!TryWalkAt(depart, out var waiting, out var crossing))
            {
                blockedDepartures ??= EdgeWindows(q, source.Node, node, duration, source.ExactPosition);
                depart = NextOpenDeparture(blockedDepartures.Value, earliest);
                if (depart <= earliest + TimeEpsilon) continue;
                startup = EncounterRouteRequest.StartupSeconds;
                depart = NextOpenDeparture(blockedDepartures.Value,
                    Math.Max(depart, source.Arrival + startup + (stopped ? 0 : 2 * TimeEpsilon)));
                if (depart >= source.Window.End || depart + duration >= latest || !TryWalkAt(depart, out waiting, out crossing)) continue;
            }
            dest.Travel = source.Travel + distance;
            dest.Soft = source.Soft + EncounterScoring.TransitionCost(waiting) + EncounterScoring.TransitionCost(crossing);
            dest.Margin = Math.Max(source.Margin, Math.Max(waiting.ImpactMarginShortfall, crossing.ImpactMarginShortfall));
            dest.Exposure = source.Exposure + waiting.Exposure + crossing.Exposure;
            dest.Arrival = depart + duration;
            dest.Departure = Math.Max(source.Arrival, depart - startup);
            dest.Parent = parent;
            dest.Interval = i;
            AddIntervalLabel(q, dest);
        }

        bool TryWalkAt(double depart, out EncounterTransitionResult waiting, out EncounterTransitionResult crossing)
        {
            crossing = AssessInterval(q, from, to, depart, depart + duration, source.Parent < 0 && depart == 0);
            waiting = depart > source.Arrival
                ? AssessInterval(q, from, from, source.Arrival, depart, source.Parent < 0)
                : new EncounterTransitionResult(true, 0, 0, false);
            return crossing.GeometryValid && crossing.Hits == 0 && waiting.GeometryValid && waiting.Hits == 0;
        }
    }

    private EncounterTransitionResult AssessInterval(EncounterRouteRequest q, Vector2 from, Vector2 to,
        double start, double end, bool includeStartImpacts = true) =>
        EncounterHazardRules.Evaluate(q.Snapshot.Hazards, from, to, start, end, q.Now, includeStartImpacts, _initialExposure);

    private double NextOpenDeparture(WindowRange edges, double earliest)
    {
        for (var i = edges.Start; i < edges.Start + edges.Count; ++i)
        {
            var block = _edgeWindows[i];
            if (earliest < block.Start - TimeEpsilon) break;
            if (earliest <= block.End) earliest = block.End + TimeEpsilon;
        }
        return earliest;
    }

    private Vector2 IntervalPosition(SafeState state) => state.ExactPosition ?? _positions[state.Node];

    private void AddIntervalLabel(EncounterRouteRequest q, SafeState state)
    {
        var label = _states.Count;
        _states.Add(state);
        if (state.Interval >= 0) _bestLabels[state.Interval] = label;
        EnqueueInterval(q, label);
    }

    private void EnqueueInterval(EncounterRouteRequest q, int id)
    {
        var state = _states[id];
        var guide = NextTimedTarget(state);
        var target = guide < _guidePositions.Count ? _guidePositions[guide] : _intervalTarget;
        var needsTarget = guide < _guidePositions.Count || HasPositionGoal(q);
        var estimate = state.Arrival + (_intervalUseHeuristic && needsTarget
            ? Vector2.Distance(IntervalPosition(state), target) / q.Speed : 0);
        // Many states share the same completion bound while events are pending.
        // In that group prefer a state that has already crossed more events.
        _intervalQueue.Enqueue(id, (Math.Max(_lastEvent, estimate), -guide, estimate));
    }

    private static bool HasPositionGoal(EncounterRouteRequest q) =>
        q.Region != null || q.Goal != q.Position ||
        q.Snapshot.Hints is { StackGoals.Length: > 0 } or { Goals.Length: > 0 };

    private int NextTimedTarget(SafeState state)
    {
        for (var i = 0; i < _timedTargets.Count; ++i)
            if (_timedTargets[i].Phase >= state.Knockback && _timedTargets[i].At >= state.Arrival)
                return i;
        return _timedTargets.Count;
    }

    private void BuildTimedTargets(EncounterRouteRequest q, int first, CancellationToken token)
    {
        var phase = first;
        foreach (var time in q.Snapshot.Hazards.Where(h => h.Kind == EncounterHazardKind.Impact)
            .Select(h => (h.Activation - q.Now).TotalSeconds)
            .Where(at => at > TimeEpsilon && at <= _planningHorizon).Distinct().Order())
        {
            while (phase < q.Snapshot.Knockbacks.Length &&
                (q.Snapshot.Knockbacks[phase].Source.Activation - q.Now).TotalSeconds <= time)
                AddLaunch();
            _timedTargets.Add(new(time, phase, false));
        }
        while (phase < q.Snapshot.Knockbacks.Length) AddLaunch();
        if (q.TimingBoundary is { } boundary)
        {
            var at = (boundary.Source.Activation - q.Now).TotalSeconds;
            if (at > 0 && at <= _planningHorizon)
                _timedTargets.Add(new(at, phase, true));
        }

        for (var i = 0; i < _timedTargets.Count; ++i)
        {
            var window = _timedTargets[i];
            _lastEvent = Math.Max(_lastEvent, window.At);
            if (window.Launch)
            {
                var timing = LaunchPrediction(q, window).Timing;
                if (timing.Available) _lastEvent = Math.Max(_lastEvent, window.At + timing.ApplicationDelay + timing.Duration);
            }
            if (i == _guideSites.Count)
            {
                _guideSites.Add(new());
                _guideRows.Add(new());
            }
            var sites = _guideSites[i];
            var rows = _guideRows[i];
            sites.Clear();
            rows.Clear();
            var target = q.Position;
            var best = float.PositiveInfinity;
            for (var row = 0; row < _height; ++row)
            {
                token.ThrowIfCancellationRequested();
                if (CandidateBudgetExpired()) return;
                var start = sites.Count;
                var end = (row + 1) * _width;
                for (var node = row * _width; node < end; ++node)
                {
                    if (!SupportsTarget(q, node, window)) continue;
                    sites.Add(node);
                    var distance = Vector2.DistanceSquared(q.Position, _positions[node]);
                    if (distance < best) { best = distance; target = _positions[node]; }
                }
                if (sites.Count > start) rows.Add(new(start, sites.Count - start));
            }
            _guidePositions.Add(target);
        }

        void AddLaunch()
        {
            var at = (q.Snapshot.Knockbacks[phase].Source.Activation - q.Now).TotalSeconds;
            _timedTargets.Add(new(at, phase++, true));
        }
    }

    private bool SupportsTarget(EncounterRouteRequest q, int node, TimedTarget target)
    {
        var range = _nodeIntervals[node];
        for (var i = range.Start; i < range.Start + range.Count; ++i)
        {
            var state = _states[i];
            if (state.Knockback != target.Phase || state.Window.Start >= target.At ||
                (target.Launch ? state.Window.End < target.At : state.Window.End <= target.At)) continue;
            if (!target.Launch) return true;
            var prediction = LaunchPrediction(q, target);
            var landing = EncounterDisplacement.Project(q.Snapshot, prediction, _positions[node], default);
            return prediction.AllowsLanding(landing) &&
                q.Snapshot.Arena.Contains(new WPos(landing.X, landing.Y) - q.Snapshot.ArenaCenter);
        }
        return false;
    }

    private static KnockbackPrediction LaunchPrediction(EncounterRouteRequest q, TimedTarget target) =>
        target.Phase < q.Snapshot.Knockbacks.Length ? q.Snapshot.Knockbacks[target.Phase] : q.TimingBoundary!.Value;

    private void OfferDirectEdges(EncounterRouteRequest q, int parent, SafeState source, int guide, CancellationToken token)
    {
        var from = IntervalPosition(source);
        var reach = (_timedTargets[guide].At - source.Arrival) * q.Speed;
        if (reach <= 0) return;
        _shortcutCandidates.Clear();
        var reachSquared = reach * reach;
        var sites = _guideSites[guide];
        var rows = _guideRows[guide];
        // Each row is sorted by X. Merge only its nearest candidates on either
        // side of the origin, so repeated waits never sort all the guide sites.
        for (var i = 0; i < rows.Count; ++i)
        {
            if ((i & 31) == 0)
            {
                token.ThrowIfCancellationRequested();
                if (CandidateBudgetExpired()) return;
            }
            var row = rows[i];
            if (MathF.Abs(_positions[sites[row.Start]].Y - from.Y) >= reach) continue;
            var lo = row.Start;
            var hi = row.Start + row.Count;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (_positions[sites[mid]].X < from.X) lo = mid + 1;
                else hi = mid;
            }
            Enqueue(lo - 1, row.Start, -1);
            Enqueue(lo, row.Start + row.Count - 1, 1);
        }
        for (var attempt = 0; attempt < DirectCandidates && _shortcutCandidates.TryDequeue(out var head, out var rank);)
        {
            if (CandidateBudgetExpired()) return;
            Enqueue(head.Index + head.Direction, head.Last, head.Direction);
            if (rank.Distance < TimeEpsilon) continue;
            ++attempt;
            RelaxInterval(q, parent, rank.Node);
        }

        void Enqueue(int index, int last, int direction)
        {
            if (direction < 0 ? index < last : index > last) return;
            var node = sites[index];
            var distance = Vector2.DistanceSquared(from, _positions[node]);
            if (distance < reachSquared)
                _shortcutCandidates.Enqueue(new(index, last, direction), (distance, node));
        }
    }

    private void OfferWait(EncounterRouteRequest q, int parent, SafeState source, int guide)
    {
        var target = _timedTargets[guide];
        var release = target.At + 2 * TimeEpsilon;
        if (target.Launch || release >= source.Window.End || source.Arrival >= release) return;
        var position = IntervalPosition(source);
        var wait = AssessInterval(q, position, position, source.Arrival, release, source.Parent < 0);
        if (!wait.GeometryValid || wait.Hits != 0 || !_eventWaits.Add((position, guide))) return;
        AddIntervalLabel(q, source with
        {
            Arrival = release, Departure = source.Arrival, Parent = parent, Step = IntervalStep.Wait,
            Interval = -1, Soft = source.Soft + EncounterScoring.TransitionCost(wait),
            Exposure = source.Exposure + wait.Exposure, Margin = Math.Max(source.Margin, wait.ImpactMarginShortfall)
        });
    }

    private WindowRange EdgeWindows(EncounterRouteRequest q, int from, int to, double duration, Vector2? landing)
    {
        if (!landing.HasValue && _edgeRanges.TryGetValue((from, to), out var cached)) return cached;
        _blocks.Clear();
        var a = landing ?? _positions[from]; var b = _positions[to];
        var length = Vector2.Distance(a, b);
        var steps = Math.Max(2, (int)MathF.Ceiling(length / .0625f));
        for (var h = 0; h < q.Snapshot.Hazards.Length; ++h)
        {
            if (_windows[h] is not { } window) continue;
            var hazard = q.Snapshot.Hazards[h];
            var shape = hazard.Shape;
            var da = landing.HasValue ? EncounterHazardRules.Clearance(hazard, a) : _distances[h][from];
            var db = _distances[h][to];
            var exiting = _initialExposure[h] && da < 0;
            if (shape is SDHalfPlane)
            {
                if (exiting || da >= 0 && db >= 0) continue;
                double enter = 0, leave = 1;
                if (da >= 0) enter = da / (da - db);
                else if (db >= 0) leave = da / (da - db);
                _blocks.Add(new(window.Start - leave * duration, window.End - enter * duration));
                continue;
            }
            if (shape is SDCircle or SDDonut)
            {
                // Only these verified distance fields use a Lipschitz bound.
                if (da > length + .0001f || db > length + .0001f) continue;
                if (da < -length - .0001f || db < -length - .0001f)
                {
                    if (!exiting) _blocks.Add(new(window.Start - duration, window.End));
                    continue;
                }
            }
            var inside = da < 0;
            double enterAt = 0;
            for (var step = 1; step <= steps; ++step)
            {
                var f = (double)step / steps;
                var next = Distance(hazard, a, b, f) < 0;
                if (next != inside)
                {
                    var low = (double)(step - 1) / steps; var high = f;
                    for (var iteration = 0; iteration < 15; ++iteration)
                    {
                        var mid = (low + high) * .5;
                        if ((Distance(hazard, a, b, mid) < 0) == inside) low = mid;
                        else high = mid;
                    }
                    if (inside)
                    {
                        if (!exiting) _blocks.Add(new(window.Start - high * duration, window.End - enterAt * duration));
                        exiting = false;
                    }
                    else enterAt = low;
                }
                inside = next;
            }
            if (inside && !exiting) _blocks.Add(new(window.Start - duration, window.End - enterAt * duration));
        }
        MergeBlocks();
        var result = new WindowRange(_edgeWindows.Count, _blocks.Count);
        _edgeWindows.AddRange(_blocks);
        if (!landing.HasValue) _edgeRanges.Add((from, to), result);
        return result;
    }

    private static float Distance(in EncounterHazard hazard, Vector2 a, Vector2 b, double fraction)
    {
        var p = Vector2.Lerp(a, b, (float)fraction);
        var d = EncounterHazardRules.Clearance(hazard, p);
        return float.IsFinite(d) ? d : float.NegativeInfinity;
    }

    private void MergeBlocks()
    {
        _blocks.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        var used = 0;
        for (var i = 0; i < _blocks.Count; ++i)
        {
            var item = _blocks[i];
            if (used > 0 && item.Start <= _blocks[used - 1].End + TimeEpsilon)
                _blocks[used - 1] = new(_blocks[used - 1].Start, Math.Max(_blocks[used - 1].End, item.End));
            else _blocks[used++] = item;
        }
        if (used < _blocks.Count) _blocks.RemoveRange(used, _blocks.Count - used);
    }

    private EncounterRoute BuildIntervalRoute(EncounterRouteRequest q, int goal, bool complete = true)
    {
        _chain.Clear(); _routePoints.Clear();
        for (var id = goal; id >= 0; id = _states[id].Parent) _chain.Add(id);
        _routePoints.Add(new(q.Position, 0));
        for (var index = _chain.Count - 2; index >= 0; --index)
        {
            var state = _states[_chain[index]];
            if (state.Departure > _routePoints[^1].Seconds)
                _routePoints.Add(new(_routePoints[^1].Position, state.Departure));
            var target = IntervalPosition(state);
            if (state.Step != IntervalStep.Walk)
            {
                _routePoints.Add(new(target, state.Arrival)
                    { GameDriven = state.Step == IntervalStep.Displacement, CommandVelocity = Vector2.Zero });
                continue;
            }
            var command = Vector2.Normalize(target - _routePoints[^1].Position) * q.Speed;
            _routePoints.Add(new(target, state.Arrival) { CommandVelocity = command });
        }
        if (complete && _routePoints[^1].Seconds < _planningHorizon)
            _routePoints.Add(new(_routePoints[^1].Position, _planningHorizon));
        var final = _states[goal];
        var endpoint = IntervalPosition(final);
        var tail = TerminalAssessment(q, endpoint, final.Arrival);
        var assessment = EncounterScoring.StaticScore(q, endpoint, final.Travel,
            final.Soft + EncounterScoring.TransitionCost(tail), Math.Max(final.Margin, tail.ImpactMarginShortfall), tail.Hits, final.Exposure + tail.Exposure) with { RecoveryPending = EndsExposed(q, final.Node) };
        return Route(q, EncounterRouteKind.Ready, _routePoints.ToArray(), tail.Hits, (float)assessment.Exposure, IntervalExpanded,
            complete ? "hint-safe-interval" : "hint-safe-prefix") with { Complete = complete, Assessment = assessment };
    }
}
