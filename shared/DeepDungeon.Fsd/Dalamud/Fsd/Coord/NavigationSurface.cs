using System.Numerics;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Utilities;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>One observation, planner and follower for every navigation request.</summary>
internal sealed partial class NavigationSurface : INavSurface, IDisposable
{
    private static readonly TimeSpan ReplanInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromMilliseconds(500);
    private readonly Func<NavBitmap?> _mapProvider;
    private readonly Func<EncounterPlanningSnapshot?> _snapshotProvider;
    private readonly Func<ulong> _playerId;
    private readonly Func<float> _movementSpeed;
    private readonly Action<string, object> _record;
    private readonly DirectMovementController _direct = new("navigation");
    private readonly NavigationPlanner _regionPlanner = new();
    private readonly NavigationFollower _follower = new();
    private readonly NativeNavigationObservation _observation = new();
    private readonly List<Vector3> _displayPath = new();
    private float[] _remainingAtPoint = [];
    private NavigationObservation _observed;
    private EncounterPlanningSnapshot? _snapshot;
    private EncounterPlanningSnapshot? _completedTerrain;
    private EncounterRoute? _route;
    private EncounterPlanningSnapshot? _routeSnapshot;
    private NavigationPath? _preparedPath;
    private DateTime _nextReplan;
    private int _generation, _routeIndex;
    private Vector2 _lastDirection;
    private DateTime _lastDiag;
    private string _lastDiagState = "";
    private string? _failure;
    private bool _disposed, _displacementHeld, _inputLocked;
    private NavigationGoal _goal;
    private Vector3 _commandedTarget;

    internal NavigationSurface(Func<NavBitmap?> mapProvider, Action<string, object>? record,
        Func<EncounterPlanningSnapshot?> snapshotProvider,
        Func<ulong>? playerId = null, Func<float>? movementSpeed = null)
    {
        _mapProvider = mapProvider;
        _record = record ?? ((_, _) => { });
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _playerId = playerId ?? (() => (ulong)(Service.LocalPlayer?.GameObjectId ?? 0));
        _direct.ObserveUserInput = true;
        _movementSpeed = movementSpeed ?? (() => DirectMovementController.TryReadNativeMotion(out var m)
            ? m.TargetSpeed > 0 ? m.TargetSpeed : m.BaseSpeed > 0 ? m.BaseSpeed : 6 : 6);
        _direct.ClampFinalStep = false;
        _direct.AfterWalkInput = OnNativeWrite;
    }
    internal bool NeedsSafetyPlan => MechanicExecutionState.MovementBlocked
        || _snapshotProvider() is { RequiresTimedPlanning: true, BossDead: false } snap
        && snap.Territory == (ushort)Service.ClientState.TerritoryType;

    internal string? Failure => _failure;
    internal string Status => _lastDiagState;
    internal string Solver => _planDiagnostics.Solver ?? "none";
    internal NavBitmap? CurrentBitmap => _mapProvider();
    internal INavigationGrid? CurrentTerrain => _currentTerrain;
    internal IReadOnlyList<Vector3> RawPath => _rawPath;
    internal IReadOnlyList<Vector3> SmoothedPath => _displayPath;
    internal int LegIndex => Math.Max(0, _follower.Index - 1);
    internal Vector3 CommandedTarget => _commandedTarget;
    internal Vector3 FinalDestination => _goal.Position;
    internal float ArrivalRadius => _goal.Radius;
    internal bool InputEnabled => _direct.Enabled;
    internal bool PlanningPending => _pendingPlan != null;

    internal float? RemainingDistance(Vector3 destination, float arrivalRadius, Vector3 player)
    {
        if (_activeRegion != null || destination != _goal.Position || arrivalRadius != _goal.Radius ||
            _route is not { Points.Length: > 0 } route) return null;
        int index = Math.Min(_follower.Index + 1, route.Points.Length - 1);
        var from = new Vector2(player.X, player.Z);
        var next = route.Points[index].Position;
        if (index == 0) return Vector2.Distance(from, next) + _remainingAtPoint[index];
        var leg = route.Points[index - 1].Position - next;
        var length = leg.Length();
        var left = length > 0 ? Math.Clamp(Vector2.Dot(from - next, leg) / length, 0, length) : 0;
        return left + _remainingAtPoint[index];
    }

