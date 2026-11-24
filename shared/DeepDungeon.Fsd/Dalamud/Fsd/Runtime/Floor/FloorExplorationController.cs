using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using MasterPlayer.MechanicRuntime;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud.Coord;
using DeepDungeon.Fsd.Dalamud.Encounter;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using DeepDungeon.Fsd.Dalamud.Map;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal enum FloorRuntimeKind { Normal, Boss }

/// <summary>Owns the search, chest, patrol and passage execution of one stable floor.</summary>
internal sealed partial class FloorExplorationController : IDisposable
{
    internal delegate void NativeStateRecorder(string checkpoint, bool force);
    internal delegate void NativeStateSnapshotRecorder(string checkpoint, bool force, bool nativeStateAvailable, bool nativeIntuitionActive);
    private readonly RunContext _ctx;
    private readonly EnemyChaseHelper _chaseHelper;
    private readonly PomanderManager _pomanderManager;
    private readonly ChatWatchers? _chatWatchers;
    private readonly IRunTelemetryObserver? _runTelemetryObserver;
    private readonly PassageDestinationResolver _passageDestination;
    private readonly Func<FloorPhase> _getPhase;
    private readonly Action<FloorPhase> _setPhase;
    private readonly Func<string> _getStatus;
    private readonly Action<string> _setStatus;
    private readonly Func<bool> _getNativeIntuition;
    private readonly Action CancelActiveMovement;
    private readonly Action<string, object> RecordReplayEvent;
    private readonly NativeStateRecorder RecordNativeIntuitionState;
    private readonly NativeStateSnapshotRecorder RecordNativeIntuitionSnapshot;
    private readonly Func<RunOptions> SnapshotRunOptions;
    private FloorPhase _phase { get => _getPhase(); set => _setPhase(value); }
    private string _status { get => _getStatus(); set => _setStatus(value); }
    private bool _nativeIntuitionActive => _getNativeIntuition();
    internal Vector3? CameraChestPosition => _coordPipeline?.ActiveChestAttempt?.Waypoint.Position;
    private string _blockedMovementOperation = string.Empty;
    private static readonly TimeSpan GeneralTickInterval = TimeSpan.FromMilliseconds(500);
    private Vector3 ResolvePassageWalkingPosition(Vector3 actor) => _passageDestination.Resolve(actor, Generation);
    private FarmingItemPolicy ItemPolicy => FarmingItemPolicy.For(_ctx.FarmingPlan?.Mode);
    private bool EntryIncenseWindowOpen() => this.Survey.EntryIncenseWindowOpen();
    private bool CombatBuffUsable(uint slot, bool overcapRelief = false) =>
        IsPomanderAvailableForFloorUse(slot) && ItemPolicy.AllowsCombatBuff(
            _pomanderManager.GetCount(slot), _ctx?.Duty.IsBossFloor == true, overcapRelief);



    public FloorExplorationController(
        long generation,
        uint dungeonId,
        byte floor,
        FloorRuntimeKind kind,
        DateTime readyAtUtc,
        NormalFloorGraphSnapshot? normalGraph,
        bool? nativeIntuitionActive,
        RunFloorTelemetryTrace? runTelemetry, FloorItemExecutor items,
        RunContext context,
        EnemyChaseHelper chase, PomanderManager pomanders, ChatWatchers? watchers,
        IRunTelemetryObserver? telemetryObserver, PassageDestinationResolver passageDestination,
        Func<FloorPhase> getPhase, Action<FloorPhase> setPhase,
        Func<string> getStatus, Action<string> setStatus, Func<bool> getNativeIntuition,
        Action cancelMovement, Action<string, object> record,
        NativeStateRecorder recordNative, Func<RunOptions> snapshotRunOptions,
        NativeStateSnapshotRecorder recordNativeSnapshot)
    {
        Generation = generation;
        DungeonId = dungeonId;
        Floor = floor;
        Kind = kind;
        ReadyAtUtc = readyAtUtc;
        NormalGraph = normalGraph;
        Objectives = new FloorObjectiveSession(generation);
        // Floor-scoped knowledge binds before any normal decision; the
        // spawn room and hoard baseline seal on the first native ingest.
        context.Knowledge.BeginFloor(generation, dungeonId, floor, -1, -1);
        NativeIntuitionActive = nativeIntuitionActive;
        ObjectEvidence = new FloorObjectEvidenceTracker();
        RunTelemetry = runTelemetry;
        Items = items;
        _ctx = context;
        _chaseHelper = chase; _pomanderManager = pomanders; _chatWatchers = watchers;
        context.ClearingTargetId = null;
        chase.IsTargetBlocked = context.IsCombatTargetSuppressed;
        _runTelemetryObserver = telemetryObserver; _passageDestination = passageDestination;
        _getPhase = getPhase; _setPhase = setPhase; _getStatus = getStatus; _setStatus = setStatus;
        _getNativeIntuition = getNativeIntuition; CancelActiveMovement = cancelMovement;
        RecordReplayEvent = record; RecordNativeIntuitionState = recordNative;
        SnapshotRunOptions = snapshotRunOptions;
        RecordNativeIntuitionSnapshot = recordNativeSnapshot;
    }

