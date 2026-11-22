using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace MasterPlayer.MechanicRuntime;

public sealed partial class EncounterHintPlanner
{
    private const int RecoveryAttempts = 4;
    private double[] _recoverySafeFrom = [];
    private float[] _recoveryPenalty = [];
    private bool _allowUnsafeRecovery;
    private readonly List<int> _recoverySources = new();

    // Relaxation only generates candidates. It never changes the hazard facts
    // used to score a returned route. Prefer an original-safe terminal; if none
    // is in reach, still move toward less exposure and report its violations.
    private EncounterRoute? PlanRecoveryIntervals(EncounterRouteRequest q, CancellationToken token)
    {
        if (CandidateBudgetExpired()) return null;
        var hazards = q.Snapshot.Hazards;
        if (hazards.Length == 0) return null;
        if (_recoverySafeFrom.Length != _count + 1) _recoverySafeFrom = new double[_count + 1];
        if (_recoveryPenalty.Length != _count + 1) _recoveryPenalty = new float[_count + 1];
        Array.Clear(_recoverySafeFrom);
        Array.Clear(_recoveryPenalty);
        var relaxed = new bool[hazards.Length];
        _recoverySources.Clear();
        var hasRelaxation = false;
        for (var h = 0; h < hazards.Length; ++h)
        {
            if (CandidateBudgetExpired()) return null;
            var hazard = hazards[h];
            var activation = (hazard.Activation - q.Now).TotalSeconds;
            if (hazard.Activation == DateTime.MaxValue || activation > _planningHorizon
                || EncounterHintEvaluator.EndSeconds(hazard, q.Now) <= 0
                || hazard.Kind == EncounterHazardKind.Impact && activation < -TimeEpsilon) continue;
            _recoverySources.Add(h);
            for (var node = 0; node <= _count; ++node)
            {
                if ((node & 255) == 0) token.ThrowIfCancellationRequested();
                if (_distances[h][node] < 0)
                {
                    _recoverySafeFrom[node] = hazard.Kind == EncounterHazardKind.Persistent
                        ? Math.Max(_recoverySafeFrom[node], EncounterHintEvaluator.EndSeconds(hazard, q.Now) + 2 * TimeEpsilon)
                        : Math.Max(_recoverySafeFrom[node], activation + 2 * TimeEpsilon);
                    _recoveryPenalty[node] -= _distances[h][node];
                }
            }
            // Every source covering the start now must be relaxed together;
            // otherwise the ordinary SIPP root still has no safe interval.
            if (_distances[h][_count] < 0 && EncounterHintEvaluator.Active(hazard, q.Now, 0))
            {
                RelaxRecoverySource(hazards, relaxed, h);
                hasRelaxation = true;
            }
        }
        _recoverySources.Sort((a, b) =>
        {
            var time = hazards[b].Activation.CompareTo(hazards[a].Activation);
            return time != 0 ? time : a.CompareTo(b);
        });
        EncounterRoute? best = null;
        _allowUnsafeRecovery = true;
        for (var node = 0; node < _count; ++node)
            if (_walk[node] && _recoverySafeFrom[node] < _planningHorizon
                && Vector2.Distance(q.Position, _positions[node]) <= q.Speed * _planningHorizon)
            { _allowUnsafeRecovery = false; break; }
        var expanded = IntervalExpanded;
        var budgetReached = IntervalBudgetReached;
        try
        {
            for (var attempt = 0; attempt < RecoveryAttempts; ++attempt)
            {
                token.ThrowIfCancellationRequested();
                if (CandidateBudgetExpired()) break;
                if (attempt > 0 || !hasRelaxation)
                {
                    var next = -1;
                    foreach (var h in _recoverySources)
                        if (!relaxed[h]) { next = h; break; }
                    if (next < 0)
                    {
                        if (best != null || _allowUnsafeRecovery) break;
                        _allowUnsafeRecovery = true;
                    }
                    else if (attempt == RecoveryAttempts - 1)
                    {
                        // Last bounded candidate permits unavoidable crossings.
                        foreach (var h in _recoverySources) relaxed[h] = true;
                        _allowUnsafeRecovery |= best == null;
                    }
                    else RelaxRecoverySource(hazards, relaxed, next);
                }
                var remaining = 0;
                for (var h = 0; h < hazards.Length; ++h) if (!relaxed[h]) ++remaining;
                var candidateHazards = new EncounterHazard[remaining];
                for (int h = 0, at = 0; h < hazards.Length; ++h)
                    if (!relaxed[h]) candidateHazards[at++] = hazards[h];
                var candidateRequest = q with { Snapshot = q.Snapshot with { Hazards = candidateHazards } };
                if (!Prepare(candidateRequest)) break;
                var candidate = PlanIntervals(candidateRequest, token, recovery: true);
                expanded += IntervalExpanded;
                budgetReached |= IntervalBudgetReached;
                if (candidate == null) continue;
                // Relaxed intervals only propose a path. Evaluate that new
                // candidate once against the original branch-state rules.
                var original = new EncounterForwardProblem(q, 0, _routeHorizon, token);
                var controls = ToControls(candidate.Points, original);
                var score = original.Evaluate(controls.Velocities, controls.Ends);
                if (score.GeometryFailures != 0) continue;
                if (best?.Assessment is { } previousScore && score.CompareTo(previousScore) >= 0) continue;
                best = candidate with { PredictedHits = score.Failures, Exposure = (float)score.Exposure,
                    Assessment = score,
                    Reason = _allowUnsafeRecovery ? "hint-interval-least-exposure" : "hint-safe-interval-recovery" };
                if (score.Failures == 0 && !score.RecoveryPending) break;
            }
        }
        finally
        {
            // Each following search calls Prepare with its own source array.
            // Do not start more geometry work once this plan's budget expired.
            if (!CandidateBudgetExpired()) Prepare(q);
            IntervalExpanded = expanded;
            IntervalBudgetReached = budgetReached;
            _allowUnsafeRecovery = false;
        }
        return best is { } route ? route with { Expanded = expanded } : null;
    }

    private bool RecoveryTerminal(int node, double arrival, Vector2 start) =>
        Vector2.DistanceSquared(start, _positions[node]) > .0001f
        && (_allowUnsafeRecovery || arrival >= _recoverySafeFrom[node]);

    private static void RelaxRecoverySource(EncounterHazard[] hazards, bool[] relaxed, int selected)
    {
        var source = hazards[selected].SourceId;
        for (var h = 0; h < hazards.Length; ++h)
            if (h == selected || source != 0 && hazards[h].SourceId == source && hazards[h].Activation == hazards[selected].Activation) relaxed[h] = true;
    }
}