    internal Action? AttackSuspended { get; init; }
    private NavigationRegionGoal? _attackRegion;
    internal NavigationRegionGoal AttackRegion => _attackRegion ??= AttackRegionGoal.Create(
        (map, seed) => CombatPositionNavigation.BitmapGroundPoint(map, seed), CheckAttackStand);

    private long _attackOutOfRange, _attackSightBlocked, _attackSightUnknown, _attackStandClear;

    private bool? CheckAttackStand(Vector3 stand, Vector3 target, float range, float clearance)
    {
        var check = CombatPositionNavigation.CheckAttack(stand, target, range, margin: clearance);
        if (!check.InRange) ++_attackOutOfRange;
        else if (check.SightBlocked) ++_attackSightBlocked;
        else if (check.Allowed) ++_attackStandClear;
        else ++_attackSightUnknown;
        return check.AvailableResult;
    }

    private NavigationRegionGoal? _activeRegion;
    private NavigationRegionGoal? _chestRegion;

    internal NavigationState NavigateToAttack(ulong id, Vector3 target, Vector3 player, float range)
    {
        var ranges = DeepDungeon.Fsd.Core.CombatChaseRanges.ForAction(range);
        return NavigateRegion(AttackRegion, id, target, player, ranges.Approach, ranges.Hold);
    }

    internal NavigationState NavigateToChest(ChestTargetVerb chest, Vector3 player,
        out NavigationRegionExhaustion? exhaustion)
    {
        _chestRegion ??= new((map, seed) => CombatPositionNavigation.BitmapGroundPoint(map, seed),
            CanInteractFrom);
        var result = NavigateRegion(_chestRegion, chest.TargetId, chest.Position, player,
            chest.Range.Radius, chest.Range.Radius);
        exhaustion = _chestRegion.Exhaustion is { } exhausted &&
            exhausted.AppliesTo(_mapProvider(), player, chest.Position, CombatPositionNavigation.CurrentYOffset)
            ? exhausted : null;
        return result;
    }

    private static bool? CanInteractFrom(Vector3 stand, Vector3 target, float radius, float clearance)
    {
        var range = new DeepDungeon.Fsd.Core.NavigationRange(radius);
        if (!(clearance == 0 ? range.Contains(stand, target) : range.CanStandAt(stand, target)))
            return false;
        return CombatPositionNavigation.Sight(stand, target, out _) switch
        {
            CombatSight.Clear => true,
            CombatSight.Blocked => false,
            _ => null
        };
    }

    private NavigationState NavigateRegion(NavigationRegionGoal region, ulong id, Vector3 target,
        Vector3 player, float approach, float hold)
    {
        if (_activeRegion != region || region.TargetId != id) Cancel();
        _activeRegion = region;
        region.Observe(id, target, approach, hold, CombatPositionNavigation.CurrentYOffset);
        return NavigateCore(new(target, approach), player, null, region);
    }

    public NavigationState Navigate(Vector3 dest, Vector3 playerPos, float arrivalRadius, NavigationPath? path = null)
    {
        if (_activeRegion != null) Cancel();
        return NavigateCore(new(dest, arrivalRadius), playerPos, path, null);
    }

    internal NavigationState PositionForEncounter(ulong encounterId, Vector3 position, Vector3 playerPos, float attackRange)
    {
        if (_activeRegion != null) Cancel();
        return NavigateCore(new(position, attackRange, encounterId), playerPos, null, null);
    }

