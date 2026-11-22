using System.Numerics;
using BossMod;
namespace MasterPlayer.MechanicRuntime;

// Ordinary duty navigation only controls the character during a boss telegraph.
public sealed class BossDodgeNavigation
{
    private readonly NavigationPlanner _planner = new();
    private readonly NavigationFollower _follower = new();
    private EncounterPlanningSnapshot? _planned;
    private DateTime _plannedAt;
    public bool TraceRecovery { get; init; }
    public EncounterRoute? LastDecision { get; private set; }
    public string? RouteReason => LastDecision?.Reason;
    public bool HasRoute => LastDecision?.Kind == EncounterRouteKind.Ready;

    public static bool HasMovementHints(EncounterPlanningSnapshot s) =>
        s.Hazards.Length != 0 || s.Knockbacks.Length != 0 || s.ChasingHazards.Length != 0
        || s.PathMechanic != null || s.Hints is { KeepMoving: true } or { Goals.Length: > 0 }
        or { Mode: not AIHints.SpecialMode.Normal };

    public void Reset()
    {
        if (_planned == null) return;
        _planned = null;
        _planner.Reset();
        _follower.Reset();
        LastDecision = null;
    }

    public NavigationCommand Step(EncounterPlanningSnapshot? snapshot, in NavigationObservation observation,
        ulong playerId, float height, float speed, INavigationGrid? terrain = null)
    {
        var pos = observation.Position;
        var now = observation.At;
        if (snapshot == null || !HasMovementHints(snapshot)
            || snapshot.BossDead || observation.MovementLocked
            || now < snapshot.CapturedAt || (now - snapshot.CapturedAt).TotalSeconds > .5
            || !snapshot.Arena.Contains(new WPos(pos.X, pos.Y) - snapshot.ArenaCenter))
        {
            Reset();
            return new(NavigationCommandKind.Wait, pos);
        }
        if (_planned?.EncounterId != snapshot.EncounterId) Reset();
        if (!ReferenceEquals(_planned, snapshot) || (now - _plannedAt).TotalMilliseconds >= 100)
        {
            var route = _planner.Plan(new(now, pos, pos, NavigationFollower.Arrival, speed, terrain, snapshot)
            {
                TraceRecovery = TraceRecovery, PlayerId = playerId, PlayerHeight = height, Observation = observation,
                Continuation = _follower.Plan, ContinuationIndex = _follower.Index, Follower = _follower
            }, default);
            _planned = snapshot;
            _plannedAt = now;
            LastDecision = route;
            _follower.Adopt(route);
        }
        return _follower.Step(observation);
    }
}
