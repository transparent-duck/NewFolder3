using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using global::Dalamud.Game.ClientState.Objects.SubKinds;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

/// <summary>One bounded position recovery. Floor accounting survives objective and target changes.</summary>
internal sealed class CombatPositionRecoveryController : IDisposable
{
    private enum Phase { Idle, Searching, Querying, Moving, Verifying }
    private readonly RunContext _ctx;
    private readonly NavigationHelper _navigation;
    private readonly EnemyChaseHelper _chase;
    private readonly NormalFloorGraphSnapshot? _graph;
    private readonly CombatRecoveryBudget _budget;
    private readonly RunFloorTelemetryTrace? _telemetry;
    private readonly Action<string> _status;
    private readonly Action<string, object> _record;
    private readonly CombatPositionNavigation _geometry = new();
    private readonly EnemyChaseAttackWindow _attackWindow = new();
    private readonly List<Vector3> _seeds = new(264), _candidates = new(32), _rejected = new(3);
    private CancellationTokenSource? _cancel;
    private Task<List<Vector3>>? _pending;
    private Phase _phase;
    private ulong _targetId;
    private uint _observedHp;
    private DateTime _lastProgressAt, _lastDamageAt, _deadline, _nextGeometryAt, _verifyAt, _lastMoveAt;
    private Vector3 _targetAtPlan, _startPosition, _destination, _lastMovePosition;
    private int _seedIndex, _candidateIndex, _pathQueries, _failedPositions, _playerRoom;
    private float _actionRange, _playerRadius, _targetRadius;
    private bool _ownsPath;
    private bool _resumeSearch;

    public CombatPositionRecoveryController(RunContext context, NavigationHelper navigation, EnemyChaseHelper chase,
        NormalFloorGraphSnapshot? graph, CombatRecoveryBudget budget, RunFloorTelemetryTrace? telemetry,
        Action<string> setStatus, Action<string, object> record)
    {
        _ctx = context; _navigation = navigation; _chase = chase; _graph = graph; _budget = budget;
        _telemetry = telemetry; _status = setStatus; _record = record;
    }
    public DateTime AttackWindowStartedAt => _attackWindow.StartedAt;
    public bool IsAttackHolding(DateTime now) => _attackWindow.IsHolding(now);
    public void ObserveChaseLegArrival(DateTime now) => _attackWindow.Observe(false, true, now);
    public bool IsActive => _phase != Phase.Idle;