    public long Generation { get; }
    public uint DungeonId { get; }
    public byte Floor { get; }
    public FloorRuntimeKind Kind { get; }
    public DateTime ReadyAtUtc { get; }
    public NormalFloorGraphSnapshot? NormalGraph { get; private set; }
    private bool? NativeIntuitionActive { get; set; }
    public FloorObjectiveSession Objectives { get; }
    private DateTime _lastGeneralTickAt;
    public PendingIntuitionState PendingIntuition => Items.PendingIntuition;
    public FloorItemExecutor Items { get; }
    public FloorItemUseSession ItemUse => Items.Session;
    public FloorObjectEvidenceTracker ObjectEvidence { get; }
    public RunFloorTelemetryTrace? RunTelemetry { get; }
    private RunFloorStateCumulativePublisher? RunFloorStatePublisher { get; set; }
    private int LastObservedRoomIndex { get; set; } = -1;
    private bool FarmingPassageItemConfirmed { get; set; }
    public FloorBossController Boss { get; private set; } = null!;
    public void AttachBoss(FloorBossController boss) => Boss = boss;
    public FloorSurveyController Survey { get; private set; } = null!;
    public void AttachSurvey(FloorSurveyController survey) => Survey = survey;
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed)
            return;

        IsDisposed = true;
        NormalGraph = null;
        NativeIntuitionActive = null;
        LastObservedRoomIndex = -1;
        Survey.Dispose();
        Boss?.Dispose();
        Items.Dispose();
        ObjectEvidence.Dispose();
        // Retire work, release the shared navigator, then drop floor-local data.
        _coordPipeline?.Reset();
        _navigation?.Cancel();
        _navigation?.Dispose();
        _siteDriver?.Dispose();
        _coordRecoveryController = null;
        _combatActionEvidence?.Dispose();
        _combatActionEvidence = null;
        // Clear the diagnostics publication only while THIS owner still
        // owns it — never erase a newer publication.
        if (Coord.NavDiag.Navigation == _navigation)
            Coord.NavDiag.Navigation = null;
        if (Coord.NavDiag.Map == _navBitmap)
            Coord.NavDiag.Map = null;
        _navFailureReported.Clear();
        _siteSearch = null;
        _coordPipeline = null;
        _siteDriver = null;
        _navigation = null;
        _ctx?.Knowledge.EndFloor(Generation);
        RunFloorStatePublisher = null;
        Objectives.Dispose();
    }
    internal bool RequireMovementPermission(
string operation,
FloorObjectiveKind? requiredObjective = null,
bool primaryOwnsOperation = true)
    {
        var decision = this.Objectives.ObjectiveDecision;
        bool allowed = this.Objectives.HasObjectiveDecision == true &&
                       decision.Channels.Movement != CommandChannelPermission.Blocked &&
                       primaryOwnsOperation &&
                       (!requiredObjective.HasValue || decision.PrimaryObjective == requiredObjective.Value);
        if (allowed)
        {
            ClearPermissionBlock(ref _blockedMovementOperation, "Movement");
            return true;
        }

        RecordPermissionBlock(ref _blockedMovementOperation, "Movement", operation, requiredObjective);
        _status = $"Waiting: movement permission blocked for {operation} ({decision.PrimaryObjective})";
        return false;
    }

    private EncounterPlanningSnapshot? ReadNavigationSnapshot() =>
        Kind == FloorRuntimeKind.Normal
            ? _ctx?.Encounters.Read().LocalHazards
            : _ctx?.Encounters.Read().Encounter;

    private unsafe void EnsureCoordScope(InstanceContentDeepDungeon* dd)
    {
        _scopeDd = dd;
        _coordPipeline ??= new CoordPipeline(RecordReplayEvent);
        _navigation ??= new Coord.NavigationSurface(() => _navBitmap, RecordReplayEvent, ReadNavigationSnapshot)
        { AttackSuspended = () => _coordRecoveryController?.Suspend() };
        // Rebuild floor terrain and coverage once per floor. All movement
        // uses the same navigator, irrespective of the selected task.
        var mapKey = dd == null ? null : FloorMapKey.For(dd);
        if (dd != null && (Floor != _navBitmapFloor || (mapKey.HasValue ? _navBitmap?.Matches(mapKey, dd) != true : _navBitmap != null)))
        {
            if (mapKey is { } key)
            {
                var map = FloorMap.Get(key, dd->Floor) ?? throw new InvalidDataException(FloorMap.Error);
                _navBitmap = Coord.NavBitmap.Create(map, dd);
                _coverage = new Coord.CoverageSet(
                    _navBitmap.SampleCoverageAnchors(_navBitmap.RoomAnchors),
                    _navBitmap.RoomFlags,
                    RoomGraph.GetLocalPlayerRoomIndex(dd));
                RecordReplayEvent("coord-floor-map", new
                {
                    floor = dd->Floor, territory = key.Territory, layout = key.Layout,
                    anchors = _navBitmap.RoomAnchors.Count, closedBoxes = _navBitmap.ClosedBoxes
                });
            }
            else
            {
                _navBitmap = null;
                _coverage = null;
            }
            _navBitmapFloor = Floor;
            Coord.NavDiag.Map = _navBitmap;
            _navFailureReported.Clear();   // dedup is per floor+target
            _siteDriver ??= new Coord.ObjectiveSiteDriver();
            // Quota-position preview is a read-only last-target
            // observation — the driver must never ACQUIRE a target just
            // to describe the work.
            _siteDriver.ChasePositionProvider =
                () => _chaseHelper?.LastTarget is { } lt ? lt.LivePosition : null;
            var siteOpts = SnapshotRunOptions();
            _siteDriver.Configure(new Coord.ObjectiveSiteSource.ObjectiveSiteFilter(
                OpenBronze: siteOpts.OpenBronze,
                OpenSilver: siteOpts.OpenSilver,
                OpenGold: siteOpts.OpenGold,
                Banded: siteOpts.BandedEnabled,
                HuntMobs: true, ProbeRooms: true, Exit: true));
            _siteDriver.SetNormalGraph(NormalGraph);
            _siteDriver.SetNavigationMap(_navBitmap);
            if (UsesSiteCoordination && _ctx != null)
            {
                // Knowledge-driven path: run-pinned map policy/catalog,
                // shared run knowledge, mode policy — never the live
                // catalog manager or a second evidence scan.
                _siteDriver.ConfigureWork(
                    _ctx.DetailedMap, _ctx.Knowledge,
                    () => FloorWorkPolicyBuilder.Build(
                        _ctx.FarmingPlan, SnapshotRunOptions()),
                    RecordReplayEvent, CanClearChestOvercap);
            }
            _siteSearch = new Coord.SiteSearchBoundary(
                _siteDriver, () => _scopeDd,
                () => Service.LocalPlayer?.Position ?? Vector3.Zero,
                () => ObjectEvidence.Current);
            _navigation?.Cancel();
            UpdateEncounterTask();
            Coord.NavDiag.Navigation = _navigation;
        }
        _coordReal ??= new RealNativeBoundary(
            () => _navigation!,
            () => Service.LocalPlayer?.Position ?? Vector3.Zero,
            () => _ctx?.ChestInteraction,
            () => _siteSearch,
            SelectClearingTarget,
            NavigateToAttack,
            ResolveCoordChestOvercap,
            snapshot => _ctx?.CombatAssist.SelectForNavigation(_ctx, snapshot));
    }

    private EnemyChaseAcquisitionFailure _lastChaseAcquisitionFailure;

    private unsafe EnemyChaseTarget? SelectClearingTarget(Vector3 position, bool allowNew)
    {
        var blockedAggroId = _coordRecoveryController?.BlockedAggroId ?? 0;
        float range = _ctx.CombatAssist.GetEngageRange();
        var target = _chaseHelper.GetClearingTarget(_ctx, _scopeDd, NormalGraph, LastObservedRoomIndex,
            position, out var reason, allowNew, blockedAggroId != 0
                ? actor => actor.GameObjectId != blockedAggroId
                : null);
        if (target == null && allowNew && reason is EnemyChaseAcquisitionFailure.TargetRoomUnavailable
            or EnemyChaseAcquisitionFailure.PlayerRoomUnavailable)
        {
            var corridorTarget = CombatTargetingHelpers.PickNearestHostile(range, out _, actor =>
                !_ctx.IsCombatTargetSuppressed(actor.GameObjectId) && _ctx.IsNewCombatTargetAllowed(actor) &&
                CombatPositionNavigation.CheckAttack(position, actor.Position, range).Allowed);
            if (corridorTarget != null)
            {
                target = new EnemyChaseTarget(corridorTarget.GameObjectId, corridorTarget.Position);
                reason = EnemyChaseAcquisitionFailure.AttackableWithoutRoomIndex;
            }
        }
        _ctx.ClearingTargetId = target?.GameObjectId;
        if (reason != _lastChaseAcquisitionFailure)
        {
            _lastChaseAcquisitionFailure = reason;
            RecordReplayEvent("clearing-target-observation", new { floor = Floor, reason = reason.ToString() });
        }
        if (target == null && reason != EnemyChaseAcquisitionFailure.None)
            _status = $"等待清怪目標：{reason}";
        return target;
    }

    private static object V(Vector3 p) => new { p.X, p.Y, p.Z };

    /// <summary>Bounded local bitmap window for a failure replay event —
    /// ±half meters of cell states around the player, one char per cell
    /// ('#' blocked, '.' free), capped to keep the payload sane.</summary>
    private static object? BitmapWindow(
        Coord.NavBitmap? map, Vector3 center, float half)
    {
        if (map == null)
            return null;
        var (xa, ya) = map.WorldToGrid(center + new Vector3(-half, 0, -half));
        var (xb, yb) = map.WorldToGrid(center + new Vector3(half, 0, half));
        // min/max come from the ORIGINAL pair, before either bound is
        // assigned — the previous clamped-x0 comparison was wrong.
        var x0 = Math.Clamp(Math.Min(xa, xb), 0, map.Width - 1);
        var x1 = Math.Clamp(Math.Max(xa, xb), 0, map.Width - 1);
        var yA = Math.Clamp(Math.Min(ya, yb), 0, map.Height - 1);
        var yB = Math.Clamp(Math.Max(ya, yb), 0, map.Height - 1);
        var truncated = false;
        if (x1 - x0 > 256) { x1 = x0 + 256; truncated = true; }
        if (yB - yA > 256) { yB = yA + 256; truncated = true; }
        var rows = new List<string>(yB - yA + 1);
        for (var y = yA; y <= yB; y++)
        {
            var sb = new System.Text.StringBuilder(x1 - x0 + 1);
            for (var x = x0; x <= x1; x++)
                sb.Append(map.IsFree(x, y) ? '.' : '#');
            rows.Add(sb.ToString());
        }
        // World origin + dimensions + truncation keep the window
        // spatially reconstructible.
        var origin = map.GridToWorld(x0, yA);
        return new
        {
            x0, y0 = yA, x1, y1 = yB, pixel = map.PixelSize,
            origin = new { origin.X, origin.Y, origin.Z },
            width = x1 - x0 + 1, height = yB - yA + 1, truncated, rows,
        };
    }

    private NavOutcome NavigateToAttack(AttackTargetVerb goal)
    {
        if (_ctx == null || Service.LocalPlayer is not { IsDead: false } player ||
            CombatTargetingHelpers.GetBattleCharaByGameObjectId(goal.TargetId) is not { IsDead: false, CurrentHp: > 0 } target)
        {
            _navigation!.Cancel();
            return NavOutcome.Pending;
        }
        _coordRecoveryController ??= new(_ctx, _chaseHelper, RunTelemetry,
            _combatActionEvidence ??= new(_ctx.Encounters as EncounterMechanicHost),
            status => _status = status, RecordReplayEvent);
        var range = _ctx.CombatAssist.GetEngageRange();
        if (!float.IsFinite(range) || range <= 0) range = CombatPositionPolicy.DefaultAttackRange(player.ClassJob.RowId);
        return _coordRecoveryController.Navigate(_navigation!, target, player.Position, goal.Position, range, DateTime.UtcNow);
    }

    /// <summary>True while the coord escape claim owns movement — boss
    /// controllers suppress their own movement on this flag.</summary>
    internal bool CoordEscapeActive { get; private set; }

    /// <summary>Normal-floor site coordination: the committed site-work
    /// pipeline owns decisions and movement.</summary>
    internal bool UsesSiteCoordination =>
        Kind == FloorRuntimeKind.Normal;

    /// <summary>Internal boss navigation becomes available when the shared
    /// mechanic host recognizes the encounter. A bound encounter retains
    /// ownership during missing frames; unsupported bosses keep their flow.</summary>
    internal bool UsesEncounterCoordination =>
        Kind == FloorRuntimeKind.Boss
        && (_coordPipeline?.CurrentEncounterOp != null
            || _ctx?.Encounters.Read().Encounter is { Hints: not null } snapshot
            && snapshot.Territory == (ushort)Service.ClientState.TerritoryType);

    // Task selection does not select a different navigator.
    internal EncounterReadiness GetBossReadiness()
    {
        var source = _ctx?.Encounters ?? throw new InvalidOperationException("Boss floor has no encounter source.");
        var frame = source.Read();
        if (frame.Encounter is { } snapshot && snapshot.Territory == (ushort)Service.ClientState.TerritoryType)
            return frame.Readiness;
        var boss = CombatTargetingHelpers.PickNearestHostile(60f, out _);
        return source.Read(boss?.EntityId ?? 0, boss?.BaseId ?? 0).Readiness;
    }

    private void UpdateEncounterTask()
    {
        if (!UsesEncounterCoordination) _coordPipeline?.RetireEncounter();
        _navBackend = _navigation?.Solver ?? "navigation";
        Coord.NavDiag.Navigation = _navigation;
    }

    /// <summary>Encounter-driven tick for the supported profile. Returns
    /// false only for another profile — this profile owns the flow even
    /// while waiting for data or a blocked channel. A settings-mode
    /// switch mid-floor still unwinds the wrapper before returning.</summary>
    internal unsafe bool UpdateBossEncounter(InstanceContentDeepDungeon* dd)
    {
        if (!UsesEncounterCoordination)
        {
            if (_coordPipeline?.CurrentEncounterOp != null)
                UpdateEncounterTask();
            return false;
        }
        if (Service.Condition[ConditionFlag.BetweenAreas]
            || Service.Condition[ConditionFlag.BetweenAreas51])
        {
            _navigation?.Cancel();
            _coordPipeline?.SuspendEncounter();
            _status = "boss encounter - waiting for floor load";
            return true;
        }
        if (Service.LocalPlayer is not { } p || p.IsDead)
        {
            _navigation?.Cancel();
            _coordPipeline?.SuspendEncounter();
            _status = "boss encounter - waiting for player";
            return true;
        }
        if (!RequireMovementPermission("boss encounter", FloorObjectiveKind.DefeatBoss))
        {
            _navigation?.Cancel();
            _coordPipeline?.SuspendEncounter();
            return true;
        }
        EnsureCoordScope(dd);
        UpdateEncounterTask();
        var snap = _ctx?.Encounters.Read().Encounter;
        if (snap != null && snap.Territory != (ushort)Service.ClientState.TerritoryType)
            snap = null;
        _coordPipeline?.TickEncounter(this.Floor, RoomGraph.GetLocalPlayerRoomIndex(dd),
            DateTime.UtcNow, p.Position,
            Service.Condition[ConditionFlag.InCombat], p.IsDead,
            snap, ObjectEvidence.Current, _coordReal!);
        // Buffs only while a FRESH live boss remains and the op runs —
        // a stale-but-alive snapshot never consumes them.
        var live = snap is { BossDead: false }
            && DateTime.UtcNow - snap.CapturedAt <= TimeSpan.FromMilliseconds(500)
            && _coordPipeline?.EncounterStatus == Coord.OpStatus.Running;
        Boss.UpdateManagedEncounter(dd,
            _coordPipeline?.CurrentEncounterOp is { } encOp
                ? $"{encOp.Describe()}|nav:{_navigation?.Status ?? _navBackend}"
                : _status, live);
        return true;
    }

    /// <summary>
    /// Boss-floor escape path: same claim arbitration as normal floors,
    /// escape slice only — the single movement owner on every floor kind.
    /// </summary>
    internal unsafe void UpdateBossEscape(InstanceContentDeepDungeon* dd)
    {
        CoordEscapeActive = false;
        if (Service.LocalPlayer is not { } p)
            return;
        EnsureCoordScope(dd);
        _coverage?.Update(p.Position);
        CoordEscapeActive = !p.IsDead && _navigation!.NeedsSafetyPlan;
        _coordPipeline!.ExecuteFloorMovement(_coordReal!, null, CoordEscapeActive, p.Position);
    }

    /// <summary>Normal coord decision prep — invoked by the outer update
    /// once per frame AFTER ingest and BEFORE the completion check and the
    /// pipeline tick: coord scope, options, general
    /// pomander cadence, then the site scheduler's decision tick. The
    /// driver is not ticked a second time inside UpdateFloorActive.</summary>
    internal unsafe void PrepareNormalDecision(InstanceContentDeepDungeon* dd)
    {
        if (Service.LocalPlayer is { } coordPlayer)
        {
            EnsureCoordScope(dd);
            SyncLiveRunOptions(dd);
            if (ShouldRunGeneralTick())
                TryUseGeneralAutoPomander(dd);
            // One activity sample per decision tick — the same booleans
            // feed the driver rebuild, the pipeline tick and channel
            // permissions; a transition dirties the route immediately.
            _normalActivity = new Coord.FloorActivity(
                _ctx?.Duty.PassageOpen == true,
                Service.Condition[ConditionFlag.InCombat]);
            _siteSearch?.SetActivity(_normalActivity);
            _siteSearch?.Tick();
            if (_ctx?.Knowledge.Floor?.WorkDecisionInitialized == true)
                PublishInitialRunFloorState(dd, coordPlayer.Position);
            if (_siteDriver is { } driver)
            {
                _status = driver.Status;
                if (driver.PassageSearchExhausted && driver.BlockedWorkReason is { } reason &&
                    _ctx is { } ctx && ctx.FarmingPlan?.ReusesSave != true)
                {
                    ctx.StatusLine = reason;
                    ctx.StatusIsError = true;
                    CancelCoordMovement();
                }
            }
        }
    }

    internal unsafe void UpdateFloorActive(InstanceContentDeepDungeon* dd)
    {
        // Ordinary floors execute the current committed work.
        if (Service.LocalPlayer is { } coordPlayer)
        {
            _coverage?.Update(coordPlayer.Position);
            Vector3? passageDest = null, passageCenter = null, passageActorPos = null;
            int passageRoomIdx = -1;
            var evidence = this.ObjectEvidence.Current;
            if (evidence?.Available == true &&
                _siteSearch is { Head: { } passageHead } &&
                passageHead.Kind == Coord.ObjectiveSiteKind.Passage)
            {
                // Execute the committed entity. A fresh nearest-actor lookup can
                // silently navigate to another beacon on HoH's large floors.
                var position = passageHead.Position;
                foreach (var actor in evidence.PassageActors)
                    if (actor.GameObjectId == passageHead.ActorId)
                    {
                        position = actor.Position;
                        passageActorPos = position;
                        break;
                    }
                passageDest = passageHead.ActorId != 0
                    ? ResolvePassageWalkingPosition(position) : position;
                passageRoomIdx = passageHead.RoomIndex;
                if (MapPos.TryGetRoomCenter(dd, passageRoomIdx, out var center))
                    passageCenter = center;
            }
            // Ownership is published BEFORE the pipeline tick — a chest
            // interaction dispatched inside it can synchronously raise an
            // overcap event whose handlers read CurrentInteractionAttempt.
            var takeoverChanged = !_coordTookOver;
            _coordTookOver = true;
            if (!coordPlayer.IsDead && _coordRecoveryController is { } recovery)
            {
                recovery.ObserveTargetEngagement(_navigation!);
                if (_ctx?.StatusIsError == true) return;
            }
            if (_ctx != null) _ctx.ClearingTargetId = null;
            var boundary = (INativeBoundary)_coordReal!;
            _coordPipeline!.Tick(
                this.Floor,
                RoomGraph.GetLocalPlayerRoomIndex(dd),
                DateTime.UtcNow,
                coordPlayer.Position,
                _normalActivity.InCombat,
                coordPlayer.IsDead,
                _normalActivity.PassageOpen,
                _navigation!.NeedsSafetyPlan,
                passageDest,
                passageCenter,
                passageRoomIdx,
                evidence,
                boundary,
                passageActorPos);
            if (takeoverChanged)
            {
                RecordReplayEvent("coord-takeover", new
                {
                    floor = this.Floor,
                    active = true,
                    op = _coordPipeline.CurrentObjective.ToString(),
                    nav = _navBackend
                });
            }
            // First no-progress failure for a work target/floor: capture
            // the local bitmap window + BOTH paths + the follow state
            // while they still exist (before any cancel clears them).
            if (_navigation?.Failure is { Length: > 0 } navFailure &&
                navFailure.StartsWith("no-progress", StringComparison.Ordinal) &&
                _navFailureReported.Add(
                    $"{this.Floor}|{_siteDriver?.HeadToken.FloorGeneration}|" +
                    $"{_siteDriver?.HeadToken.Key}|" +
                    $"{_siteDriver?.HeadToken.Attempt}"))
            {
                var raw = _navigation.RawPath;
                var smoothed = _navigation.SmoothedPath;
                Utilities.DirectMovementController.TryReadNativeMotion(out var mot);
                RecordReplayEvent("coord-nav-no-progress", new
                {
                    floor = this.Floor,
                    reason = navFailure,
                    headToken = _siteDriver?.HeadToken,
                    movementOwner = _coordPipeline?.MovementOwner,
                    leg = _navigation.LegIndex,
                    legs = smoothed.Count,
                    player = V(coordPlayer.Position),
                    desired = V(_navigation.CommandedTarget),
                    final = V(_navigation.FinalDestination),
                    radius = _navigation.ArrivalRadius,
                    inputEnabled = _navigation.InputEnabled,
                    nativeInput = new { mot.HasInput, mot.Speed },
                    rawCount = raw.Count,
                    rawTruncated = raw.Count > 4096,
                    rawPath = raw.Take(4096).Select(p => V(p)).ToArray(),
                    smoothedCount = smoothed.Count,
                    smoothedTruncated = smoothed.Count > 4096,
                    smoothedPath = smoothed.Take(4096).Select(p => V(p)).ToArray(),
                    bitmap = BitmapWindow(
                        _navigation.CurrentBitmap, coordPlayer.Position, 10f),
                });
            }
        }
    }

    /// <summary>Cancel the coord movement surface — the real surface, not
    /// only the legacy nav helper. Claims resolve fresh next tick.</summary>
    internal void CancelCoordMovement()
    {
        _navigation?.Cancel();
        if (UsesEncounterCoordination || _coordPipeline?.CurrentEncounterOp != null)
            _coordPipeline?.SuspendEncounter();
    }

    private readonly int[] _pomanderStock = new int[RunKnowledge.PomanderSlotCount];
    private readonly bool[] _pomanderActive = new bool[RunKnowledge.PomanderSlotCount];

    /// <summary>
    /// Outer-update knowledge ingest: seal floor facts once, refresh the
    /// pomander/native snapshot, chat-derived intuition facts, the hoard
    /// counter (acquisition = native delta) and shared object evidence —
    /// sight positions, observed banded, exact reveal. Knowledge updates
    /// only mark the route dirty; nothing here replans or moves.
    /// </summary>
    internal unsafe void IngestKnowledge(InstanceContentDeepDungeon* dd)
    {
        var knowledge = _ctx?.Knowledge;
        var fk = knowledge?.Floor;
        if (knowledge == null || fk == null || dd == null)
            return;
        // The floor baseline seals before any count-derived lifecycle
        // observation — a -1 baseline must never be eligible.
        fk.SealFloor(RoomGraph.GetLocalPlayerRoomIndex(dd), dd->HoardCount);
        for (var i = 0; i < RunKnowledge.PomanderSlotCount; i++)
        {
            _pomanderStock[i] = _pomanderManager.GetCount((uint)i);
            _pomanderActive[i] = _pomanderManager.IsActive((uint)i);
        }
        knowledge.SetPomanderSnapshot(_pomanderStock, _pomanderActive);
        fk.ObserveNativeIntuition(_nativeIntuitionActive);
        fk.ObserveSightState(_chatWatchers?.SightState ?? SightUseState.None);
        ObserveIntuitionUse(fk, dd->Floor);
        ObserveHoardCount(dd, "coord-ingest");

        var evidence = ObjectEvidence.Current;
        if (evidence?.Available != true)
            return;
        foreach (var trap in evidence.SightTrapIndicators)
            fk.ObserveSightPosition(new RawWorldPosition(trap.Position.X, trap.Position.Y, trap.Position.Z));
        // Chest entities/kind/position persist across object unload — the
        // point is knowledge, never a live pointer.
        foreach (var chest in evidence.Chests)
        {
            fk.ObserveChest(chest.Object.EntityId, (int)chest.Kind,
                chest.Object.Position,
                RoomIndexOf(dd, chest.Object.Position) ?? -1);
            if (chest.Kind == FloorChestKind.Banded)
                fk.ObserveBanded(chest.Object.Position);
        }
        // Preserve same-floor reveal evidence across object unload. The
        // hoard counter retires it even if a marker lingers after acquisition.
        if (BandedChestLocator.TryFindHoardIndicatorMatch(evidence, out var indicator) &&
            indicator.HasValue && !fk.HoardAcquired &&
            HoardIndicatorLifecyclePlanner.Decide(new(fk.HoardAcquired, fk.HoardBaseline, fk.LatestHoardCount)).AcceptIndicator)
        {
            var had = fk.ExactHoardPosition;
            fk.SetExactHoard(indicator.Value.Position,
                RoomIndexOf(dd, indicator.Value.Position));
            if (!had.HasValue && fk.ExactHoardPosition.HasValue)
                RecordReplayEvent("cached-hoard-indicator-updated", new
                {
                    x = indicator.Value.Position.X,
                    y = indicator.Value.Position.Y,
                    z = indicator.Value.Position.Z
                });
        }
    }

    private unsafe int? RoomIndexOf(InstanceContentDeepDungeon* dd, Vector3 pos)
    {
        // Nearest center within the 30m disk — adjacent room centers'
        // disks overlap, so first-match order must not pick the room.
        var best = -1;
        var bestD = 30f * 30f;
        for (var r = 0; r < RoomGraph.MaxRooms; r++)
            if (MapPos.TryGetRoomCenter(dd, r, out var c))
            {
                var dx = c.X - pos.X;
                var dz = c.Z - pos.Z;
                var d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = r; }
            }
        return best < 0 ? null : best;
    }

    /// <summary>Coord-mode permissions: movement and the primary channel
    /// belong to the pipeline's actual objective; combat permission derives
    /// from real combat/passage state, and chest interaction stays
    /// op-owned (no legacy waypoint sidecar).</summary>
    private void RefreshCoordObjectiveDecision()
    {
        var primary = _coordPipeline?.SelectedObjective ?? FloorObjectiveKind.None;
        var passageOpen = _normalActivity.PassageOpen;
        var inCombat = _normalActivity.InCombat;
        var combat = primary == FloorObjectiveKind.FinishCombatBeforePassage
            ? CommandChannelPermission.PrimaryObjective
            : inCombat || !passageOpen
                ? CommandChannelPermission.CompatibleSidecar
                : CommandChannelPermission.Blocked;
        var channels = new ObjectiveChannelPermissions(
            primary == FloorObjectiveKind.None
                ? CommandChannelPermission.Blocked
                : CommandChannelPermission.PrimaryObjective,
            combat,
            CommandChannelPermission.Blocked,
            primary == FloorObjectiveKind.EnterPassage
                ? CommandChannelPermission.PrimaryObjective
                : CommandChannelPermission.Blocked);
        Objectives.SetObjectiveDecision(new ObjectiveArbiterDecision(primary, channels));
    }


    private void RecordPermissionBlock(
        ref string blockedOperation,
        string channel,
        string operation,
        FloorObjectiveKind? requiredObjective)
    {
        if (string.Equals(blockedOperation, operation, StringComparison.Ordinal))
            return;

        blockedOperation = operation;
        _navigation?.Cancel();
        var decision = this.Objectives.ObjectiveDecision;
        RecordReplayEvent("objective-permission-blocked", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            phase = _phase.ToString(),
            channel,
            operation,
            requiredObjective = requiredObjective?.ToString(),
            primaryObjective = decision.PrimaryObjective.ToString(),
            movement = decision.Channels.Movement.ToString(),
            combat = decision.Channels.Combat.ToString(),
            transition = decision.Channels.Transition.ToString()
        });
    }

    private void ClearPermissionBlock(ref string blockedOperation, string channel)
    {
        if (string.IsNullOrEmpty(blockedOperation))
            return;

        string operation = blockedOperation;
        blockedOperation = string.Empty;
        var decision = this.Objectives.ObjectiveDecision;
        RecordReplayEvent("objective-permission-restored", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            phase = _phase.ToString(),
            channel,
            operation,
            primaryObjective = decision.PrimaryObjective.ToString()
        });
    }

    internal void ResetPermissionBlocks()
    {
        _blockedMovementOperation = string.Empty;
    }

    internal unsafe void RefreshObjectiveDecision(InstanceContentDeepDungeon* dd)
    {
        if (IsDisposed || Floor != dd->Floor || DungeonId != dd->DeepDungeonId)
            return;
        if (UsesSiteCoordination)
        {
            RefreshCoordObjectiveDecision();
            return;
        }
        var bossSnapshot = new ObjectiveArbiterSnapshot(
            BossObjective: true, VisibleBandedChest: false, KnownOrConfirmedHoard: false,
            RequiredHoardDiscovery: false, MandatoryHoardTerminal: true,
            PassageOpen: _ctx.Duty.PassageOpen, PassageActivationRequired: false,
            CombatInProgress: Service.Condition[ConditionFlag.InCombat], RoutineCombatAllowed: true,
            ActiveChestInteraction: false, SuppressCombat: _ctx.FarmingPlan?.ReusesSave == true);
        Objectives.SetObjectiveDecision(ObjectiveArbiter.Decide(bossSnapshot));
    }

    internal void RequestPlanRefresh(string reason)
    {
        if (this.Kind != FloorRuntimeKind.Normal || this.IsDisposed)
            return;

        // Coord path: a refresh request marks the route dirty only — never
        // a synchronous replan or movement callback.
        if (UsesSiteCoordination)
            _siteDriver?.MarkRouteDirty();
    }

    internal void ObserveFloorRuntimeNativeIntuitionEdge()
    {
        if (this.Kind != FloorRuntimeKind.Normal || this.IsDisposed)
            return;

        if (!this.NativeIntuitionActive.HasValue)
        {
            this.NativeIntuitionActive = _nativeIntuitionActive;
            return;
        }

        bool previous = this.NativeIntuitionActive.Value;
        if (previous == _nativeIntuitionActive)
            return;

        this.NativeIntuitionActive = _nativeIntuitionActive;
        string reason = _nativeIntuitionActive
            ? "native-intuition-activated"
            : "native-intuition-deactivated";
        RequestPlanRefresh(reason);
        RecordReplayEvent("native-intuition-edge", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            previous,
            current = _nativeIntuitionActive,
            reason
        });
    }

    internal unsafe void UpdateFloorSetup(InstanceContentDeepDungeon* dd)
    {
        var player = Service.LocalPlayer;
        if (player == null)
        {
            _status = "Waiting for player position";
            return;
        }

        if (_ctx!.Duty.IsBossFloor)
        {
            _phase = FloorPhase.BossFloor;
            _status = "Boss floor";
            Service.Log.Info("[FloorPhase] Boss floor detected -> BossFloor");
            return;
        }

        var normalGraph = this.NormalGraph;
        if (normalGraph == null)
        {
            _status = "Waiting for room graph...";
            return;
        }

        if (_ctx?.FarmingPlan?.Mode is FarmingMode.Hoard or FarmingMode.HoardDiscovery &&
            _ctx.Knowledge.Floor is { ChatSaysHoard: false, ChatSaysNoHoard: false, ExactHoardPosition: null } &&
            this.Survey.AwaitingInheritedIntuition)
        {
            _status = "Waiting for inherited Intuition result";
            return;
        }


        if (!ShouldRunGeneralTick())
            return;

        if (!UsesProgressionItemPolicy && EntryIncenseWindowOpen() &&
            _ctx!.Duty.PassageOpen != true && !this.Survey.NaturalPoisonfruitAttempted &&
            !this.Survey.NaturalMazerootAttemptedOrAdopted &&
            DeepDungeonFloorItemUsePolicy.CanUsePtIncense(dd->DeepDungeonBanId) &&
            (GetStoneCountAvailableForFloorUse(1) > 0 || GetStoneCountAvailableForFloorUse(2) > 0))
        {
            if (this.ItemUse.Pending != null || !CanAttemptPomanderUse())
            {
                _status = "Preparing entry incense before exploration";
                return;
            }
            if (this.Survey.TryUseNaturalPassageAcceleration(dd))
                return;
        }
        TryUseFloorInitPomander(dd);
        if (this.ItemUse.Pending is
            {
                BlocksFloorSetup: true
            } pendingFloorItemUse)
        {
            _status =
                $"Waiting for {pendingFloorItemUse.Key.Kind} {pendingFloorItemUse.Key.ItemId} use confirmation";
            return;
        }
        RecordNativeIntuitionSnapshot("first-floor-plan-generated", force: true, nativeStateAvailable: true, nativeIntuitionActive: _nativeIntuitionActive);
        RecordReplayEvent("floor-plan-generated", new { floor = dd->Floor, reason = "floor-setup-generated", coordination = "site-work" });

        _phase = FloorPhase.FloorActive;
        _status = "Starting floor objective";
        RecordReplayEvent("floor-lifecycle-transition", new
        {
            from = FloorPhase.FloorSetup.ToString(),
            to = FloorPhase.FloorActive.ToString(),
            reason = "floor-ready-plan-available"
        });
    }

    private void ObserveIntuitionUse(FloorKnowledge knowledge, byte floor)
    {
        bool confirmed = _chatWatchers?.UsedIntuitionThisFloor == true;
        bool resultPending = PendingIntuition.TryGetCurrentFloorUseElapsedMilliseconds(floor, DateTime.UtcNow, out var elapsed) &&
            !knowledge.ChatSaysHoard && !knowledge.ChatSaysNoHoard;
        int window = FloorSurveyController.IntuitionResolutionWindowMilliseconds;
        bool waiting = resultPending && elapsed < window;
        knowledge.ObserveIntuitionUse(confirmed, waiting);
        if (confirmed && resultPending && !waiting && PendingIntuition.TryMarkOverdueRecorded())
            RecordReplayEvent("current-intuition-evidence-overdue", new
            {
                floor,
                windowMilliseconds = window,
                reason = "result-window-elapsed-without-authoritative-evidence"
            });
    }

    internal unsafe void ObserveCurrentRoom(InstanceContentDeepDungeon* dd)
    {
        if (dd == null ||
            this.IsDisposed ||
            this.Floor != dd->Floor ||
            this.DungeonId != dd->DeepDungeonId)
            return;

        int roomIndex = RoomGraph.GetLocalPlayerRoomIndex(dd);
        if (roomIndex < 0)
            return;

        this.Survey.EvidenceSession?.ObserveRoomVisit(roomIndex);
        if (roomIndex == this.LastObservedRoomIndex)
            return;

        this.LastObservedRoomIndex = roomIndex;
        RecordReplayEvent("room-entered", new
        {
            floor = dd->Floor,
            floorGeneration = this.Generation,
            roomIndex,
            phase = _phase.ToString(),
            status = _status
        });
    }

    private ChestInteractionAttempt? CurrentInteractionAttempt => _coordPipeline?.ActiveChestAttempt;
    internal const float SilverWaitTimeoutSeconds = 30.0f;
    internal const uint StrengthStatusId = 687;
    internal const uint SteelStatusId = 1100;
    internal const uint DeepDungeonCurseStatusId = 1087;
    internal const uint PtHasteStatusId = 4718;

    internal void HandleGoldChestOvercapObserved(uint? slotIndex)
    {
        var attempt = CurrentInteractionAttempt;
        if (!slotIndex.HasValue ||
            this.IsDisposed ||
            attempt == null ||
            attempt.EntityId == 0 ||
            attempt.Waypoint.Kind != FloorChestKind.Gold)
        {
            RecordReplayEvent("gold-chest-overcap-rejected", new
            {
                floor = this.Floor,
                floorGeneration = this.Generation,
                slotIndex,
                reason = "no-active-gold-interaction-attempt"
            });
            return;
        }

        attempt.PendingGoldOvercapSlotIndex = slotIndex.Value;
        RecordReplayEvent("gold-chest-overcap-correlated", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            // The real work's waypoint — the legacy executor's room/context
            // does not identify coord-owned chests.
            waypoint = attempt.Waypoint.Position,
            entityId = attempt.EntityId,
            slotIndex = slotIndex.Value
        });
    }

    internal unsafe void HandleSilverChestOvercapObserved(uint? stoneRowId)
    {
        var efw = EventFramework.Instance();
        var dd = efw != null ? efw->GetInstanceContentDeepDungeon() : null;
        var attempt = CurrentInteractionAttempt;
        if (dd == null || dd->DeepDungeonId is not (2 or 4) ||
            IsDisposed || this.Floor != dd->Floor ||
            attempt == null || attempt.EntityId == 0 ||
            attempt.Waypoint.Kind != FloorChestKind.Silver ||
            !stoneRowId.HasValue)
            return;
        attempt.PendingSilverOvercapStoneRowId = stoneRowId;
        RecordReplayEvent("silver-chest-overcap-correlated", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            entityId = attempt.EntityId,
            stoneRowId
        });
    }

    private unsafe bool CanClearChestOvercap(ChestInteractionAttempt attempt)
    {
        if (_scopeDd == null) return false;
        if (attempt.PendingGoldOvercapSlotIndex is { } slot)
            return CanUseGoldOvercapRelief(slot, _scopeDd);
        return attempt.PendingSilverOvercapStoneRowId.HasValue && SelectSilverOvercapStone(_scopeDd) != 0;
    }

    private unsafe bool CanUseGoldOvercapRelief(uint slot, InstanceContentDeepDungeon* dd) =>
        FloorItemExecutor.CanUsePomanderOnFloor(slot, dd) &&
        IsPomanderAvailableForFloorUse(slot) && ShouldUseGoldChestOvercapPomander(slot);

    private unsafe Coord.ChestOvercapProgress ResolveCoordChestOvercap(ChestInteractionAttempt attempt)
    {
        if (attempt.PendingGoldOvercapSlotIndex == null && attempt.PendingSilverOvercapStoneRowId == null)
            return Coord.ChestOvercapProgress.Ready;
        var dd = _scopeDd;
        if (dd == null) return Coord.ChestOvercapProgress.Waiting;

        // Inventory decides whether the chest can be opened again. The item
        // channel alone owns dispatch, confirmation and retry.
        bool itemPending = ItemUse.Pending != null;
        bool dispatchReady = !ItemUse.DispatchedThisUpdate && CanAttemptPomanderUse();
        if (attempt.PendingGoldOvercapSlotIndex is { } slot)
        {
            if (_pomanderManager.GetCount(slot) < 3)
            {
                attempt.PendingGoldOvercapSlotIndex = null;
                attempt.NextInteractAt = DateTime.MinValue;
                return Coord.ChestOvercapProgress.Ready;
            }
            if (itemPending) return Coord.ChestOvercapProgress.Waiting;
            if (!CanUseGoldOvercapRelief(slot, dd))
                return Coord.ChestOvercapProgress.PolicySkipped;
            if (dispatchReady)
                TryUsePomander(slot, dd, $"gold overcap relief ({DescribePomanderSlot(slot)})",
                    FloorItemUsePurpose.GoldChestOvercap);
        }
        else
        {
            if (SiteSearchBoundary.ReadStoneCount(dd) < 3)
            {
                attempt.PendingSilverOvercapStoneRowId = null;
                attempt.NextInteractAt = DateTime.MinValue;
                return Coord.ChestOvercapProgress.Ready;
            }
            if (itemPending) return Coord.ChestOvercapProgress.Waiting;
            byte stone = SelectSilverOvercapStone(dd);
            if (stone == 0) return Coord.ChestOvercapProgress.PolicySkipped;
            if (dispatchReady && TryDispatchFloorStone(stone, dd, dd->DeepDungeonId == 2
                    ? "silver overcap relief (HoH magicite)" : "silver overcap relief (serenity incense)",
                    FloorItemUsePurpose.SilverChestOvercap) && dd->DeepDungeonId == 2)
                _magiciteAwaitingRelease = true;
        }

        return Coord.ChestOvercapProgress.Waiting;
    }

    private bool IsSightUseBlocked()
    {
        return SightUseStateMachine.PreventsAutomaticUse(
            _chatWatchers?.SightState ?? SightUseState.None);
    }

    private unsafe void TryUseFloorInitPomander(InstanceContentDeepDungeon* dd)
    {
        if (_chatWatchers == null)
            return;
        var snapshot = BuildFloorInitSnapshot(dd, HasHarmfulFloorEffect(dd));
        if (DungeonCatalog.SupportsNaturalPtStones(dd->DeepDungeonId) &&
            ShouldUseNaturalMazerootForHoardExploration(snapshot) &&
            this.Survey.TryUseNaturalMazeroot(dd, "S1 Sight fallback with 敏慧") == true)
        {
            return;
        }

        var decision = FloorInitPlanner.Decide(snapshot);
        if (decision.ShouldUse)
        {
            TryUsePomander(decision.SlotIndex!.Value, dd, decision.Reason!);
        }
    }

    private bool ShouldUseNaturalMazerootForHoardExploration(
        in FloorInitSnapshot snapshot)
    {
        // Preference over Sight when both effects are needed: with the
        // passage still closed a Mazeroot is worthwhile even while a
        // Sight pomander could serve the same reveal.
        var passageOpen = _ctx?.Duty.PassageOpen == true;
        return snapshot.CanAttemptPomanderUse &&
               snapshot.BandedEnabled &&
               !snapshot.HasOpenedHoardThisFloor &&
               FloorsetHoardDistributionPolicy.AllowsHoardPomander(
                   snapshot.HoardOpportunity) &&
               !snapshot.IntuitionActive &&
               !snapshot.IntuitionUsable &&
               !snapshot.SightUseBlocked &&
               (!passageOpen || !snapshot.SightUsable) &&
               GetStoneCountAvailableForFloorUse(2) > 0;
    }

    private unsafe void TryUseGeneralAutoPomander(InstanceContentDeepDungeon* dd)
    {
        if (CurrentInteractionAttempt is { PendingGoldOvercapSlotIndex: not null } or { PendingSilverOvercapStoneRowId: not null })
            return;

        var decision = GeneralAutoPomanderPlanner.Decide(BuildGeneralAutoPomanderSnapshot(allowStatusOverlap: false, HasHarmfulFloorEffect(dd)));
        if (decision.ShouldUse &&
            TryUsePomander(decision.SlotIndex!.Value, dd, decision.Reason!))
        {
            return;
        }
        var cfk = _ctx?.Knowledge?.Floor;
        // Known exact / known-present intelligence and already observed
        // Banded coffers are useful facts — never reasons to consume
        // another Intuition/Sight. Coverage exhaustion stays eligible.
        if (cfk == null || cfk.HoardAcquired || cfk.ChatSaysNoHoard ||
            cfk.InheritedNoHoardInferred ||
            cfk.ObservedBandedPositions.Count > 0 ||
            cfk.EvidenceState is HoardEvidenceState.IntuitionNoHoard
                or HoardEvidenceState.Disabled
                or HoardEvidenceState.AlreadyOpened
                or HoardEvidenceState.FloorsetMaxed
                or HoardEvidenceState.FloorsetDistributionExcluded
                or HoardEvidenceState.IntuitionDirect
                or HoardEvidenceState.IntuitionWaitingForIndicator
                or HoardEvidenceState.IntuitionPending
                or HoardEvidenceState.IntuitionActiveUnconfirmed)
            return;
        var roomFinish = RoomFinishPomanderPlanner.Decide(
            BuildRoomFinishPomanderSnapshot(dd));
        if (roomFinish.Kind == RoomFinishPomanderDecisionKind.Use &&
            roomFinish.SlotIndex.HasValue)
            TryUsePomander(roomFinish.SlotIndex.Value, dd, roomFinish.Reason!);
    }

    private unsafe FloorInitSnapshot BuildFloorInitSnapshot(
        InstanceContentDeepDungeon* dd,
        bool hasHarmfulFloorEffect)
    {
        bool pomandersUsableThisFloor =
            DeepDungeonFloorItemUsePolicy.CanUsePomanders(
                dd->DeepDungeonBanId);
        // Coord floors read the shared knowledge/run options — the
        // legacy executor's room state is not authoritative there.
        var cfk = _ctx?.Knowledge?.Floor;
        return new FloorInitSnapshot
        {
            CanAttemptPomanderUse = CanAttemptPomanderUse(),
            BandedEnabled = SnapshotRunOptions().BandedEnabled,
            HasOpenedHoardThisFloor = cfk?.HoardAcquired == true,
            HoardOpportunity = DeepDungeonFloorsetTracker.GetCurrentOpportunity(dd->Floor),
            IntuitionActive = _nativeIntuitionActive,
            UsedIntuitionThisFloor = cfk?.HasCurrentFloorIntuitionUse == true,
            SightUseBlocked = IsSightUseBlocked(),
            IntuitionUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.IntuitionPomanderSlotIndex),
            SightUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.SightPomanderSlotIndex),
            AffluenceUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.AffluencePomanderSlotIndex),
            StrengthUsable = !UsesProgressionItemPolicy && pomandersUsableThisFloor && CombatBuffUsable(FloorInitPlanner.StrengthPomanderSlotIndex),
            SteelUsable = pomandersUsableThisFloor && CombatBuffUsable(FloorInitPlanner.SteelPomanderSlotIndex),
            PurityUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.PurityPomanderSlotIndex),
            SerenityUsable = !UsesProgressionItemPolicy &&
                FloorItemExecutor.CanUsePomanderOnFloor(FloorInitPlanner.SerenityPomanderSlotIndex, dd) &&
                IsPomanderAvailableForFloorUse(FloorInitPlanner.SerenityPomanderSlotIndex),
            RaisingUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.RaisingPomanderSlotIndex),
            AffluenceActive = _pomanderManager.IsActive(FloorInitPlanner.AffluencePomanderSlotIndex),
            RaisingActive = _pomanderManager.IsActive(FloorInitPlanner.RaisingPomanderSlotIndex),
            HasStrengthStatus = HasLocalPlayerStatus(StrengthStatusId),
            HasSteelStatus = HasLocalPlayerStatus(SteelStatusId),
            HasCurseStatus = HasLocalPlayerStatus(DeepDungeonCurseStatusId),
            HasHarmfulFloorEffect = hasHarmfulFloorEffect,
            NextFloorIsMob = FloorItemExecutor.NextFloorKind(dd) == DeepDungeonFloorKind.Mob
        };
    }

    private unsafe GeneralAutoPomanderSnapshot BuildGeneralAutoPomanderSnapshot(bool allowStatusOverlap, bool hasHarmfulFloorEffect = false)
    {
        var efw = EventFramework.Instance();
        var dd = efw != null ? efw->GetInstanceContentDeepDungeon() : null;
        return new GeneralAutoPomanderSnapshot
        {
            CanAttemptPomanderUse = CanAttemptPomanderUse(),
            AffluenceUsable = IsPomanderAvailableForFloorUse(FloorInitPlanner.AffluencePomanderSlotIndex),
            StrengthUsable = (!UsesProgressionItemPolicy || allowStatusOverlap) &&
                CombatBuffUsable(FloorInitPlanner.StrengthPomanderSlotIndex, allowStatusOverlap),
            SteelUsable = CombatBuffUsable(FloorInitPlanner.SteelPomanderSlotIndex, allowStatusOverlap),
            PurityUsable = IsPomanderAvailableForFloorUse(FloorInitPlanner.PurityPomanderSlotIndex),
            SerenityUsable = !UsesProgressionItemPolicy && IsPomanderAvailableForFloorUse(FloorInitPlanner.SerenityPomanderSlotIndex),
            RaisingUsable = IsPomanderAvailableForFloorUse(FloorInitPlanner.RaisingPomanderSlotIndex),
            AffluenceActive = _pomanderManager.IsActive(FloorInitPlanner.AffluencePomanderSlotIndex),
            RaisingActive = _pomanderManager.IsActive(FloorInitPlanner.RaisingPomanderSlotIndex),
            HasStrengthStatus = HasLocalPlayerStatus(StrengthStatusId),
            HasSteelStatus = HasLocalPlayerStatus(SteelStatusId),
            HasCurseStatus = HasLocalPlayerStatus(DeepDungeonCurseStatusId),
            HasHarmfulFloorEffect = hasHarmfulFloorEffect,
            AllowStatusOverlap = allowStatusOverlap,
            FlightUsable = (!UsesProgressionItemPolicy || allowStatusOverlap) &&
                IsPomanderAvailableForFloorUse(FloorInitPlanner.FlightPomanderSlotIndex),
            FortuneUsable = IsPomanderAvailableForFloorUse(FloorInitPlanner.FortunePomanderSlotIndex),
            HasteUsable = (!UsesProgressionItemPolicy || allowStatusOverlap) &&
                dd != null && dd->DeepDungeonId == 4 && CombatBuffUsable(FloorInitPlanner.HastePomanderSlotIndex, allowStatusOverlap),
            FlightActive = _pomanderManager.IsActive(FloorInitPlanner.FlightPomanderSlotIndex) ||
                UsesProgressionItemPolicy && _ctx.Knowledge.Floor?.WasPomanderUseConfirmed(FloorInitPlanner.FlightPomanderSlotIndex) == true,
            FortuneActive = _pomanderManager.IsActive(FloorInitPlanner.FortunePomanderSlotIndex),
            HasHasteStatus = _pomanderManager.IsActive(FloorInitPlanner.HastePomanderSlotIndex) || HasLocalPlayerStatus(PtHasteStatusId),
            NextFloorIsMob = dd != null && FloorItemExecutor.NextFloorKind(dd) == DeepDungeonFloorKind.Mob
        };
    }

    private unsafe bool ShouldUseGoldChestOvercapPomander(uint slotIndex)
    {
        if (_chatWatchers == null)
        {
            return false;
        }

        if (UsesProgressionItemPolicy && _scopeDd != null)
        {
            if (slotIndex == FloorInitPlanner.SerenityPomanderSlotIndex)
                return CanSpendProgressionCurrentFloorItem() && ProgressionItemPolicy.Pressure(
                    _scopeDd->DeepDungeonStatusId, _scopeDd->DeepDungeonBanId, _scopeDd->DeepDungeonDangerId) != ProgressionFloorPressure.None;
            if (DungeonId == 2 && slotIndex == ProgressionItemPolicy.FrailtySlot)
                return CanSpendProgressionCurrentFloorItem() && !_pomanderManager.IsActive(slotIndex);
        }

        return slotIndex switch
        {
            _ when GeneralAutoPomanderPlanner.ShouldUseSlot(BuildGeneralAutoPomanderSnapshot(allowStatusOverlap: true), slotIndex) => true,
            FloorInitPlanner.IntuitionPomanderSlotIndex => ShouldUseGoldChestOvercapHoardPomander(slotIndex),
            FloorInitPlanner.SightPomanderSlotIndex => ShouldUseGoldChestOvercapHoardPomander(slotIndex),
            _ => false
        };
    }

    private bool ShouldUseGoldChestOvercapHoardPomander(uint slotIndex)
    {
        if (_chatWatchers == null ||
            IsPomanderBlockedForFloor(slotIndex) ||
            !SnapshotRunOptions().BandedEnabled ||
            _ctx.Knowledge.Floor?.HoardAcquired == true ||
            !FloorsetHoardDistributionPolicy.AllowsHoardPomander(
                DeepDungeonFloorsetTracker.GetCurrentOpportunity(
                    _ctx?.Duty.Floor ?? 0)))
        {
            return false;
        }

        return slotIndex switch
        {
            FloorInitPlanner.IntuitionPomanderSlotIndex => !_nativeIntuitionActive,
            FloorInitPlanner.SightPomanderSlotIndex => !_nativeIntuitionActive && !IsSightUseBlocked(),
            _ => false
        };
    }

    private unsafe RoomFinishPomanderSnapshot BuildRoomFinishPomanderSnapshot(
        InstanceContentDeepDungeon* dd)
    {
        bool pomandersUsableThisFloor =
            DeepDungeonFloorItemUsePolicy.CanUsePomanders(
                dd->DeepDungeonBanId);
        DeepDungeonFloorsetTracker.TryGetCurrentFloorsetState(
            dd->Floor,
            out FloorsetHoardDistributionState floorsetState);
        // Coord floors read the shared knowledge/run options — the
        // legacy executor's room state is not authoritative there.
        var cfk = _ctx?.Knowledge?.Floor;
        return new RoomFinishPomanderSnapshot
        {
            CanAttemptPomanderUse = CanAttemptPomanderUse(),
            BandedEnabled = SnapshotRunOptions().BandedEnabled,
            HasOpenedHoardThisFloor = cfk?.HoardAcquired == true,
            FloorsetBandedCount = floorsetState.TotalHoardCount,
            HoardOpportunity = FloorsetHoardDistributionPolicy.Decide(
                floorsetState,
                dd->Floor),
            IntuitionActive = _nativeIntuitionActive,
            UsedIntuitionThisFloor = cfk?.HasCurrentFloorIntuitionUse == true,
            SightUseBlocked = IsSightUseBlocked(),
            IntuitionUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.IntuitionPomanderSlotIndex),
            SightUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.SightPomanderSlotIndex),
            IntuitionCount = _pomanderManager.GetCount(FloorInitPlanner.IntuitionPomanderSlotIndex),
            RemainingMobFloors = GetRemainingMobFloorCount(dd)
        };
    }

    private static unsafe bool HasHarmfulFloorEffect(InstanceContentDeepDungeon* dd)
    {
        if (dd == null)
            return false;

        return DeepDungeonFloorEffectPolicy.HasHarmfulSerenityRemovableEffect(
            dd->DeepDungeonStatusId,
            dd->DeepDungeonBanId,
            dd->DeepDungeonDangerId);
    }

    internal static bool HasLocalPlayerStatus(uint statusId)
    {
        var player = Service.LocalPlayer;
        if (player == null)
            return false;

        foreach (var status in player.StatusList)
        {
            if (status.StatusId == statusId)
                return true;
        }

        return false;
    }

    private bool CanAttemptPomanderUse() =>
        FloorItemExecutor.CanAttemptPomanderUse(this.Items);
    private bool IsPomanderAvailableForFloorUse(uint slotIndex) =>
        this.Items.IsPomanderAvailableForFloorUse(slotIndex);
    private bool IsPomanderBlockedForFloor(uint slotIndex) =>
        this.Items.IsPomanderBlockedForFloor(slotIndex) == true;
    private bool IsStoneBlockedForFloor(byte stoneId) =>
        this.Items.IsStoneBlockedForFloor(stoneId) == true;
    private int GetStoneCountAvailableForFloorUse(byte stoneId) =>
        this.Items.GetStoneCountAvailableForFloorUse(stoneId);
    private unsafe bool TryUsePomander(uint slotIndex, InstanceContentDeepDungeon* dd, string reason,
        FloorItemUsePurpose purpose = FloorItemUsePurpose.Automatic) =>
        this.Items.TryUsePomander(slotIndex, dd, reason, purpose) == true;
    private unsafe bool TryDispatchFloorStone(byte stoneId, InstanceContentDeepDungeon* dd, string reason,
        FloorItemUsePurpose purpose) => this.Items.TryDispatchFloorStone(stoneId, dd, reason, purpose) == true;

    internal unsafe void ResolvePendingFloorItemUse(InstanceContentDeepDungeon* dd)
    {
        if (this.Items.ResolvePending(dd) is not { } resolution) return;
        if (resolution.Decision == FloorItemUseConfirmationDecisionKind.Confirmed)
            ApplyConfirmedFloorItemUse(dd, resolution.Pending, resolution.CurrentCount);
        else if (resolution.Decision == FloorItemUseConfirmationDecisionKind.Exhausted)
            ApplyExhaustedFloorItemUse(resolution.Pending);
    }

    private unsafe void ApplyConfirmedFloorItemUse(
        InstanceContentDeepDungeon* dd,
        PendingFloorItemUse pending,
        int currentCount)
    {
        if (pending.Key.Kind == FloorItemUseKind.Pomander)
        {
            uint slotIndex = pending.Key.ItemId;
            _ctx?.Knowledge.Floor?.RecordConfirmedPomanderUse(slotIndex);
            if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
            {
                _chatWatchers?.MarkIntuitionUsedThisFloor();
                RecordNativeIntuitionState(
                    $"after-intuition-use:{pending.Reason}:confirmed",
                    force: true);
            }
            else if (slotIndex == FloorInitPlanner.SightPomanderSlotIndex)
            {
                _chatWatchers?.MarkSightAttemptedThisFloor();
                this.Survey.ApplyConfirmedNaturalReveal(pending, SightResearchRevealResource.Sight);
            }

            Service.Log.Info(
                $"[FloorPhase] Confirmed pomander slot {slotIndex} use ({pending.Reason}) on floor {dd->Floor}");
            RecordReplayEvent("pomander-used", new
            {
                floor = dd->Floor,
                slotIndex,
                reason = pending.Reason,
                attempt = pending.AttemptNumber,
                countBeforeDispatch = pending.CountBeforeDispatch,
                currentCount,
                intuitionAttemptId = pending.IntuitionAttemptId
            });
        }
        else
        {
            _ctx?.Knowledge.Floor?.RecordConfirmedStoneUse(pending.Key.ItemId);
            byte stoneId = (byte)pending.Key.ItemId;
            if (dd->DeepDungeonId == 4 && stoneId is 1 or 2) this.FarmingPassageItemConfirmed = true;
            // A confirmed Mazeroot IS this floor's natural reveal — mark
            // Sight confirmed regardless of purpose, without touching
            // synthetic native log sequences.
            if (dd->DeepDungeonId == 4 && stoneId == 2)
                _chatWatchers?.ConfirmSightThisFloor("MazerootUseConfirmed");
            switch (pending.Purpose)
            {
                case FloorItemUsePurpose.NaturalReveal:
                    _chatWatchers?.MarkSightAttemptedThisFloor();
                    this.Survey.ApplyConfirmedNaturalReveal(pending,
                        SightResearchRevealResource.Mazeroot);
                    break;
                case FloorItemUsePurpose.NaturalPoisonfruit:
                    this.Survey.ObserveConfirmedStone(pending.Purpose);
                    break;
                case FloorItemUsePurpose.NaturalPassageMazeroot:
                    this.Survey.ObserveConfirmedStone(pending.Purpose);
                    this.Survey.ApplyConfirmedNaturalReveal(pending,
                        SightResearchRevealResource.Mazeroot);
                    break;
            }

            string eventType = dd->DeepDungeonId == 2 ? "magicite-used" : "incense-used";
            Service.Log.Info(
                $"[FloorPhase] Confirmed stone {stoneId} use ({pending.Reason}) in dungeon {dd->DeepDungeonId} floor {dd->Floor}");
            RecordReplayEvent(eventType, new
            {
                floor = dd->Floor,
                stoneId,
                reason = pending.Reason,
                attempt = pending.AttemptNumber,
                countBeforeDispatch = pending.CountBeforeDispatch,
                currentCount
            });
        }

        RecordReplayEvent("floor-item-use-confirmed", new
        {
            floor = dd->Floor,
            kind = pending.Key.Kind.ToString(),
            itemId = pending.Key.ItemId,
            purpose = pending.Purpose.ToString(),
            attempt = pending.AttemptNumber,
            countBeforeDispatch = pending.CountBeforeDispatch,
            currentCount
        });
    }

    private void ApplyExhaustedFloorItemUse(
        PendingFloorItemUse pending)
    {
        if (pending.Key is
            {
                Kind: FloorItemUseKind.Pomander,
                ItemId: FloorInitPlanner.IntuitionPomanderSlotIndex or
                    FloorInitPlanner.SightPomanderSlotIndex
            } ||
            pending.Purpose == FloorItemUsePurpose.NaturalReveal)
        {
            RequestPlanRefresh("floor-item-use-exhausted");
        }

        this.Survey.ObserveExhaustedItem(pending.Purpose);
    }

    private unsafe void ObserveHoardCount(InstanceContentDeepDungeon* dd, string checkpoint)
    {
        int current = dd->HoardCount;
        int previous = _ctx.Knowledge.Floor?.LatestHoardCount ?? current;
        _ctx.Knowledge.ObserveHoardCount(current);
        if (current > previous)
            RecordNativeIntuitionState($"hoard-completed:{checkpoint}", force: true);
    }

    private static string DescribePomanderSlot(uint slotIndex)
    {
        return slotIndex switch
        {
            0 => "safety",
            1 => "sight",
            2 => "strength",
            3 => "steel",
            4 => "affluence",
            5 => "flight",
            6 => "alteration",
            7 => "purity",
            8 => "fortune",
            9 => "witching",
            10 => "serenity",
            11 => "unique-1",
            12 => "unique-2",
            13 => "intuition",
            14 => "raising",
            15 => "unique-3",
            _ => $"slot {slotIndex}"
        };
    }

    private unsafe int GetRemainingMobFloorCount(InstanceContentDeepDungeon* dd)
    {
        if (dd == null || !DungeonCatalog.TryGetByDungeonId(dd->DeepDungeonId, out var dungeon))
            return 0;

        int endFloor = dungeon.TerritoryFloorRanges.Count > 0
            ? dungeon.TerritoryFloorRanges.Values.Max(x => x.endFloor)
            : 0;

        int remaining = 0;
        for (int floor = dd->Floor + 1; floor <= endFloor; floor++)
        {
            if (!DeepDungeonHelper.IsBossFloor(dd->DeepDungeonId, (byte)floor))
            {
                remaining++;
            }
        }

        return remaining;
    }

    private long _seenRunOptionsVersion = -1;

    private unsafe void SyncLiveRunOptions(InstanceContentDeepDungeon* dd)
    {
        var version = _ctx.RunOptions.Version;
        if (_seenRunOptionsVersion == version)
            return;
        _seenRunOptionsVersion = version;
        var options = SnapshotRunOptions();
        RecordReplayEvent("run-options-synchronized", new
        {
            floor = dd->Floor,
            phase = _phase.ToString(),
            openGold = options.OpenGold,
            openSilver = options.OpenSilver,
            openBronze = options.OpenBronze,
            bandedEnabled = options.BandedEnabled
        });
        _siteDriver?.MarkRouteDirty();
    }

    private bool ShouldRunGeneralTick()
    {
        var now = DateTime.UtcNow;
        if (now - _lastGeneralTickAt < GeneralTickInterval) return false;
        _lastGeneralTickAt = now;
        return true;
    }
    internal bool PassageAttemptActive => _coordPipeline?.CurrentObjective == FloorObjectiveKind.EnterPassage;
    internal void ResetEngagedTargetProgress()
    {
        _coordRecoveryController = null;
        _ctx.ClearingTargetId = null;
    }

    private CoordPipeline? _coordPipeline;
    private RealNativeBoundary? _coordReal;
    // The once-per-decision-tick activity pair — driver, pipeline tick and
    // channel permissions all read THIS sample, never Service.Condition.
    private Coord.FloorActivity _normalActivity;
    // The combat controller observes the attack claim selected by the pipeline.
    private CombatPositionRecoveryController? _coordRecoveryController;
    private CombatActionEvidence? _combatActionEvidence;
    // First no-progress failure per floor+work target gets one bounded
    // replay snapshot — dedup key set.
    private readonly HashSet<string> _navFailureReported = new();
    private bool _coordTookOver;
    // Overcap relief request bound to the attempt that asked for it — the
    // pending-item channel is one-at-a-time so a reference pair suffices;
    // the resolution clears THIS attempt, never whichever head is current.
    private Coord.NavBitmap? _navBitmap;
    private Coord.CoverageSet? _coverage;
    private Coord.NavigationSurface? _navigation;
    private Coord.SiteSearchBoundary? _siteSearch;
    private Coord.ObjectiveSiteDriver? _siteDriver;
    private unsafe InstanceContentDeepDungeon* _scopeDd;
    private string _navBackend = "navigation";
    private int _navBitmapFloor = -1;

}
