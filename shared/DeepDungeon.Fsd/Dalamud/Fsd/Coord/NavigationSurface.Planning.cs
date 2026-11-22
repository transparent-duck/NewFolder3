using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Coord;

internal sealed partial class NavigationSurface
{
    private sealed record PendingPlan(long Id, int Generation, NavigationRequest Request,
        Vector2 PreviousDirection, CancellationTokenSource Cancellation, Task<PlanCompletion> Work);

    private readonly NavigationPlanner _backgroundPlanner = new();
    private int _backgroundGeneration = -1;
    private PendingPlan? _pendingPlan;
    private Vector2? _lastAttackApproachGoal;
    private NavBitmap? _observedMap;
    private INavigationGrid? _currentTerrain;
    private IReadOnlyList<Vector3> _rawPath = [];
    private PlannerDiagnostics _planDiagnostics;

    private void InvalidatePlanning(DateTime nextReplan)
    {
        ++_generation;
        _nextReplan = nextReplan;
        _pendingPlan?.Cancellation.Cancel();
        _lastAttackApproachGoal = null;
        _regionPlanner.Reset();
        _currentTerrain = null;
        _rawPath = [];
        _planDiagnostics = default;
    }

    private void StartBackgroundPlan(NavigationRequest request)
    {
        // FSD snapshots own their forecast arrays and Deep Dungeon goal closures
        // capture geometry or values. The duty grid is replaced rather than edited.
        // The live follower is the mutable input that needs a per-request copy.
        request = request with { Follower = _follower.CopyForPrediction() };
        var generation = _generation;
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var work = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            if (_backgroundGeneration != generation)
            {
                _backgroundPlanner.ResetRoute();
                _backgroundGeneration = generation;
            }
            return MeasurePlan(_backgroundPlanner, request, token);
        }, token);
        _pendingPlan = new(++_planSequence, generation, request, _lastDirection, cancellation, work);
    }

    private void CompleteBackgroundPlan(DateTime now, float speed, EncounterPlanningSnapshot? snapshot, bool canApproach)
    {
        if (_pendingPlan is not { Work.IsCompleted: true } pending) return;
        _pendingPlan = null;
        pending.Cancellation.Dispose();
        try
        {
            var result = pending.Work.GetAwaiter().GetResult();
            var route = result.Route;
            bool attackApproach = pending.Request.AttackApproachTarget != 0;
            var decision = pending.Generation != _generation ? "request-replaced"
                : attackApproach && (!canApproach || _activeRegion?.TargetId != pending.Request.AttackApproachTarget ||
                    _route != null || _activeRegion is not { Holding: false, Unreachable: false, Path: null }) ? "approach-replaced"
                : route.Kind == EncounterRouteKind.NoRoute
                    ? route.Reason == "hint-budget-exhausted" ? "budget-exhausted" : "no-route"
                : attackApproach && !AttackApproachMakesProgress(route, new(_goal.Position.X, _goal.Position.Z)) ? "approach-outdated"
                : CheckHandoff(pending.Request, route, speed, snapshot, result.Terrain);
            RecordPlan(pending.Id, pending.Generation, pending.Request, pending.PreviousDirection, result, decision, DateTime.UtcNow);
            if (attackApproach && decision != "adopted") return;
            if (decision is "adopted" or "no-route" or "budget-exhausted")
                AdoptPlan(pending.Id, pending.Request, result);
            else if (decision != "request-replaced")
            {
                _failure = decision;
                _nextReplan = now;
                if (decision == "displacement-changed") DropRoute();
            }
        }
        catch (OperationCanceledException) when (pending.Generation != _generation)
        {
            RecordPlan(pending.Id, pending.Generation, pending.Request, pending.PreviousDirection, null, "cancelled", now);
        }
        catch (Exception ex)
        {
            if (pending.Generation == _generation) PlanFault(pending.Id, pending.Request, ex);
            else _record("navigation-plan-fault", new { planId = pending.Id, error = ex.ToString() });
        }
    }

    private bool AttackApproachMakesProgress(EncounterRoute route, Vector2 target) =>
        route.Points.Length > 1 &&
        Vector2.DistanceSquared(route.Points[^1].Position, target) < Vector2.DistanceSquared(_observed.Position, target);

    private string CheckHandoff(in NavigationRequest request, EncounterRoute route,
        float speed, EncounterPlanningSnapshot? snapshot, INavigationGrid? terrain)
    {
        var before = request.Observation?.Displacement ?? request.Encounter?.Displacement;
        var current = _observed.Displacement ?? snapshot?.Displacement;
        if (before.HasValue != current.HasValue || before?.GlobalSequence != current?.GlobalSequence
            || before?.ActionId != current?.ActionId
            || before?.Target != current?.Target || before?.Native != current?.Native)
            return "displacement-changed";

        var follower = _follower.CopyForPrediction();
        follower.Adopt(route);
        var command = follower.Step(_observed);
        if (command.Kind == NavigationCommandKind.Replan) return "mechanic-handoff-passed";
        var plannedSnapshot = ReferenceEquals(route, _route) ? _routeSnapshot : request.Encounter;
        if (!NavigationPlanner.CanStart(route, _observed, command, snapshot, terrain, speed, ReplanInterval.TotalSeconds, plannedSnapshot))
            return "route-start-changed";
        return "adopted";
    }

    private void PlanRegion(in NavigationRequest request)
    {
        var id = ++_planSequence;
        try
        {
            // Region goals still call native ground and sight queries on this thread.
            var result = MeasurePlan(_regionPlanner, request, CancellationToken.None);
            RecordPlan(id, _generation, request, _lastDirection, result,
                result.Route.Kind == EncounterRouteKind.Ready ? "adopted" : "no-route", DateTime.UtcNow);
            AdoptPlan(id, request, result);
        }
        catch (Exception ex) { PlanFault(id, request, ex); }
    }

    private void AdoptPlan(long id, in NavigationRequest request, PlanCompletion result)
    {
        var route = result.Route;
        // The approach and the precise search belong to the same attack claim.
        // A search still in progress does not revoke an already safe approach.
        if (request.Region != null && _route is { Reason: "attack-approach" } &&
            route.Reason is "region-search" or "region-path-disconnected") return;
        _planDiagnostics = result.Diagnostics;
        _currentTerrain = result.Terrain;
        _rawPath = result.RawPath;
        if (route.Kind != EncounterRouteKind.Ready)
        {
            _failure = route.Reason;
            if (request.Region != null || request.Encounter is { UsesDutyTerrain: true }) DropRoute();
            return;
        }
        _failure = null;
        if (!ReferenceEquals(_route, route))
        {
            _route = route;
            _routeSnapshot = request.Encounter;
            _follower.Adopt(route);
            _routeIndex = 0;
            _displayPath.Clear();
            if (request.Region == null)
            {
                int count = route.Points.Length;
                if (_remainingAtPoint.Length < count) _remainingAtPoint = new float[count];
                if (count > 0) _remainingAtPoint[count - 1] = 0;
                for (var i = count - 2; i >= 0; i--)
                    _remainingAtPoint[i] = _remainingAtPoint[i + 1] +
                        Vector2.Distance(route.Points[i].Position, route.Points[i + 1].Position);
            }
            foreach (var point in route.Points) _displayPath.Add(new(point.Position.X, request.PlayerHeight, point.Position.Y));
        }
        _adoptedPlanId = id;
    }

    private void PlanFault(long id, in NavigationRequest request, Exception error)
    {
        _failure = error.Message;
        if (request.Region != null || request.Encounter is { UsesDutyTerrain: true }) DropRoute();
        _record("navigation-plan-fault", new { planId = id, error = error.ToString() });
    }

    private void DrainOnDispose()
    {
        if (_pendingPlan is not { } pending) return;
        _pendingPlan = null;
        _ = pending.Work.ContinueWith(task =>
        {
            _ = task.Exception;
            pending.Cancellation.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