    public unsafe bool Update(InstanceContentDeepDungeon* dd, EnemyChaseTarget target, IBattleChara? current,
        IPlayerCharacter player, bool aggro, bool attackOpportunity)
    {
        var now = DateTime.UtcNow;
        if (current == null || current.IsDead || current.CurrentHp == 0)
        { ResetEngagedTargetProgress(); return false; }
        if (_targetId != target.GameObjectId)
        {
            ResetEngagedTargetProgress();
            _targetId = target.GameObjectId; _observedHp = current.CurrentHp; _lastProgressAt = now;
        }
        _attackWindow.Observe(attackOpportunity, false, now);
        if (current.CurrentHp < _observedHp)
        {
            _lastDamageAt = _lastProgressAt = now;
            if (_phase != Phase.Idle)
            {
                _record("sight-recovery-completed", new { targetId = _targetId, reason = "target-hp-progress", hp = current.CurrentHp });
                EndAttempt();
            }
        }
        _observedHp = current.CurrentHp;
        if (_budget.Exhausted) { Stop("Combat position recovery exhausted the floor retry limit."); return true; }
        _actionRange = _ctx.CombatAssist.GetCachedEngageRange(_ctx.Configuration);
        _playerRadius = player.HitboxRadius; _targetRadius = current.HitboxRadius;
        if (_phase != Phase.Idle)
        {
            if (now >= _deadline) { Fail("recovery-deadline", current); return true; }
            if (_resumeSearch)
                BeginSearch(dd, player.Position, current.Position);
            else if (_phase == Phase.Moving && now >= _nextGeometryAt && Vector3.DistanceSquared(_targetAtPlan, current.Position) > 1)
            {
                _nextGeometryAt = now.AddMilliseconds(200);
                if (!_geometry.HasStandingMargin(_destination, current.Position, _actionRange, _playerRadius, _targetRadius))
                    BeginSearch(dd, player.Position, current.Position);
            }
            return Advance(dd, player, current, now);
        }
        if (now < _nextGeometryAt || Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting]) return false;
        _nextGeometryAt = now.AddMilliseconds(200);
        bool inRange = CombatPositionPolicy.WithinRange(player.Position, current.Position, _actionRange,
            _playerRadius, _targetRadius, 0, 0);
        if (!inRange && _attackWindow.StartedAt == default) return false;
        var sight = CombatPositionNavigation.Sight(player.Position, current.Position, out var hit);
        bool stalled = (aggro ? now - _lastProgressAt : _attackWindow.StartedAt == default
            ? TimeSpan.Zero : now - _attackWindow.StartedAt) >= CombatPositionPolicy.UnknownNoProgressLimit;
        if ((!inRange || sight != CombatSight.Blocked || now - _lastDamageAt < TimeSpan.FromSeconds(1)) && !stalled) return false;
        if (_budget.IsTargetExhausted(_targetId)) return false;
        if (!_geometry.Ready) { Stop("Ground pathfinding is unavailable. Enable the navigation plugin."); return true; }
        _deadline = now + CombatPositionPolicy.AttemptLimit;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(_ctx.Token);
        _cancel.CancelAfter(CombatPositionPolicy.AttemptLimit);
        _failedPositions = 0; _rejected.Clear(); _navigation.Cancel();
        _telemetry?.ObserveStalledEngageRecoveryStarted();
        _record("sight-recovery-started", new
        {
            targetId = _targetId, aggro, reason = sight == CombatSight.Blocked ? "native-los-blocked" : "no-target-progress",
            sight = sight.ToString(), hitPoint = new { hit.X, hit.Y, hit.Z },
            player = new { player.Position.X, player.Position.Y, player.Position.Z },
            target = new { current.Position.X, current.Position.Y, current.Position.Z }, deadlineUtc = _deadline,
            offsets = new[] { 0, -7 }, failures = _budget.TotalFailures
        });
        BeginSearch(dd, player.Position, current.Position);
        return true;
    }

    private unsafe void BeginSearch(InstanceContentDeepDungeon* dd, Vector3 player, Vector3 target)
    {
        StopPath();
        _resumeSearch = false;
        _pending = null; _phase = Phase.Searching; _startPosition = player; _targetAtPlan = target;
        _playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
        _seedIndex = _candidateIndex = _pathQueries = 0; _seeds.Clear(); _candidates.Clear();
        AddSeeds(player, player.Y, target.Y);
        if (Vector2.DistanceSquared(new(player.X, player.Z), new(target.X, target.Z)) <= 225)
            AddSeeds(target, player.Y, target.Y);
        _nextGeometryAt = DateTime.MinValue;
    }
    private void AddSeeds(Vector3 center, float fromY, float toY)
    {
        for (int height = 0; height < 4; height++)
        {
            float y = fromY + (toY - fromY) * height / 3;
            _seeds.Add(new(center.X, y, center.Z));
            for (int radius = 3; radius <= 12; radius += 3)
                foreach (var direction in CombatPositionPolicy.Directions)
                    _seeds.Add(new(center.X + direction.X * radius, y, center.Z + direction.Y * radius));
        }
    }
    private unsafe bool Advance(InstanceContentDeepDungeon* dd, IPlayerCharacter player, IBattleChara current, DateTime now)
    {
        if (!_geometry.Ready) { Stop("Navigation became unavailable during combat position recovery."); return true; }
        if (_phase == Phase.Searching)
        {
            _status("Searching for a clear combat position");
            if (now < _nextGeometryAt) return true;
            _nextGeometryAt = now.AddMilliseconds(50);
            for (int count = 0; count < 8 && _seedIndex < _seeds.Count; count++, _seedIndex++)
            {
                var point = _geometry.GroundPoint(_seeds[_seedIndex]);
                if (point is not { } p || Vector2.DistanceSquared(new(p.X, p.Z), new(_startPosition.X, _startPosition.Z)) > 225) continue;
                if (_graph == null || _playerRoom < 0) continue;
                int room = RoomGraph.GetRoomIndexForPosition(dd, p, _graph.ReachableRooms, -1);
                if (room < 0 || _graph.RoomDistances[_playerRoom, room] is < 0 or > 1) continue;
                if (_rejected.Any(previous => Vector3.DistanceSquared(previous, p) < 1) ||
                    _candidates.Any(previous => Vector3.DistanceSquared(previous, p) < 0.25f)) continue;
                if (_geometry.HasStandingMargin(p, current.Position, _actionRange, _playerRadius, _targetRadius)) _candidates.Add(p);
            }
            if (_seedIndex < _seeds.Count) return true;
            _candidates.Sort((a, b) => Vector3.DistanceSquared(_startPosition, a).CompareTo(Vector3.DistanceSquared(_startPosition, b)));
            if (_candidates.Count == 0) { Fail("no-compatible-local-position", current); return true; }
            StartPathQuery(player.Position); return true;
        }
        if (_phase == Phase.Querying)
        {
            _status("Verifying the returned ground-path endpoint");
            if (_pending == null || !_pending.IsCompleted) return true;
            try
            {
                var result = _pending.GetAwaiter().GetResult(); _pending = null;
                float length = 0;
                if (!CombatPositionPolicy.ValidatePathEndpoint(player.Position, _destination,
                        result, out length, out var reason))
                {
                    _record("sight-recovery-path-rejected", new { targetId = _targetId, reason });
                    if (_candidateIndex < _candidates.Count && _pathQueries < 4) StartPathQuery(player.Position);
                    else Fail("no-local-path-to-position", current);
                    return true;
                }
                var requested = _destination;
                _destination = result[^1];
                if (!_geometry.HasStandingMargin(_destination, current.Position, _actionRange, _playerRadius, _targetRadius))
                { BeginSearch(dd, player.Position, current.Position); return true; }
                _geometry.Move(result); _ownsPath = true;
                _phase = Phase.Moving; _lastMoveAt = now; _lastMovePosition = player.Position;
                _record("sight-recovery-path-started", new
                {
                    targetId = _targetId, status = "EndpointVerified", pathLength = length, margin = CombatPositionPolicy.StandingMargin,
                    requested = new { requested.X, requested.Y, requested.Z },
                    destination = new { _destination.X, _destination.Y, _destination.Z }, offsets = new[] { 0, -7 },
                    deadlineUtc = _deadline
                });
            }
            catch (Exception error) { Fail("path-query-failed: " + error.Message, current); }
            return true;
        }
        if (_phase == Phase.Moving)
        {
            _status("Moving to a clear combat position");
            if (Vector3.DistanceSquared(player.Position, _destination) <= CombatPositionPolicy.ArrivalRadius * CombatPositionPolicy.ArrivalRadius)
            {
                StopPath(); _phase = Phase.Verifying; _verifyAt = now; _attackWindow.Observe(true, true, now);
                _record("sight-recovery-position-arrived", new { targetId = _targetId, position = new { player.Position.X, player.Position.Y, player.Position.Z } });
                return false;
            }
            if (Vector3.DistanceSquared(player.Position, _lastMovePosition) > 0.04f)
            { _lastMovePosition = player.Position; _lastMoveAt = now; }
            if (now - _lastMoveAt >= TimeSpan.FromSeconds(4)) RejectPosition(dd, player, current, "navigation-no-progress");
            return true;
        }
        if (_phase == Phase.Verifying && now - _verifyAt >= TimeSpan.FromSeconds(6))
        { RejectPosition(dd, player, current, "attack-no-progress"); return true; }
        return false;
    }
    private void StartPathQuery(Vector3 player)
    {
        _destination = _candidates[_candidateIndex++]; _pathQueries++;
        _pending = _geometry.FindPath(player, _destination, _cancel!.Token);
        _pending.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        _phase = Phase.Querying;
    }
    private unsafe void RejectPosition(InstanceContentDeepDungeon* dd, IPlayerCharacter player, IBattleChara target, string reason)
    {
        _rejected.Add(_destination); _failedPositions++;
        _record("sight-recovery-position-rejected", new { targetId = _targetId, reason, failedPositions = _failedPositions });
        if (_failedPositions >= 3) Fail(reason, target);
        else BeginSearch(dd, player.Position, target.Position);
    }
    private void Fail(string reason, IBattleChara target)
    {
        _budget.RecordFailure(_targetId);
        _record("clearing-target-deprioritized", new { targetId = _targetId, reason, hp = target.CurrentHp, failures = _budget.TotalFailures,
            targetExhausted = _budget.IsTargetExhausted(_targetId) });
        _chase.DeprioritizeCurrentTarget(); _ctx.SuppressCombatTarget(_targetId, TimeSpan.FromSeconds(20));
        _ctx.ClearPreferredAggroTarget(); EndAttempt(); _targetId = 0; _attackWindow.Reset();
        _status("Switching target after combat position recovery failed");
        if (_budget.Exhausted) Stop("Combat position recovery exhausted the floor retry limit.");
    }
    private void Stop(string reason)
    {
        EndAttempt(); _ctx.StatusIsError = true; _ctx.StatusLine = reason; _status(reason);
        _record("sight-recovery-stopped", new { reason, targetId = _targetId, failures = _budget.TotalFailures });
    }
    private void StopPath()
    {
        if (_ownsPath) { moveHelper.VNav.Path.Stop(); _ownsPath = false; }
    }
    private void EndAttempt()
    {
        StopPath(); _cancel?.Cancel(); _cancel?.Dispose(); _cancel = null; _pending = null; _phase = Phase.Idle;
    }
    public void ResetEngagedTargetProgress()
    {
        EndAttempt(); _targetId = 0; _observedHp = 0; _lastProgressAt = _lastDamageAt = default; _attackWindow.Reset();
    }
    public void Dispose() => ResetEngagedTargetProgress();
    public void YieldMovement()
    {
        if (_phase == Phase.Idle) return;
        StopPath();
        _resumeSearch = true;
    }
    public static unsafe bool TryResolveRoomDestination(InstanceContentDeepDungeon* dd, int room, out Vector3 destination)
    {
        destination = default;
        return room >= 0 && dd != null && MapPos.TryGetRoomCenter(dd, room, out destination);
    }
}