    private NavigationState NavigateCore(NavigationGoal goal, Vector3 playerPos,
        NavigationPath? path, NavigationRegionGoal? region)
    {
        if (_disposed) return NavigationState.Failed;
        var (dest, arrivalRadius, _) = goal;
        var now = DateTime.UtcNow;
        var speed = _movementSpeed();
        CaptureDiagnosticContext(now, dest, playerPos, arrivalRadius, speed);
        var snap = _snapshotProvider();
        if (snap?.Territory != (ushort)Service.ClientState.TerritoryType) snap = null;
        if (snap is { BossDead: true })
        {
            // The encounter has ended, but its terrain still connects the player to the exit.
            if (!snap.Arena.Contains(new BossMod.WPos(playerPos.X, playerPos.Z) - snap.ArenaCenter)) snap = null;
            else
            {
                if (_completedTerrain?.EncounterId != snap.EncounterId)
                    _completedTerrain = snap with { Hazards = [], MovingObstacles = [], Chasers = [],
                        ChasingHazards = [], Knockbacks = [], Displacement = null, TemporaryObstacles = [],
                        Residence = null, Hints = null };
                snap = _completedTerrain;
            }
        }
        else if (snap != null) _completedTerrain = null;
        _diagnosticSnapshot = snap;
        if (snap is { BossDead: false } && now - snap.CapturedAt > MaxSnapshotAge)
        { InvalidatePlanning(now); ReleaseDirect(); Diag(now, "waiting-snapshot"); return NavigationState.StuckRepathing; }
        if (snap?.EncounterId != _snapshot?.EncounterId || snap?.Territory != _snapshot?.Territory
            || !ReferenceEquals(snap?.Arena, _snapshot?.Arena))
        {
            InvalidatePlanning(now);
            // On the first hazard frame the timed solver can assess the remaining
            // spatial route against the new hazards before searching for a detour.
            if (!(snap is { UsesDutyTerrain: true } && _snapshot == null && _route is { Reason: "astar" }))
                DropRoute();
        }
        var constraintsChanged = snap != null && !snap.HasSameMovementConstraints(_snapshot);
        _snapshot = snap;
        var held = MechanicExecutionState.MovementBlocked || snap?.Hints?.HoldMovement(now) == true;
        _observed = _observation.Capture(playerPos, now, held, snap?.Hints?.PendingKnockback == true);
        var heldForDisplacement = _observed.PendingDisplacement || _observed.ForcedMovement;
        if (heldForDisplacement != _displacementHeld)
        {
            InvalidatePlanning(now);
            DropRoute();
        }
        _displacementHeld = heldForDisplacement;
        var inputLocked = _observed.MovementLocked && !heldForDisplacement;
        var inputResumed = _inputLocked && !inputLocked;
        _inputLocked = inputLocked;
        var targetChanged = goal.Replaces(_goal);
        _goal = goal;
        if ((targetChanged && region == null) || !ReferenceEquals(path, _preparedPath))
        {
            InvalidatePlanning(now);
            if (region == null) DropRoute();
        }
        _preparedPath = path;
        var map = _mapProvider();
        // Do not use a bitmap belonging to another region of this territory.
        if (map != null && !map.ContainsWorld(playerPos)) map = null;
        if (!ReferenceEquals(map, _observedMap))
        {
            _observedMap = map;
            InvalidatePlanning(now);
            DropRoute();
        }
        if (inputLocked)
        {
            ReleaseDirect();
            Diag(now, "movement-locked");
            return NavigationState.Moving;
        }
        if (constraintsChanged || inputResumed)
        {
            _nextReplan = now;
            if (_route is { } route && !NavigationPlanner.CanStart(route, _observed,
                _follower.CopyForPrediction().Step(_observed), snap, _currentTerrain, speed, ReplanInterval.TotalSeconds, _routeSnapshot))
                DropRoute();
        }
        bool canApproach = region != null && ReferenceEquals(region, _attackRegion) && map != null
            && snap == null && !heldForDisplacement
            && !new DeepDungeon.Fsd.Core.NavigationRange(arrivalRadius).Contains(playerPos, dest);
        if (_route is { Reason: "attack-approach" } approach && (!canApproach ||
            !AttackApproachMakesProgress(approach, new(dest.X, dest.Z))))
            DropRoute();
        CompleteBackgroundPlan(now, speed, snap, canApproach);
        if ((region != null || _pendingPlan == null)
            && (now >= _nextReplan || (region is { Searching: true } && snap == null)))
        {
            _nextReplan = now + ReplanInterval;
            var request = new NavigationRequest(now, new(playerPos.X, playerPos.Z), new(dest.X, dest.Z),
                arrivalRadius, speed, map, snap)
            { Region = region, PlayerHeight = playerPos.Y, PlayerId = _playerId(), Observation = _observed, Continuation = _route, ContinuationIndex = _follower.Index, Follower = _follower, PreparedPath = path };
            if (region == null) StartBackgroundPlan(request);
            else
            {
                PlanRegion(request);
                if (canApproach && _pendingPlan == null && _route == null && region.Searching &&
                    (_lastAttackApproachGoal is not { } previous || Vector2.DistanceSquared(request.Goal, previous) > .01f))
                {
                    _lastAttackApproachGoal = request.Goal;
                    StartBackgroundPlan(request with { Region = null, AttackApproachTarget = region.TargetId });
                }
            }
        }
        if (_route == null)
        { ReleaseDirect(); Diag(now, _failure ?? "planning"); return NavigationState.StuckRepathing; }
        var command = _follower.Step(_observed);
        _routeIndex = _follower.Index;
        _commandedTarget = new(command.Target.X, playerPos.Y, command.Target.Y);
        try
        {
            NativeNavigationOutput.Apply(_direct, command, playerPos);
            _issuedPlanId = _adoptedPlanId;
        }
        catch (Exception ex)
        {
            _failure = ex.Message; ReleaseDirect(); _nextReplan = now;
            Diag(now, "controller-unavailable"); return NavigationState.StuckRepathing;
        }
        if (command.Kind == NavigationCommandKind.Replan)
        { _nextReplan = now; Diag(now, "game-driven-handoff"); return NavigationState.Moving; }
        if (command.Correction is { } corrected)
        {
            _observation.CorrectPosition(corrected);
            _record("coord-endpoint-correction", new { planId = _adoptedPlanId,
                fromX = playerPos.X, fromZ = playerPos.Z, toX = corrected.X, toZ = corrected.Y });
        }
        if (command.Kind == NavigationCommandKind.Move)
        {
            var direction = command.Target - _observed.Position;
            if (direction.LengthSquared() > 1e-6f) _lastDirection = Vector2.Normalize(direction);
            Diag(now, "moving"); return NavigationState.Moving;
        }
        if (_observed.MovementLocked || _observed.PendingDisplacement || _observed.ForcedMovement)
        { Diag(now, _observed.ForcedMovement ? "native-displacement" : _observed.MovementLocked ? "movement-locked" : "displacement-pending"); return NavigationState.Moving; }
        var inside = snap == null || snap.Arena.Contains(new BossMod.WPos(playerPos.X, playerPos.Z) - snap.ArenaCenter);
        var arrived = region?.Holding ?? (inside && new DeepDungeon.Fsd.Core.NavigationRange(arrivalRadius).HasArrived(playerPos, dest));
        Diag(now, arrived ? "holding-in-range" : command.Kind == NavigationCommandKind.Complete ? "replanning" : "waiting-seg");
        if (command.Kind == NavigationCommandKind.Complete && !arrived)
        {
            if (_route is { Reason: "attack-approach" }) DropRoute();
            _nextReplan = now;
            return NavigationState.StuckRepathing;
        }
        return arrived ? NavigationState.Arrived : NavigationState.Moving;
    }

    private void DropRoute()
    {
        _route = null;
        _routeSnapshot = null;
        _follower.Reset();
        _routeIndex = 0;
        _displayPath.Clear();
    }

    private void ReleaseDirect() { if (_direct.Enabled) _direct.Release(); }
    private void Diag(DateTime now, string state) => RecordDiagnosticState(now, state);
    internal void CancelAttack(ulong targetId)
    {
        if (_attackRegion is { } attack && _activeRegion == attack && attack.TargetId == targetId) Cancel();
    }

    public void Cancel()
    {
        if (_activeRegion is { } active)
        {
            active.Reset();
            if (active == _attackRegion) AttackSuspended?.Invoke();
            _activeRegion = null;
        }
        if (_route != null || _direct.Enabled) Diag(DateTime.UtcNow, "cancel-requested");
        InvalidatePlanning(default);
        _preparedPath = null; _failure = null; _observedMap = null;
        DropRoute();
        // Keep observation history through claim changes and pending-effect gaps.
        ReleaseDirect();
    }
    public void Dispose()
    {
        if (_disposed) return;
        Cancel(); DrainOnDispose(); _observation.Reset(); _disposed = true;
        _direct.AfterWalkInput = null; _direct.Dispose();
    }
}
