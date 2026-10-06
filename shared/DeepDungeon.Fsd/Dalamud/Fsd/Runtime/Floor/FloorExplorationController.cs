using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
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
    internal delegate void HoardWaitRecorder(string eventType, int? remainingWaitMilliseconds = null);
    internal delegate void HoardWaitEnder(string outcome, string? expectedEventType = null);
    internal unsafe delegate void PassageExitRecorder(InstanceContentDeepDungeon* dd);
    internal unsafe delegate void ChaseFailureRecorder(InstanceContentDeepDungeon* dd, EnemyChaseAcquisitionFailure failure);
    internal delegate void PassageNavigationRecorder(string eventType, string result, bool usedActor, int passageRoomIndex, int playerRoom);
    private readonly RunContext _ctx;
    private readonly NavigationHelper _navHelper;
    private readonly NavigationDriver _navDriver;
    private readonly EnemyChaseHelper _chaseHelper;
    private readonly PomanderManager _pomanderManager;
    private readonly ChatWatchers? _chatWatchers;
    private readonly IRunTelemetryObserver? _runTelemetryObserver;
    private readonly PassageDestinationResolver _passageDestination;
    private CombatRecoveryBudget RecoveryBudget { get; } = new();
    private readonly Func<FloorPhase> _getPhase;
    private readonly Action<FloorPhase> _setPhase;
    private readonly Func<string> _getStatus;
    private readonly Action<string> _setStatus;
    private readonly Func<bool> _getNativeIntuition;
    private readonly Action CancelActiveMovement;
    private readonly Action<string, object> RecordReplayEvent;
    private readonly NativeStateRecorder RecordNativeIntuitionState;
    private readonly NativeStateSnapshotRecorder RecordNativeIntuitionSnapshot;
    private readonly HoardWaitRecorder RecordHoardEvidenceWait;
    private readonly HoardWaitEnder EndHoardEvidenceWait;
    private readonly Func<RunOptions> SnapshotRunOptions;
    private readonly Func<long> _nextRoomSearchRequestId;
    private readonly Func<int> ReadMobForbiddenZoneCount;
    private readonly Func<Vector3, object> ReadBossMovementDiagnostics;
    private readonly PassageExitRecorder RecordPassageExitDelayedByCombat;
    private readonly Action<string, EnemyChaseTarget> RecordChaseTargetEvent;
    private readonly ChaseFailureRecorder RecordChaseAcquisitionFailure;
    private readonly PassageNavigationRecorder RecordPassageNavigationEvent;
    private readonly Action ClearChaseAcquisitionFailure;
    private FloorPhase _phase { get => _getPhase(); set => _setPhase(value); }
    private string _status { get => _getStatus(); set => _setStatus(value); }
    private bool _nativeIntuitionActive => _getNativeIntuition();
    internal TaskPhase CurrentTaskPhase => _taskRunner?.Phase ?? TaskPhase.Idle;
    internal void ResetTaskRunner(bool cancelNavigation = true) => _taskRunner?.Reset(cancelNavigation);
    internal void ClearActiveWaypoint() => _activeWaypoint = null;
    internal void SuspendTask(bool suspended) => _taskRunner?.SetSuspended(suspended);
    private AutoPilotExecutor? _executor => Executor;
    private WaypointTaskRunner? _taskRunner => ActiveExecution?.TaskRunner;
    private string _blockedMovementOperation = string.Empty;
    private string _blockedTransitionOperation = string.Empty;
    private static readonly TimeSpan GeneralTickInterval = TimeSpan.FromMilliseconds(500);
    private ObjectiveArbiterDecision CurrentObjectiveDecision => Objectives.ObjectiveDecision;
    private bool AllowsChestSidecarInteraction => Objectives.HasObjectiveDecision &&
        Objectives.ObjectiveDecision.Channels.Interaction != CommandChannelPermission.Blocked;
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
        DetailedMapRunSnapshot detailedMap,
        RunFloorTelemetryTrace? runTelemetry, FloorItemExecutor items,
        RunContext context, NavigationHelper navigation, NavigationDriver navigationDriver,
        EnemyChaseHelper chase, PomanderManager pomanders, ChatWatchers? watchers,
        IRunTelemetryObserver? telemetryObserver, PassageDestinationResolver passageDestination,
        Func<FloorPhase> getPhase, Action<FloorPhase> setPhase,
        Func<string> getStatus, Action<string> setStatus, Func<bool> getNativeIntuition,
        Action cancelMovement, Action<string, object> record,
        NativeStateRecorder recordNative, HoardWaitRecorder recordHoardWait,
        HoardWaitEnder endHoardWait, Func<RunOptions> snapshotRunOptions,
        Func<long> nextRoomRequest, Func<int> readMobForbiddenZoneCount, Func<Vector3, object> readBossMovementDiagnostics,
        PassageExitRecorder recordPassageExit, Action<string, EnemyChaseTarget> recordChaseTarget,
        ChaseFailureRecorder recordChaseFailure, PassageNavigationRecorder recordPassageNavigation, Action clearChaseAcquisitionFailure, NativeStateSnapshotRecorder recordNativeSnapshot)
    {
        Generation = generation;
        DungeonId = dungeonId;
        Floor = floor;
        Kind = kind;
        ReadyAtUtc = readyAtUtc;
        NormalGraph = normalGraph;
        Objectives = new FloorObjectiveSession(generation);
        Executor = kind == FloorRuntimeKind.Normal
            ? new AutoPilotExecutor(Objectives.GetRoomProgress, detailedMap)
            : null;
        NativeIntuitionActive = nativeIntuitionActive;
        ObjectEvidence = new FloorObjectEvidenceTracker();
        RunTelemetry = runTelemetry;
        Items = items;
        _ctx = context; _navHelper = navigation; _navDriver = navigationDriver;
        _chaseHelper = chase; _pomanderManager = pomanders; _chatWatchers = watchers;
        context.CombatRecoveryBudget = RecoveryBudget;
        chase.IsTargetBlocked = context.IsCombatTargetSuppressed;
        _runTelemetryObserver = telemetryObserver; _passageDestination = passageDestination;
        _getPhase = getPhase; _setPhase = setPhase; _getStatus = getStatus; _setStatus = setStatus;
        _getNativeIntuition = getNativeIntuition; CancelActiveMovement = cancelMovement;
        RecordReplayEvent = record; RecordNativeIntuitionState = recordNative;
        RecordHoardEvidenceWait = recordHoardWait; EndHoardEvidenceWait = endHoardWait;
        SnapshotRunOptions = snapshotRunOptions; _nextRoomSearchRequestId = nextRoomRequest;
        ReadMobForbiddenZoneCount = readMobForbiddenZoneCount; ReadBossMovementDiagnostics = readBossMovementDiagnostics; RecordPassageExitDelayedByCombat = recordPassageExit;
        RecordChaseTargetEvent = recordChaseTarget; RecordChaseAcquisitionFailure = recordChaseFailure;
        RecordPassageNavigationEvent = recordPassageNavigation; ClearChaseAcquisitionFailure = clearChaseAcquisitionFailure; RecordNativeIntuitionSnapshot = recordNativeSnapshot;
    }

    public long Generation { get; }
    public uint DungeonId { get; }
    public byte Floor { get; }
    public FloorRuntimeKind Kind { get; }
    public DateTime ReadyAtUtc { get; }
    public NormalFloorGraphSnapshot? NormalGraph { get; private set; }
    public AutoPilotExecutor? Executor { get; }
    private bool? NativeIntuitionActive { get; set; }
    public FloorObjectiveSession Objectives { get; }
    private ObjectiveExecution? ActiveExecution { get; set; }
    public FloorPlanningSession PlanningState { get; } = new();
    public PendingIntuitionState PendingIntuition => Items.PendingIntuition;
    public FloorItemExecutor Items { get; }
    public FloorItemUseSession ItemUse => Items.Session;
    private MobMechanicsMovementGuard MobMechanicsGuard { get; } = new();
    private bool MobMechanicsYielding { get; set; }
    private DateTime NextMobMechanicsCheckUtc { get; set; }
    private DateTime NextMobMechanicsDiagnosticUtc { get; set; }
    private FloorSearchState SearchState { get; } = new();
    public FloorObjectEvidenceTracker ObjectEvidence { get; }
    public RunFloorTelemetryTrace? RunTelemetry { get; }
    private RunFloorStateCumulativePublisher? RunFloorStatePublisher { get; set; }
    private int LastObservedRoomIndex { get; set; } = -1;
    private int? ActiveRoomNavigationTarget { get; set; }
    private BandedRevealPending? BandedRevealExpectation { get; set; }
    private bool FarmingPassageItemConfirmed { get; set; }
    public FloorBossController Boss { get; private set; } = null!;
    public void AttachBoss(FloorBossController boss) => Boss = boss;
    public FloorSurveyController Survey { get; private set; } = null!;
    public void AttachSurvey(FloorSurveyController survey) => Survey = survey;
    public bool IsDisposed { get; private set; }

    private void ReplaceObjectiveExecution(FloorObjectiveKind objective, NavigationHelper navigation,
        RunContext context, EnemyChaseHelper chase, Action<string> setStatus, Action<string, object> record)
    {
        ActiveExecution?.Dispose();
        ActiveExecution = objective == FloorObjectiveKind.None
            ? null
            : new ObjectiveExecution(objective, navigation,
                new CombatPositionRecoveryController(context, navigation, chase, NormalGraph, RecoveryBudget, RunTelemetry, setStatus, record));
    }


    public void Dispose()
    {
        if (IsDisposed)
            return;

        IsDisposed = true;
        NormalGraph = null;
        NativeIntuitionActive = null;
        LastObservedRoomIndex = -1;
        ActiveRoomNavigationTarget = null;
        BandedRevealExpectation = null;
        Survey.Dispose();
        Items.Dispose();
        ActiveExecution?.Dispose();
        ActiveExecution = null;
        ObjectEvidence.Dispose();
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

    private static bool IsSearchObjective(FloorObjectiveKind objective) =>
        objective is FloorObjectiveKind.OpenVisibleBandedChest or
            FloorObjectiveKind.CompleteKnownHoard or
            FloorObjectiveKind.DiscoverHoard or FloorObjectiveKind.OpenPlannedChest;

    private bool ShouldContinuePlannedRouteForPassageActivation(FloorObjectiveKind objective) =>
        _ctx?.ControlledPtSurvey == null &&
        objective == FloorObjectiveKind.ActivatePassage &&
        _ctx?.Duty.PassageOpen != true &&
        _executor?.PlannedRouteCount > 0;

    private bool ClearingMovementOwnedByCurrentObjective()
    {
        return CurrentObjectiveDecision.PrimaryObjective is
            FloorObjectiveKind.ActivatePassage or
            FloorObjectiveKind.FinishCombatBeforePassage;
    }

    private bool HasActiveSearchExecution()
    {
        return _activeWaypoint.HasValue ||
               (_taskRunner != null && _taskRunner.Phase != TaskPhase.Idle) ||
               _executor?.RoomContext != null ||
               (this.ActiveExecution?.ObjectiveRecords.Count ?? 0) > 0;
    }

    internal unsafe void UpdateFloorActive(InstanceContentDeepDungeon* dd)
    {
        if (YieldToMobMechanics())
        {
            ActiveExecution?.Recovery.YieldMovement();
            if (Service.LocalPlayer is { } player && ShouldRunGeneralTick()) SyncLiveRunOptions(dd, player);
            return;
        }
        if (this.Survey.TryUpdateControlledActive(dd, CurrentObjectiveDecision.PrimaryObjective))
            return;

        var objective = CurrentObjectiveDecision.PrimaryObjective;
        if (!EnsureObjectiveExecution(objective))
            return;
        if (TryActivateVisibleBandedObjective(dd))
            return;

        bool continuePlannedRoute = ShouldContinuePlannedRouteForPassageActivation(objective);
        if (!IsSearchObjective(objective) && !continuePlannedRoute && HasActiveSearchExecution() && !StopActiveSearchExecution(objective))
            return;

        if (IsSearchObjective(objective) || continuePlannedRoute)
        {
            UpdateSearchMechanics(dd);
            return;
        }

        if (objective is FloorObjectiveKind.ActivatePassage or FloorObjectiveKind.FinishCombatBeforePassage)
        {
            if (_ctx?.FarmingPlan?.ReusesSave == true)
            {
                CancelActiveMovement();
                _ctx.ClearPreferredAggroTarget();
                _status = "速刷：等待跳層道具生效／脫離戰鬥";
                return;
            }
            UpdateClearingMechanics(dd);
            return;
        }

        if (objective == FloorObjectiveKind.EnterPassage)
        {
            UpdatePassageNavigation(dd);
            return;
        }

        CancelActiveMovement();
        _chaseHelper.Reset();
        ResetPatrolPlan();
        _ctx?.ClearPreferredAggroTarget();
        _status = $"Floor active, waiting for objective ({CurrentObjectiveDecision.PrimaryObjective})";
    }

    private bool EnsureObjectiveExecution(FloorObjectiveKind objective)
    {
        if (this.IsDisposed || _navHelper == null)
            return false;
        if (this.ActiveExecution?.Objective == objective)
            return true;
        if (this.ActiveExecution != null &&
            HasActiveSearchExecution() &&
            ShouldContinuePlannedRouteForPassageActivation(objective))
        {
            this.ActiveExecution.Objective = objective;
            return true;
        }
        if (this.ActiveExecution != null)
        {
            if (HasActiveSearchExecution())
            {
                if (!StopActiveSearchExecution(objective))
                    return false;
            }
            else
            {
                CancelActiveMovement();
            }
            _chaseHelper.Reset();
            _ctx?.ClearPreferredAggroTarget();
        }

        this.ReplaceObjectiveExecution(objective, _navHelper, _ctx!, _chaseHelper, status => _status = status, RecordReplayEvent);
        return true;
    }

    private unsafe bool TryActivateVisibleBandedObjective(InstanceContentDeepDungeon* dd)
    {
        if (CurrentObjectiveDecision.PrimaryObjective != FloorObjectiveKind.OpenVisibleBandedChest ||
            (_activeWaypoint ?? _executor?.CurrentWaypoint)?.Type == RoomObjectiveType.ChestBanded)
        {
            return false;
        }
        var player = Service.LocalPlayer;
        if (this.NormalGraph == null || player == null || this.ObjectEvidence.Current is not { } evidence)
        {
            _status = "Waiting to activate visible banded objective...";
            return true;
        }
        if (!BandedChestLocator.TryFindNearestToPlayer(evidence, out var bandedPosition))
        {
            _status = "Waiting for banded chest evidence...";
            return true;
        }
        if (!bandedPosition.HasValue)
            return false;

        int roomIndex = RoomGraph.GetRoomIndexForPosition(
            dd,
            bandedPosition.Value,
            this.NormalGraph.ReachableRooms,
            -1);
        if (roomIndex < 0)
        {
            _status = "Waiting to resolve visible banded chest room...";
            return true;
        }

        HandleVisibleBandedDetection(dd, player, roomIndex, bandedPosition.Value);
        return true;
    }

    internal bool RequireTransitionPermission(string operation, FloorObjectiveKind requiredObjective)
    {
        var decision = this.Objectives.ObjectiveDecision;
        bool allowed = this.Objectives.HasObjectiveDecision == true &&
                       decision.PrimaryObjective == requiredObjective &&
                       decision.Channels.Transition == CommandChannelPermission.PrimaryObjective;
        if (allowed)
        {
            ClearPermissionBlock(ref _blockedTransitionOperation, "Transition");
            return true;
        }

        RecordPermissionBlock(ref _blockedTransitionOperation, "Transition", operation, requiredObjective);
        _status = $"Waiting: transition permission blocked for {operation} ({decision.PrimaryObjective})";
        return false;
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
        _navDriver?.Cancel();
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
        _blockedTransitionOperation = string.Empty;
    }

    public void TickInteractionChannel(IFramework _)
    {
        if (this.ItemUse.DispatchedThisUpdate == true ||
            this.IsDisposed ||
            !AllowsChestSidecarInteraction)
        {
            return;
        }

        var waypoint = _activeWaypoint;
        var evidence = this.ObjectEvidence.Current;
        if (!waypoint.HasValue || !IsChestWaypoint(waypoint.Value) || evidence?.Available != true)
            return;

        if (waypoint.Value.Type == RoomObjectiveType.ChestBanded && _ctx?.RunOptions.Current.DiscoveryOnly == true)
            return;
        if (_ctx?.ChestInteraction.TryInteract(ActiveChestAttempt, evidence, out var snapshot, out bool retry) == true)
        {
            if (!retry || _ctx?.Configuration.AggressiveChestInteraction != true)
            {
                this.ObjectEvidence.Invalidate();
                RecordChestInteractionStarted(waypoint.Value, snapshot, retry);
            }
        }
    }

    internal unsafe void RefreshObjectiveDecision(InstanceContentDeepDungeon* dd)
    {
        if (this.IsDisposed ||
            this.Floor != dd->Floor ||
            this.DungeonId != dd->DeepDungeonId)
        {
            return;
        }
        var objectEvidence = this.Kind == FloorRuntimeKind.Normal
            ? this.ObjectEvidence.Current
            : null;
        if (this.Kind == FloorRuntimeKind.Normal && objectEvidence?.Available != true)
        {
            this.Objectives.ClearObjectiveDecision();
            return;
        }

        var executor = _executor;
        var evidenceState = executor?.HoardEvidenceState ?? HoardEvidenceState.Disabled;
        var activeWaypoint = _activeWaypoint ?? executor?.CurrentWaypoint;
        bool passageOpen = _ctx?.Duty.PassageOpen == true;
        bool combatInProgress = Service.Condition[ConditionFlag.InCombat];
        bool suppressCombat = _ctx?.FarmingPlan?.ReusesSave == true;
        bool routineCombatAllowed = !suppressCombat && (combatInProgress || !passageOpen);
        bool chestInteractionAllowed =
            objectEvidence != null &&
            _activeWaypoint.HasValue &&
            IsChestWaypoint(_activeWaypoint.Value);
        bool visibleBandedChest =
            activeWaypoint?.Type == RoomObjectiveType.ChestBanded ||
            executor?.HasPendingBandedWaypoint == true ||
            _searchExecutionKind == SearchExecutionKind.BandedReentry;
        if (!visibleBandedChest &&
            executor?.ConfigSnapshot.BandedEnabled == true &&
            !executor.HasOpenedHoardThisFloor &&
            objectEvidence != null &&
            BandedChestLocator.TryFindNearestToPlayer(objectEvidence, out var visibleBandedPosition))
        {
            visibleBandedChest = visibleBandedPosition.HasValue;
        }
        bool bandedRevealPending = IsBandedRevealExpectationPending();
        bool hoardWorkResolved =
            this.Kind == FloorRuntimeKind.Boss ||
            executor?.IsHoardWorkResolved == true;
        bool mandatoryHoardTerminal =
            hoardWorkResolved && !bandedRevealPending && !visibleBandedChest;
        bool knownOrConfirmedHoard =
            !hoardWorkResolved &&
            evidenceState is HoardEvidenceState.IntuitionDirect or HoardEvidenceState.IntuitionWaitingForIndicator;
        bool requiredHoardDiscovery =
            !hoardWorkResolved &&
            evidenceState is HoardEvidenceState.BlindSearch or HoardEvidenceState.IntuitionActiveUnconfirmed;
        bool passageActivationRequired =
            this.Kind == FloorRuntimeKind.Normal &&
            !passageOpen &&
            hoardWorkResolved &&
            evidenceState != HoardEvidenceState.IntuitionPending;
        if (_ctx?.ControlledPtSurvey != null && this.Kind == FloorRuntimeKind.Normal)
        {
            visibleBandedChest = false;
            knownOrConfirmedHoard = false;
            requiredHoardDiscovery = false;
            mandatoryHoardTerminal = this.Survey.ControlledIntuitionResolved;
            passageActivationRequired = !passageOpen && this.Survey.ControlledIntuitionResolved;
        }
        var snapshot = new ObjectiveArbiterSnapshot(
            BossObjective: this.Kind == FloorRuntimeKind.Boss,
            VisibleBandedChest: visibleBandedChest,
            KnownOrConfirmedHoard: knownOrConfirmedHoard,
            RequiredHoardDiscovery: requiredHoardDiscovery,
            MandatoryHoardTerminal: mandatoryHoardTerminal,
            PassageOpen: passageOpen,
            PassageActivationRequired: passageActivationRequired,
            CombatInProgress: combatInProgress,
            RoutineCombatAllowed: routineCombatAllowed,
            ActiveChestInteraction: chestInteractionAllowed,
            SuppressCombat: suppressCombat,
            RequiredChestWork: _ctx?.RunOptions.Current.HarvestChestsRequired == true &&
                (executor?.PlannedRouteCount > 0 || executor?.RoomContext != null ||
                 _activeWaypoint.HasValue || (_taskRunner != null && _taskRunner.Phase != TaskPhase.Idle)));
        if (!this.Objectives.RefreshObjectiveDecision(
                snapshot,
                objectEvidence?.Version ?? 0,
                _ctx?.RunOptions.Version ?? 0,
                this.Objectives.ObjectiveLedger.Version,
                out var decision))
            return;

        RecordReplayEvent("objective-arbiter-decision", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            primaryObjective = decision.PrimaryObjective.ToString(),
            movement = decision.Channels.Movement.ToString(),
            combat = decision.Channels.Combat.ToString(),
            interaction = decision.Channels.Interaction.ToString(),
            interactionOwner = "ChestSidecar",
            transition = decision.Channels.Transition.ToString(),
            evidenceState = evidenceState.ToString(),
            bossObjective = this.Kind == FloorRuntimeKind.Boss,
            visibleBandedChest,
            bandedRevealPending,
            hoardWorkResolved,
            knownOrConfirmedHoard,
            requiredHoardDiscovery,
            mandatoryHoardTerminal,
            passageActivationRequired,
            passageOpen,
            combatInProgress,
            routineCombatAllowed,
            activeChestInteraction = chestInteractionAllowed
        });
    }

    internal void RequestPlanRefresh(string reason)
    {
        if (this.Kind != FloorRuntimeKind.Normal || this.IsDisposed)
            return;

        PlanningState.RequestRefresh(this.Floor, this.DungeonId, reason);
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
        if (!ReadyFloorIntuitionPlanner.ShouldRequestPlanRefresh(previous, _nativeIntuitionActive))
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

    internal void MarkPlanRefreshConsumed(long consumedVersion)
    {
        if (this.Kind == FloorRuntimeKind.Normal &&
            !this.IsDisposed &&
            this.Floor == PlanningState.PendingEvidenceFloor &&
            this.DungeonId == PlanningState.PendingEvidenceDungeonId)
        {
            PlanningState.Acknowledge(consumedVersion);
        }
    }

    internal unsafe bool TryReconcileDelayedHoardEvidence(InstanceContentDeepDungeon* dd)
    {
        if (this.IsDisposed ||
            this.Kind != FloorRuntimeKind.Normal ||
            _phase != FloorPhase.FloorActive ||
            HasActiveSearchExecution() ||
            _executor == null)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (PlanningState.TryBeginLateEvidencePoll(now, GeneralTickInterval))
        {
            if (!RefreshCachedHoardIndicator(dd))
            {
                _status = "Waiting for hoard indicator evidence...";
                return true;
            }
        }

        bool matchesCurrentFloor =
            PlanningState.PendingEvidenceFloor == this.Floor &&
            PlanningState.PendingEvidenceDungeonId == this.DungeonId;
        var gate = LateHoardEvidencePlanner.Decide(new LateHoardEvidenceSnapshot
        {
            PendingVersion = PlanningState.PendingEvidenceVersion,
            ReconciledVersion = PlanningState.ReconciledEvidenceVersion,
            StableNormalFloor = true,
            EvidenceMatchesCurrentFloor = matchesCurrentFloor,
            FloorActiveAllowsReplan = true,
            MandatoryHoardWorkResolved = _executor.IsHoardWorkResolved,
            RefreshedPlan = Array.Empty<RoomPlanEntry>()
        });
        if (!gate.ShouldRegeneratePlan)
        {
            if (PlanningState.PendingEvidenceVersion > PlanningState.ReconciledEvidenceVersion && !matchesCurrentFloor)
            {
                PlanningState.DiscardPending();
                RecordReplayEvent("late-hoard-evidence-ignored", new
                {
                    floor = this.Floor,
                    floorGeneration = this.Generation,
                    dungeonId = this.DungeonId,
                    evidenceFloor = PlanningState.PendingEvidenceFloor,
                    evidenceDungeonId = PlanningState.PendingEvidenceDungeonId,
                    evidenceVersion = PlanningState.PendingEvidenceVersion,
                    reason = "noncurrent-floor"
                });
            }
            return false;
        }

        var player = Service.LocalPlayer;
        var normalGraph = this.NormalGraph;
        if (player == null || normalGraph == null)
            return false;

        long evidenceVersion = PlanningState.PendingEvidenceVersion;
        string evidenceReason = PlanningState.PendingEvidenceReason;
        _executor.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
        if (!_executor.HasPlanningSnapshot)
        {
            _status = "Waiting for floorset or player-room evidence...";
            return true;
        }

        bool bandedEligible = _executor.ConfigSnapshot.BandedEnabled && !_executor.HasOpenedHoardThisFloor;
        Vector3? visibleBanded = null;
        if (bandedEligible && !BandedChestLocator.TryFindNearestToPlayer(this.ObjectEvidence.Current!, out visibleBanded))
        {
            _status = "Waiting for banded chest evidence...";
            return true;
        }
        bool pendingBanded = bandedEligible &&
            (_searchExecutionKind == SearchExecutionKind.BandedReentry ||
             _activeWaypoint?.Type == RoomObjectiveType.ChestBanded ||
             _executor.HasPendingBandedWaypoint);
        var decision = LateHoardEvidencePlanner.Decide(new LateHoardEvidenceSnapshot
        {
            PendingVersion = evidenceVersion,
            ReconciledVersion = PlanningState.ReconciledEvidenceVersion,
            StableNormalFloor = true,
            EvidenceMatchesCurrentFloor = true,
            FloorActiveAllowsReplan = true,
            MandatoryHoardWorkResolved = _executor.IsHoardWorkResolved,
            PendingOrVisibleBandedWork = pendingBanded || visibleBanded.HasValue,
            RefreshedPlan = _executor.SnapshotPlannedRoute()
        });

        bool startedVisibleBanded = false;
        if (decision.ShouldResumeHoardWork && visibleBanded.HasValue)
        {
            int bandedRoom = RoomGraph.GetRoomIndexForPosition(
                dd,
                visibleBanded.Value,
                normalGraph.ReachableRooms,
                -1);
            if (bandedRoom < 0)
            {
                _status = "Waiting to resolve visible banded chest room...";
                return true;
            }
            _executor.ClearRoomContext();
            startedVisibleBanded = _executor.StartBandedOnlyRoomSearch(dd, bandedRoom, player.Position, visibleBanded.Value);
            if (!startedVisibleBanded && !decision.HasRequiredHoardWork)
            {
                CancelActiveMovement();
                _status = "Visible banded chest could not be queued";
                RecordReplayEvent("late-hoard-evidence-reconcile-failed", new
                {
                    floor = this.Floor,
                    floorGeneration = this.Generation,
                    phase = _phase.ToString(),
                    evidenceVersion,
                    evidenceReason,
                    bandedRoom,
                    reason = "visible-banded-room-search-build-failed"
                });
                return true;
            }
        }

        MarkPlanRefreshConsumed(evidenceVersion);
        if (decision.BlockUnroutableMandatoryWork)
        {
            _status = $"Waiting for mandatory hoard evidence ({_executor.HoardEvidenceState})";
            RecordHoardEvidenceWait("late-hoard-evidence-waiting-unresolved");
            RecordReplayEvent("late-hoard-evidence-reconciled", new
            {
                floor = this.Floor,
                floorGeneration = this.Generation,
                phase = _phase.ToString(),
                evidenceVersion,
                evidenceReason,
                hoardEvidenceState = _executor.HoardEvidenceState.ToString(),
                reason = "non-routable-mandatory-work"
            });
            return true;
        }

        if (!decision.ShouldResumeHoardWork)
        {
            RecordReplayEvent("late-hoard-evidence-reconciled", new
            {
                floor = this.Floor,
                floorGeneration = this.Generation,
                phase = _phase.ToString(),
                evidenceVersion,
                evidenceReason,
                hoardEvidenceState = _executor.HoardEvidenceState.ToString(),
                reason = "no-required-hoard-work"
            });
            return false;
        }

        CancelActiveMovement();
        ResetPatrolPlan();
        _ctx?.ClearPreferredAggroTarget();
        _chaseHelper.Reset();
        _activeWaypoint = null;
        _searchExecutionKind = startedVisibleBanded
            ? SearchExecutionKind.BandedReentry
            : SearchExecutionKind.PlannedRoom;
        if (startedVisibleBanded)
            ClearPostRoomPomanderRetry();
        _status = startedVisibleBanded
            ? "Late hoard evidence revealed a banded chest"
            : "Late hoard evidence reopened search";
        string reentryReason = startedVisibleBanded
            ? "visible-banded-work"
            : pendingBanded
                ? "pending-banded-work"
                : "required-hoard-work";
        RecordReplayEvent("late-hoard-evidence-reconciled", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            evidenceVersion,
            evidenceReason,
            hoardEvidenceState = _executor.HoardEvidenceState.ToString(),
            reason = reentryReason
        });
        RecordReplayEvent("floor-active-mechanic-selected", new
        {
            mechanic = "Search",
            reason = $"late-hoard-evidence-{reentryReason}"
        });
        return true;
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

        ResolveCurrentFloorIntuitionTimeoutIfNeeded(dd);

        var normalGraph = this.NormalGraph;
        if (normalGraph == null)
        {
            _status = "Waiting for room graph...";
            return;
        }

        if (this.Survey is
            {
                InheritedIntuitionAttemptId: > 0,
                InheritedIntuitionDecision: null
            })
        {
            _status = "Waiting for inherited Intuition result";
            return;
        }

        if (_ctx?.ControlledPtSurvey != null)
        {
            if (!PlanningState.SetupPlanGenerated)
            {
                if (!ShouldRunGeneralTick())
                    return;

                _executor!.ResetForFloor(dd, SnapshotRunOptions());
                PlanningState.ObserveHoardCount(dd->HoardCount);
                PlanningState.MarkSetupPlanGenerated();
            }

            if (this.Survey.ControlledIntuitionResolved != true)
            {
                _status = "Controlled PT: waiting for terminal Intuition semantics";
                return;
            }

            _phase = FloorPhase.FloorActive;
            _status = "Controlled PT floor active";
            RecordReplayEvent("floor-lifecycle-transition", new
            {
                from = FloorPhase.FloorSetup.ToString(),
                to = FloorPhase.FloorActive.ToString(),
                reason = "controlled-intuition-terminal"
            });
            return;
        }

        if (!PlanningState.SetupPlanGenerated)
        {
            if (!ShouldRunGeneralTick())
                return;

            _executor!.ResetForFloor(dd, SnapshotRunOptions());
            PlanningState.ObserveHoardCount(dd->HoardCount);
            if (EntryIncenseWindowOpen() &&
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
            if (!RefreshCachedHoardIndicator(dd))
            {
                _status = "Waiting for hoard indicator evidence...";
                return;
            }
            long evidenceVersion = PlanningState.PendingEvidenceVersion;
            _executor.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
            MarkPlanRefreshConsumed(evidenceVersion);
            PlanningState.MarkSetupPlanGenerated(_executor.HasPlanningSnapshot);
            if (!PlanningState.SetupPlanGenerated)
            {
                _status = "Waiting for floor planning evidence...";
                return;
            }
            RecordNativeIntuitionSnapshot("first-floor-plan-generated", force: true, nativeStateAvailable: true, nativeIntuitionActive: _nativeIntuitionActive);
            RecordReplayEvent("floor-plan-generated", BuildPlanReplayPayload(dd->Floor, "floor-setup-generated"));
            PublishInitialRunFloorState(dd, player.Position);
        }
        else
        {
            if (!RefreshCachedHoardIndicator(dd))
            {
                _status = "Waiting for hoard indicator evidence...";
                return;
            }
            if (PlanningState.RefreshRequested)
            {
                long evidenceVersion = PlanningState.PendingEvidenceVersion;
                _executor!.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
                MarkPlanRefreshConsumed(evidenceVersion);
                RecordReplayEvent("floor-plan-regenerated-evidence", BuildPlanReplayPayload(dd->Floor, "floor-setup-evidence-refresh"));
            }
        }

        var executor = _executor;
        if (executor == null)
        {
            _status = "Waiting for floor executor...";
            return;
        }

        if (executor.HoardEvidenceState == HoardEvidenceState.IntuitionPending ||
            (executor.IsComplete && !executor.IsHoardWorkResolved))
        {
            _status = $"Waiting for hoard evidence ({executor.HoardEvidenceState})";
            RecordHoardEvidenceWait("floor-setup-waiting-hoard-evidence");
            return;
        }
        EndHoardEvidenceWait("floor-setup-wait-ended", "floor-setup-waiting-hoard-evidence");

        _phase = FloorPhase.FloorActive;
        _status = executor.IsComplete ? "Floor active" : "Starting floor objective";
        RecordReplayEvent("floor-lifecycle-transition", new
        {
            from = FloorPhase.FloorSetup.ToString(),
            to = FloorPhase.FloorActive.ToString(),
            reason = executor.IsComplete ? "floor-ready-no-initial-route" : "floor-ready-plan-available"
        });
    }

    internal unsafe bool ResolveCurrentFloorIntuitionTimeoutIfNeeded(InstanceContentDeepDungeon* dd)
    {
        if (dd == null || _chatWatchers == null)
            return false;

        if (!PendingIntuition.TryGetCurrentFloorUseElapsedMilliseconds(dd->Floor, DateTime.UtcNow, out var elapsedMilliseconds))
            return false;

        var decision = CurrentIntuitionResolutionPlanner.Decide(new CurrentIntuitionResolutionSnapshot
        {
            UsedIntuitionThisFloor = _chatWatchers.UsedIntuitionThisFloor,
            ChatSaysHoard = _chatWatchers.ChatSaysHoard,
            ChatSaysNoHoard = _chatWatchers.ChatSaysNoHoard,
            ElapsedMillisecondsSinceUse = elapsedMilliseconds,
            ResolutionWindowMilliseconds = FloorSurveyController.IntuitionResolutionWindowMilliseconds
        });

        if (decision.Kind != CurrentIntuitionResolutionKind.Wait ||
            decision.RemainingWaitMilliseconds > 0 ||
            !PendingIntuition.TryMarkOverdueRecorded())
        {
            return false;
        }

        RecordReplayEvent("current-intuition-evidence-overdue", new
        {
            floor = dd->Floor,
            windowMilliseconds = FloorSurveyController.IntuitionResolutionWindowMilliseconds,
            reason = "authoritative-7272-or-7273-still-required"
        });
        return false;
    }

    internal bool HandleNoHoardEvidenceInvalidated(string reason)
    {
        var currentPlanEntry = _executor?.CurrentPlanEntry;
        bool activeChestObjective =
            this.ActiveExecution?.ObjectiveRecords.Any(
                execution => execution.Category == RoomObjectiveCategory.Chests) == true;
        var decision = HoardWorkInvalidationPlanner.Decide(new HoardWorkInvalidationSnapshot
        {
            NoHoardEvidenceActive = true,
            HasCachedHoardIndicator = _executor?.CachedHoardIndicatorPos.HasValue == true,
            ActiveWaypointPresent = _activeWaypoint.HasValue,
            ActiveWaypointIsTrap = _activeWaypoint?.Type == RoomObjectiveType.Trap,
            CurrentPlanShouldProbeHoard = currentPlanEntry?.ShouldProbeHoard == true,
            CurrentPlanShouldSearchChests = currentPlanEntry?.ShouldSearchChests == true,
            ActiveChestObjectivePresent = activeChestObjective,
            CurrentPlanShouldVisitForIntel = currentPlanEntry?.ShouldVisitForIntel == true
        });

        if (decision.ClearCachedIndicator && _executor?.ClearCachedHoardIndicator() == true)
        {
            RecordReplayEvent("cached-hoard-indicator-cleared", new
            {
                reason
            });
        }

        if (decision.AbortActiveHoardWork)
        {
            int roomIndex = _executor?.RoomContext?.RoomIndex ?? currentPlanEntry?.RoomIndex ?? -1;
            var objectiveOutcome = new RoomObjectiveOutcomeResult(
                decision.HoardOutcome,
                decision.ChestsOutcome,
                decision.IntelOutcome);
            if (!TryApplyActiveObjectiveOutcomes(roomIndex, objectiveOutcome, reason))
                return true;
            CancelActiveMovement();
            _executor?.ClearRoomContext();
            ClearRoomIntelSettle();
            _status = "No-hoard evidence invalidated hoard work";
            RecordReplayEvent("hoard-work-aborted-by-no-hoard", new
            {
                reason
            });
        }

        return decision.RequestPlanRefresh;
    }

    internal object BuildPlanReplayPayload(byte floor, string reason, int? roomIndex = null)
    {
        var plan = _executor?.PlannedRoute ?? Array.Empty<RoomPlanEntry>();
        var trace = _executor?.LastPlanTrace ?? default;
        return new
        {
            floor,
            reason,
            roomIndex,
            phase = _phase.ToString(),
            status = _status,
            hoardEvidenceState = _executor?.HoardEvidenceState.ToString() ?? string.Empty,
            currentTargetRoomIndex = _executor?.CurrentTargetRoomIndex,
            planCount = plan.Count,
            roomPlan = plan.Select(entry => new
            {
                entry.RoomIndex,
                entry.ShouldProbeHoard,
                entry.ShouldSearchChests,
                entry.ShouldVisitForIntel,
                hoardEvidenceState = entry.HoardEvidenceState.ToString()
            }).ToArray(),
            candidates = trace.Candidates?.Select(candidate => new
            {
                candidate.RoomIndex,
                candidate.Eligible,
                candidate.ShouldProbeHoard,
                candidate.ShouldSearchChests,
                candidate.ShouldVisitForIntel,
                hoardEvidenceState = candidate.HoardEvidenceState.ToString(),
                candidate.BasePriority,
                candidate.Reason
            }).ToArray() ?? [],
            selections = trace.Selections?.Select(selection => new
            {
                selection.Step,
                selection.FromRoomIndex,
                selection.SelectedRoomIndex,
                selection.Distance,
                selection.PassageDistance,
                selection.BasePriority
            }).ToArray() ?? [],
            rejectionReason = trace.RejectionReason ?? string.Empty
        };
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
        if (this.ActiveRoomNavigationTarget == roomIndex)
        {
            this.ActiveRoomNavigationTarget = null;
        }

        RecordReplayEvent("room-entered", new
        {
            floor = dd->Floor,
            floorGeneration = this.Generation,
            roomIndex,
            phase = _phase.ToString(),
            status = _status
        });
    }

    internal unsafe NavDriveResult NavigateToRoom(InstanceContentDeepDungeon* dd, int targetRoom, global::Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
    {
        if (this.IsDisposed ||
            this.Floor != dd->Floor ||
            this.DungeonId != dd->DeepDungeonId)
        {
            return NavDriveResult.Failed;
        }

        if (!MapPos.TryGetRoomCenter(dd, targetRoom, out var target))
        {
            _status = $"Can't resolve room {targetRoom} center";
            return NavDriveResult.Failed;
        }
        int playerRoom = GameState.RoomGraph.GetLocalPlayerRoomIndex(dd);

        var result = _navDriver!.Drive(target, player.Position, 1.2f, dd, playerRoom, targetRoom);

        switch (result)
        {
            case NavDriveResult.Moving:
                if (this.ActiveRoomNavigationTarget != targetRoom)
                {
                    this.ActiveRoomNavigationTarget = targetRoom;
                    RecordReplayEvent("room-navigation-started", new
                    {
                        floor = dd->Floor,
                        floorGeneration = this.Generation,
                        fromRoom = playerRoom,
                        targetRoom,
                        mode = "Moving",
                        stage = _navDriver.StageLabel
                    });
                }
                _status = _navDriver.IsStaging
                    ? $"Navigating to room {targetRoom} ({_navDriver.StageLabel})"
                    : $"Navigating to room {targetRoom}";
                break;
            case NavDriveResult.Staging:
                if (this.ActiveRoomNavigationTarget != targetRoom)
                {
                    this.ActiveRoomNavigationTarget = targetRoom;
                    RecordReplayEvent("room-navigation-started", new
                    {
                        floor = dd->Floor,
                        floorGeneration = this.Generation,
                        fromRoom = playerRoom,
                        targetRoom,
                        mode = "Staging",
                        stage = _navDriver.StageLabel
                    });
                }
                _status = $"Staging: {_navDriver.StageLabel}";
                break;
            case NavDriveResult.Arrived:
                this.ActiveRoomNavigationTarget = null;
                break;
            case NavDriveResult.StuckRetrying:
                this.RunTelemetry?.ObserveNavigationIssue();
                _status = $"Repathing ({_navDriver.StuckRetryCount}/3)";
                break;
            case NavDriveResult.Failed:
                this.RunTelemetry?.ObserveNavigationIssue();
                this.ActiveRoomNavigationTarget = null;
                Service.Log.Warning($"[FloorPhase] Room {targetRoom} navigation failed");
                _navDriver.Cancel();
                break;
        }
        return result;
    }

    internal void HandleDirectNavState(NavigationState state, string context)
    {
        switch (state)
        {
            case NavigationState.Arrived:
                _status = $"{context}: arrived";
                break;
            case NavigationState.StuckRepathing:
                this.RunTelemetry?.ObserveNavigationIssue();
                _status = $"{context}: stuck ({_navHelper?.StuckRetryCount ?? 0}/3)";
                break;
            case NavigationState.StuckGiveUp:
                this.RunTelemetry?.ObserveNavigationIssue();
                _status = $"{context}: failed";
                break;
            case NavigationState.Failed:
                this.RunTelemetry?.ObserveNavigationIssue();
                _status = $"{context}: navigation unavailable";
                break;
        }
    }
    internal const uint PtHasteStatusId = 4718;
    private enum SearchExecutionKind
    {
        PlannedRoom,
        BandedReentry
    }

    private sealed class ObjectiveExecution : IDisposable
    {
        public ObjectiveExecution(FloorObjectiveKind objective, NavigationHelper navigation, CombatPositionRecoveryController recovery)
        {
            Objective = objective;
            TaskRunner = new WaypointTaskRunner(navigation);
            Recovery = recovery;
        }

        public FloorObjectiveKind Objective { get; set; }
        public WaypointTaskRunner TaskRunner { get; }
        public RoomWaypoint? ActiveWaypoint { get; set; }
        public RunWaypointTelemetryTrace? WaypointTelemetry { get; set; }
        public ChestInteractionAttempt? ChestAttempt { get; set; }
        public SearchExecutionKind SearchKind { get; set; }
        public List<ActiveObjectiveExecution> ObjectiveRecords { get; } = new(3);
        public List<int> PatrolRooms { get; } = new();
        public int PatrolIndex;
        public int CurrentPatrolRoom = -1;
        public CombatPositionRecoveryController Recovery { get; }

        public void Dispose()
        {
            Recovery.Dispose();
            TaskRunner.Reset(cancelNavigation: false);
            ActiveWaypoint = null;
            WaypointTelemetry = null;
            ChestAttempt = null;
            ObjectiveRecords.Clear();
            PatrolRooms.Clear();
        }
    }

    private enum RoomFinishPomanderOutcome
    {
        NotNeeded,
        PendingRetry
    }

    private sealed class PostRoomPomanderRetry
    {
        public int FinishedRoomIndex;
        public DateTime ExpiresAt;
    }

    private readonly record struct BandedRevealPending(DateTime ExpiresAtUtc, long EvidenceSequence);

    private readonly record struct TrapObservation(DateTime TimestampUtc, int RoomIndex, int WaypointIndex, SearchExecutionKind ExecutionKind, Vector3 Position);

    private sealed class FloorSearchState
    {
        public PostRoomPomanderRetry? PostRoomPomanderRetry;
        public TrapObservation? LastTrapTriggered;
        public TrapObservation? LastTrapCompleted;
        public int IntelSettleRoomIndex = -1;
        public DateTime IntelSettleUntil = DateTime.MinValue;
        public DateTime ObjectiveRetryNotBefore = DateTime.MinValue;
        public bool MandatoryObjectiveBlocked;
        public int MandatoryObjectiveBlockedRoom = -1;
        public ObjectiveIdentity? MandatoryObjectiveBlockedIdentity;
        public RoomObjectiveCategory MandatoryObjectiveBlockedCategory;
        public FloorObjectiveKind MandatoryObjectiveBlockedKind;
        public long MandatoryObjectiveBlockedEvidenceVersion;
        public bool ObjectiveOutcomeRejected;
        public DateTime RoomEvidenceRetryAt = DateTime.MinValue;
    }

    private RoomWaypoint? _activeWaypoint
    {
        get => this.ActiveExecution?.ActiveWaypoint;
        set
        {
            if (this.ActiveExecution != null)
            {
                if (this.ActiveExecution.ActiveWaypoint.HasValue &&
                    (!value.HasValue ||
                     this.ActiveExecution.ActiveWaypoint.Value != value.Value))
                {
                    EndActiveWaypointTelemetry(RunWaypointTerminalOutcome.Aborted, "WaypointCleared");
                }
                this.ActiveExecution.ActiveWaypoint = value;
                if (!value.HasValue || !IsChestWaypoint(value.Value))
                    this.ActiveExecution.ChestAttempt = null;
            }
            else if (value.HasValue)
                throw new InvalidOperationException("Cannot set a waypoint without an active objective execution.");
        }
    }
    private SearchExecutionKind _searchExecutionKind
    {
        get => this.ActiveExecution?.SearchKind ?? SearchExecutionKind.PlannedRoom;
        set
        {
            if (this.ActiveExecution != null)
                this.ActiveExecution.SearchKind = value;
            else if (value != SearchExecutionKind.PlannedRoom)
                throw new InvalidOperationException("Cannot start a non-default search execution without an active objective execution.");
        }
    }
    private ChestInteractionAttempt? ActiveChestAttempt => this.ActiveExecution?.ChestAttempt;

    private const float TrapStandDurationSeconds = 3.0f;
    private const float IntelSettleDurationSeconds = 1.0f;
    private const float SilverWaitTimeoutSeconds = 30.0f;
    private const float PostRoomPomanderRetrySeconds = 3.0f;
    private const float BandedRevealExpectationSeconds = 3.0f;
    internal const uint StrengthStatusId = 687;
    internal const uint SteelStatusId = 1100;
    internal const uint DeepDungeonCurseStatusId = 1087;

    private unsafe void UpdateSearchMechanics(InstanceContentDeepDungeon* dd)
    {
        var player = Service.LocalPlayer;
        if (player == null) return;
        if (SearchState.ObjectiveOutcomeRejected)
            return;

        if (ShouldRunGeneralTick())
        {
            if (RunGeneralSearchTick(dd, player))
                return;
        }

        if (ShouldPauseForObjectiveRetry())
            return;

        if (TryApplyPassageWorkPolicy(dd, player))
            return;

        if (TryCompleteSearchExecution())
            return;

        RegeneratePlanForEvidenceIfIdle(dd, player);
        if (!RequireMovementPermission(
                "search task or room navigation",
                primaryOwnsOperation: IsSearchObjective(CurrentObjectiveDecision.PrimaryObjective) ||
                                          ShouldContinuePlannedRouteForPassageActivation(CurrentObjectiveDecision.PrimaryObjective)))
        {
            return;
        }

        if (_taskRunner!.Phase != TaskPhase.Idle)
        {
            UpdateActiveTask(dd, player);
            return;
        }

        if (_executor!.RoomContext != null)
        {
            ContinueRoomSearch(dd, player);
            return;
        }

        var targetRoom = _executor.CurrentTargetRoomIndex;
        if (targetRoom.HasValue)
        {
            int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
            if (playerRoom == targetRoom.Value)
            {
                BeginPlannedRoomSearch(dd, player);
            }
            else
            {
                NavigateToRoom(dd, targetRoom.Value, player);
            }
        }

    }

    private unsafe bool TryApplyPassageWorkPolicy(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        if (_ctx?.RunOptions.Current.HarvestChestsRequired == true ||
            _ctx?.Duty.PassageOpen != true || _executor == null)
        {
            return false;
        }

        if (Service.Condition[ConditionFlag.InCombat])
            return false;

        bool pendingBandedWork =
            _searchExecutionKind == SearchExecutionKind.BandedReentry ||
            _activeWaypoint?.Type == RoomObjectiveType.ChestBanded ||
            _executor.HasPendingBandedWaypoint;
        var originalRoute = _executor.SnapshotPlannedRoute();
        var decision = PassageWorkPlanner.Decide(new PassageWorkSnapshot
        {
            PassageOpen = true,
            HoardWorkResolved = _executor.IsHoardWorkResolved,
            VisibleBandedWork = pendingBandedWork,
            PlannedRoute = originalRoute
        });
        bool currentRemoved = _executor.ApplyRetainedRoute(decision.PlannedRoute);

        if (currentRemoved)
        {
            var abortedEntry = originalRoute.Count > 0 ? originalRoute[0] : default;
            bool activeSearchRemoved = this.ActiveExecution?.ObjectiveRecords.Count > 0 &&
                                       _executor.RoomContext?.RoomIndex == abortedEntry.RoomIndex;
            if (activeSearchRemoved)
            {
                bool authoritativeHoardResolved = _executor.HasAuthoritativeHoardResolution;
                var abortedOutcome = new RoomObjectiveOutcomeResult(
                    abortedEntry.ShouldProbeHoard
                        ? authoritativeHoardResolved ? ObjectiveOutcomeKind.Succeeded : ObjectiveOutcomeKind.Preempted
                        : ObjectiveOutcomeKind.NotRequested,
                    abortedEntry.ShouldSearchChests ? ObjectiveOutcomeKind.Preempted : ObjectiveOutcomeKind.NotRequested,
                    abortedEntry.ShouldVisitForIntel ? ObjectiveOutcomeKind.Preempted : ObjectiveOutcomeKind.NotRequested);
                if (!TryApplyActiveObjectiveOutcomes(abortedEntry.RoomIndex, abortedOutcome, "PassageOpenHoardResolved"))
                    return true;
                RecordReplayEvent("passage-open-active-search-aborted", new
                {
                    floor = dd->Floor,
                    roomIndex = abortedEntry.RoomIndex,
                    executionKind = _searchExecutionKind.ToString(),
                    outcome = ObjectiveOutcomeKind.Preempted.ToString(),
                    hoard = abortedOutcome.Hoard.ToString(),
                    chests = abortedOutcome.Chests.ToString(),
                    intel = abortedOutcome.Intel.ToString(),
                    taskPhase = _taskRunner?.Phase.ToString() ?? TaskPhase.Idle.ToString(),
                    waypointType = (_activeWaypoint ?? _executor.CurrentWaypoint)?.Type.ToString(),
                    reason = "passage-open-hoard-resolved"
                });
            }
            CancelActiveMovement();
            _executor.ClearRoomContext();
            _activeWaypoint = null;
            _searchExecutionKind = SearchExecutionKind.PlannedRoom;
            _ctx.ClearPreferredAggroTarget();
        }

        if (originalRoute.Count != decision.PlannedRoute.Count)
        {
            RecordReplayEvent("passage-open-search-pruned", new
            {
                floor = dd->Floor,
                hoardEvidenceState = _executor.HoardEvidenceState.ToString(),
                hoardWorkResolved = _executor.IsHoardWorkResolved,
                currentRemoved,
                before = originalRoute.Select(entry => entry.RoomIndex).ToArray(),
                after = decision.PlannedRoute.Select(entry => entry.RoomIndex).ToArray(),
                reason = "passage-open-pruned-chest-only-work"
            });
        }

        if (!decision.ShouldExit)
        {
            if (_executor.IsComplete && !decision.RetainVisibleBandedWork)
            {
                _status = $"Passage open, waiting for hoard work ({_executor.HoardEvidenceState})";
                RecordHoardEvidenceWait("passage-open-waiting-hoard-work");
                return true;
            }
            return false;
        }

        CancelActiveMovement();
        ClearPostRoomPomanderRetry();
        _executor.ClearRoomContext();
        _activeWaypoint = null;
        _searchExecutionKind = SearchExecutionKind.PlannedRoom;
        _chaseHelper.Reset();
        _ctx.ClearPreferredAggroTarget();
        _status = "Passage open, hoard work resolved";
        RecordReplayEvent("passage-open-exit-after-hoard-resolved", new
        {
            floor = dd->Floor,
            hoardEvidenceState = _executor.HoardEvidenceState.ToString(),
            reason = "passage-open-hoard-resolved-no-banded-work"
        });
        RecordReplayEvent("floor-active-mechanic-completed", new
        {
            mechanic = "Search",
            reason = "passage-open-hoard-resolved",
            nextObjective = FloorObjectiveKind.EnterPassage.ToString()
        });
        return true;
    }

    private unsafe bool RunGeneralSearchTick(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        if (_executor?.HasPlanningSnapshot == false)
        {
            var normalGraph = this.NormalGraph;
            if (normalGraph == null)
            {
                _status = "Waiting for room graph...";
                return true;
            }

            _executor.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
            if (!_executor.HasPlanningSnapshot)
            {
                _status = "Waiting for floor planning evidence...";
                return true;
            }
        }

        SyncLiveRunOptions(dd, player);
        ObserveHoardCount(dd, "general-search-tick");
        if (!RefreshCachedHoardIndicator(dd))
        {
            _status = "Waiting for hoard indicator evidence...";
            return true;
        }
        ResolveCurrentFloorIntuitionTimeoutIfNeeded(dd);
        TryUseGeneralAutoPomander(dd);
        RegeneratePlanForEvidenceIfIdle(dd, player);

        return UpdatePostRoomPomanderRetry(dd, player);
    }

    private unsafe void BeginPlannedRoomSearch(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        int? targetRoom = _executor!.CurrentTargetRoomIndex;
        if (!targetRoom.HasValue)
            return;
        if (DateTime.UtcNow < SearchState.RoomEvidenceRetryAt)
        {
            _status = $"Waiting for room {targetRoom.Value} evidence...";
            return;
        }

        _searchExecutionKind = SearchExecutionKind.PlannedRoom;
        long requestId = _nextRoomSearchRequestId();
        var objectEvidence = this.ObjectEvidence.Current!;
        int observedRoomIndex = this.LastObservedRoomIndex;
        int currentPlayerRoomIndex = RoomGraph.GetLocalPlayerRoomIndex(dd);
        bool started = _executor.StartCurrentPlanRoomSearch(
            dd,
            objectEvidence,
            player.Position,
            out bool evidenceUnavailable,
            out RoomSearchChestDiagnostic? chestDiagnostic);
        if (chestDiagnostic != null)
        {
            RecordReplayEvent("room-search-chest-diagnostic", new
            {
                requestId,
                snapshotBuilt = started,
                objectEvidence = new
                {
                    objectEvidence.RefreshSequence,
                    objectEvidence.CapturedAtUtc,
                    playerPosition = objectEvidence.PlayerPosition.HasValue
                        ? new
                        {
                            x = objectEvidence.PlayerPosition.Value.X,
                            y = objectEvidence.PlayerPosition.Value.Y,
                            z = objectEvidence.PlayerPosition.Value.Z
                        }
                        : null,
                    observedRoomIndex
                },
                currentPlayerRoomIndex,
                chestDiagnostic
            });
        }
        if (evidenceUnavailable)
        {
            SearchState.RoomEvidenceRetryAt = DateTime.UtcNow.Add(GeneralTickInterval);
            _status = $"Waiting for room {targetRoom.Value} evidence...";
            return;
        }
        SearchState.RoomEvidenceRetryAt = DateTime.MinValue;
        if (!BeginRoomObjectiveExecutions(targetRoom.Value, _executor.CurrentPlanEntry))
        {
            PreemptActiveObjectiveExecutions("ObjectiveStartRejected");
            CancelActiveMovement();
            _executor.ClearRoomContext();
            return;
        }
        if (!started)
        {
            RecordReplayEvent("room-search-start-failed", new
            {
                roomIndex = targetRoom.Value,
                executionKind = _searchExecutionKind.ToString()
            });
            FinalizeRoomSearch(
                dd,
                player,
                targetRoom.Value,
                useRoomFinishPomander: true,
                allowBandedRevealExpectation: true,
                explicitOutcome: BuildTerminalOutcomeSnapshot(_executor.CurrentPlanEntry, ObjectiveOutcomeKind.Failed),
                finalizeReason: "RoomSearchBuildFailed");
            return;
        }

        RecordReplayEvent("room-search-started", new
        {
            roomIndex = targetRoom.Value,
            executionKind = _searchExecutionKind.ToString(),
            remainingWaypointCount = _executor.RoomContext?.RemainingWaypointCount ?? 0,
            blindFallbackUnavailable =
                _executor.RoomContext?.BlindFallbackUnavailable ?? false,
            fallbackCandidates = _executor.RoomContext == null
                ? null
                : new
                {
                    catalog = _executor.RoomContext.FallbackCatalogCandidateCount,
                    palacePal = _executor.RoomContext.FallbackPalacePalCandidateCount,
                    union = _executor.RoomContext.FallbackUnionCandidateCount
                },
            planEntry = _executor.CurrentPlanEntry.HasValue
                ? new
                {
                    _executor.CurrentPlanEntry.Value.RoomIndex,
                    _executor.CurrentPlanEntry.Value.ShouldProbeHoard,
                    _executor.CurrentPlanEntry.Value.ShouldSearchChests,
                    _executor.CurrentPlanEntry.Value.ShouldVisitForIntel,
                    hoardEvidenceState = _executor.CurrentPlanEntry.Value.HoardEvidenceState.ToString()
                }
                : null,
            waypoints = _executor.RoomContext?.Waypoints.Select(waypoint => new
            {
                type = waypoint.Type.ToString(),
                arrivalRadius = waypoint.ArrivalRadius,
                x = waypoint.Position.X,
                y = waypoint.Position.Y,
                z = waypoint.Position.Z
            }).ToArray() ?? []
        });

        if (!_executor.RoomContext!.HasWaypoints)
        {
            if (_executor.CurrentPlanEntry?.ShouldVisitForIntel == true)
            {
                BeginRoomIntelSettle(_executor.RoomContext.RoomIndex);
                return;
            }

            FinalizeRoomSearch(dd, player, _executor.RoomContext.RoomIndex, useRoomFinishPomander: true, allowBandedRevealExpectation: true);
        }
    }

    private bool BeginRoomObjectiveExecutions(int roomIndex, RoomPlanEntry? entry)
    {
        if (this.IsDisposed)
        {
            _status = "Cannot start room objectives without an active floor runtime";
            Service.Log.Error($"[FloorPhase] {_status}");
            return false;
        }
        var activeExecution = this.ActiveExecution;
        if (activeExecution == null)
        {
            _status = "Cannot start room objectives without an active objective execution";
            Service.Log.Error($"[FloorPhase] {_status}");
            return false;
        }
        if (activeExecution.ObjectiveRecords.Count != 0)
        {
            _status = "Cannot start room objectives while another objective attempt is active";
            Service.Log.Error($"[FloorPhase] {_status}");
            return false;
        }

        if (_searchExecutionKind == SearchExecutionKind.BandedReentry)
        {
            return TryBeginObjectiveExecution(
                activeExecution,
                roomIndex,
                RoomObjectiveCategory.Hoard,
                FloorObjectiveKind.OpenVisibleBandedChest,
                required: true);
        }

        if (entry?.ShouldProbeHoard == true &&
            !TryBeginObjectiveExecution(
                activeExecution,
                roomIndex,
                RoomObjectiveCategory.Hoard,
                entry.Value.HoardEvidenceState == HoardEvidenceState.IntuitionDirect
                    ? FloorObjectiveKind.CompleteKnownHoard
                    : FloorObjectiveKind.DiscoverHoard,
                required: true))
        {
            return false;
        }
        if (entry?.ShouldSearchChests == true &&
            !TryBeginObjectiveExecution(
                activeExecution,
                roomIndex,
                RoomObjectiveCategory.Chests,
                FloorObjectiveKind.OpenPlannedChest,
                required: false))
        {
            return false;
        }
        if (entry?.ShouldVisitForIntel == true &&
            !TryBeginObjectiveExecution(
                activeExecution,
                roomIndex,
                RoomObjectiveCategory.Intel,
                FloorObjectiveKind.DiscoverHoard,
                required: true))
        {
            return false;
        }

        return true;
    }

    private bool TryBeginObjectiveExecution(
        ObjectiveExecution activeExecution,
        int roomIndex,
        RoomObjectiveCategory category,
        FloorObjectiveKind kind,
        bool required)
    {
        var objective = this.Objectives.GetOrCreateObjective(new RoomObjectiveKey(roomIndex, category, kind), kind, required);
        if (objective.Outcome != ObjectiveOutcomeKind.Pending)
        {
            if (objective.Outcome != ObjectiveOutcomeKind.Preempted &&
                (!required || !RoomObjectiveOutcomePlanner.IsRetryableFailure(objective.Outcome)))
            {
                RecordObjectiveExecutionRejected(objective.Identity, kind, category, "Start", ObjectiveRestartStatus.NotRestartable.ToString());
                return false;
            }

            var restart = this.Objectives.ObjectiveLedger.Restart(objective.Identity);
            if (restart.Status != ObjectiveRestartStatus.Restarted)
            {
                RecordObjectiveExecutionRejected(objective.Identity, kind, category, "Restart", restart.Status.ToString());
                return false;
            }
            objective = restart.Objective;
        }

        activeExecution.ObjectiveRecords.Add(new ActiveObjectiveExecution(objective.Identity, kind, required, category));
        this.RunTelemetry?.ObserveObjectiveStart(
            kind,
            category == RoomObjectiveCategory.Intel);
        RecordReplayEvent("objective-execution-started", new
        {
            floorGeneration = objective.Identity.FloorGeneration,
            objectiveId = objective.Identity.ObjectiveId,
            attempt = objective.Identity.Attempt,
            objectiveKind = kind.ToString(),
            category = category.ToString(),
            required,
            roomIndex,
            executionKind = _searchExecutionKind.ToString()
        });
        return true;
    }

    private bool TryApplyActiveObjectiveOutcomes(
        int roomIndex,
        in RoomObjectiveOutcomeResult outcome,
        string reason,
        FloorObjectiveKind? replacementObjective = null)
    {
        var activeExecution = this.ActiveExecution;
        if (this.IsDisposed || activeExecution == null)
        {
            _status = activeExecution == null
                ? "Rejected objective outcome without an active objective execution"
                : "Rejected objective outcome without its active floor runtime";
            RecordReplayEvent("objective-outcome-rejected-no-active-execution", new
            {
                floorGeneration = this.Generation,
                roomIndex,
                reason,
                status = _status
            });
            Service.Log.Error($"[FloorPhase] {_status}");
            return false;
        }
        var objectiveRecords = activeExecution.ObjectiveRecords;
        var searchState = this.SearchState;

        foreach (var execution in objectiveRecords)
        {
            var terminalOutcome = GetCategoryOutcome(execution.Category, outcome);
            var status = this.Objectives.ObjectiveLedger.ValidateOutcome(execution.Identity, terminalOutcome, out _);
            if (status == ObjectiveOutcomeApplyStatus.Accepted)
                continue;

            RecordObjectiveExecutionRejected(execution.Identity, execution.Kind, execution.Category, "Outcome", status.ToString());
            _status = $"Rejected objective outcome {execution.Identity.ObjectiveId}/{execution.Identity.Attempt}: {status}";
            searchState.ObjectiveOutcomeRejected = true;
            Service.Log.Error($"[FloorPhase] {_status}");
            return false;
        }

        foreach (var execution in objectiveRecords)
        {
            var terminalOutcome = GetCategoryOutcome(execution.Category, outcome);
            var result = this.Objectives.ObjectiveLedger.ApplyOutcome(execution.Identity, terminalOutcome);
            RecordReplayEvent(
                terminalOutcome == ObjectiveOutcomeKind.Preempted
                    ? "objective-execution-preempted"
                    : "objective-execution-outcome",
                new
                {
                    floorGeneration = execution.Identity.FloorGeneration,
                    objectiveId = execution.Identity.ObjectiveId,
                    attempt = execution.Identity.Attempt,
                    objectiveKind = execution.Kind.ToString(),
                    category = execution.Category.ToString(),
                    execution.Required,
                    roomIndex,
                    executionKind = _searchExecutionKind.ToString(),
                    outcome = terminalOutcome.ToString(),
                    applyStatus = result.Status.ToString(),
                    failureCount = result.FailureCount,
                    reason,
                    replacementObjective = replacementObjective?.ToString()
                });
        }

        objectiveRecords.Clear();
        return true;
    }

    internal bool PreemptActiveObjectiveExecutions(
        string reason,
        FloorObjectiveKind? replacementObjective = null)
    {
        if (this.ActiveExecution?.ObjectiveRecords.Count is null or 0)
            return true;

        var outcome = new RoomObjectiveOutcomeResult(
            ObjectiveOutcomeKind.Preempted,
            ObjectiveOutcomeKind.Preempted,
            ObjectiveOutcomeKind.Preempted);
        return TryApplyActiveObjectiveOutcomes(
            _executor?.RoomContext?.RoomIndex ?? _executor?.CurrentTargetRoomIndex ?? -1,
            outcome,
            reason,
            replacementObjective);
    }

    private bool StopActiveSearchExecution(FloorObjectiveKind replacementObjective)
    {
        var previousObjective = this.ActiveExecution?.Objective ?? FloorObjectiveKind.None;
        int roomIndex = _executor?.RoomContext?.RoomIndex ?? _executor?.CurrentTargetRoomIndex ?? -1;
        var executionKind = _searchExecutionKind;
        string reason = $"ObjectiveChanged:{replacementObjective}";
        if (!PreemptActiveObjectiveExecutions(reason, replacementObjective))
            return false;

        RecordReplayEvent("room-search-aborted", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            roomIndex,
            executionKind = executionKind.ToString(),
            previousObjective = previousObjective.ToString(),
            replacementObjective = replacementObjective.ToString(),
            outcome = ObjectiveOutcomeKind.Preempted.ToString()
        });

        CancelActiveMovement();
        _executor?.ClearRoomContext();
        ClearRoomIntelSettle();
        _searchExecutionKind = SearchExecutionKind.PlannedRoom;
        return true;
    }

    private void RecordObjectiveExecutionRejected(
        ObjectiveIdentity identity,
        FloorObjectiveKind kind,
        RoomObjectiveCategory category,
        string operation,
        string rejectionStatus)
    {
        RecordReplayEvent("objective-execution-rejected", new
        {
            floorGeneration = identity.FloorGeneration,
            objectiveId = identity.ObjectiveId,
            attempt = identity.Attempt,
            objectiveKind = kind.ToString(),
            category = category.ToString(),
            operation,
            rejectionStatus
        });
    }

    private static ObjectiveOutcomeKind GetCategoryOutcome(
        RoomObjectiveCategory category,
        in RoomObjectiveOutcomeResult outcome)
    {
        return category switch
        {
            RoomObjectiveCategory.Hoard => outcome.Hoard,
            RoomObjectiveCategory.Chests => outcome.Chests,
            RoomObjectiveCategory.Intel => outcome.Intel,
            _ => ObjectiveOutcomeKind.NotRequested
        };
    }

    private unsafe void ContinueRoomSearch(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        var roomContext = _executor!.RoomContext;
        if (roomContext == null)
            return;

        int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
        if (playerRoom != roomContext.RoomIndex)
        {
            NavigateToRoom(dd, roomContext.RoomIndex, player);
            _status = $"Re-entering room {roomContext.RoomIndex}";
            return;
        }

        var waypoint = _executor.CurrentWaypoint;
        if (waypoint.HasValue)
        {
            ConfigureTaskForWaypoint(waypoint.Value);
            return;
        }

        if (UpdateRoomIntelSettle(dd, player, roomContext.RoomIndex))
            return;

        FinalizeRoomSearch(
            dd,
            player,
            roomContext.RoomIndex,
            useRoomFinishPomander: true,
            allowBandedRevealExpectation: _searchExecutionKind == SearchExecutionKind.PlannedRoom);
    }

    private unsafe void UpdateActiveTask(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        if (_executor!.RoomContext != null)
        {
            int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
            if (playerRoom != _executor.RoomContext.RoomIndex)
            {
                _taskRunner!.Reset();
                _activeWaypoint = null;
                NavigateToRoom(dd, _executor.RoomContext.RoomIndex, player);
                _status = $"Re-entering room {_executor.RoomContext.RoomIndex}";
                return;
            }
        }

        if (TryHandleGoldChestOvercap(dd, player))
            return;
        if (TryHandleSilverChestOvercap(dd, player))
            return;
        if (TryUpdateChestReapproach(dd, player))
            return;

        var taskPhaseBefore = _taskRunner!.Phase;
        double elapsedBefore = _taskRunner.ElapsedSeconds;
        SampleActiveWaypointTelemetry(taskPhaseBefore, player.Position);
        var result = _taskRunner!.Update(player.Position);
        var taskPhaseAfter = _taskRunner.Phase;
        UpdateTaskStatus();

        switch (result)
        {
            case TaskResult.Arrived:
                if (_activeWaypoint.HasValue && IsChestWaypoint(_activeWaypoint.Value) &&
                    !FsdChestInteraction.HasInteraction(ActiveChestAttempt) &&
                    this.ObjectEvidence.Current is { } arrivalEvidence &&
                    _ctx?.ChestInteraction.IsTargetMissing(ActiveChestAttempt, arrivalEvidence) == true)
                {
                    _taskRunner.Reset();
                    HandleTaskSkip(dd, player, "TargetLost", WaypointOutcomeKind.Deferred, elapsedBefore, taskPhaseBefore);
                }
                else
                {
                    HandleTaskArrived(dd, taskPhaseAfter);
                }
                break;
            case TaskResult.Complete:
                HandleTaskComplete(dd, player, elapsedBefore, taskPhaseBefore);
                break;
            case TaskResult.TimedOut:
                Service.Log.Warning("[FloorPhase] Task timed out, skipping");
                HandleTaskSkip(dd, player, "TimedOut", WaypointOutcomeKind.Failed, elapsedBefore, taskPhaseBefore);
                break;
            case TaskResult.NavigationFailed:
                Service.Log.Warning("[FloorPhase] Waypoint navigation failed, skipping");
                HandleTaskSkip(dd, player, "NavigationFailed", WaypointOutcomeKind.Failed, elapsedBefore, taskPhaseBefore);
                break;
        }
    }

    private unsafe bool TryUpdateChestReapproach(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        var attempt = ActiveChestAttempt;
        if (attempt == null ||
            _activeWaypoint is not { } waypoint ||
            !IsChestWaypoint(waypoint) ||
            _taskRunner?.Phase != TaskPhase.WaitingPost ||
            _ctx?.ChestInteraction == null)
        {
            return false;
        }

        if (_taskRunner.ElapsedSeconds >= _ctx.ChestInteraction.GetOpenTimeoutSeconds(waypoint))
        {
            if (attempt.Reapproaching)
                FinishChestReapproach(attempt, waypoint, false, "interaction-window-expired");
            return false;
        }

        if (!_ctx.ChestInteraction.TryBeginOrContinueReapproach(
                attempt,
                player.Position,
                out bool started))
        {
            return false;
        }

        if (started)
        {
            this.RunTelemetry?.ObserveChestRecoveryStarted();
            RecordReplayEvent("chest-recovery-started", new
            {
                floor = dd->Floor,
                roomIndex = _executor?.RoomContext?.RoomIndex ?? -1,
                waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1,
                chestType = waypoint.Type.ToString(),
                entityId = attempt.EntityId,
                reason = "outside-interaction-range"
            });
        }

        var state = _navHelper!.Navigate(waypoint.Position, player.Position, arrivalRadius: 1.5f);
        switch (state)
        {
            case NavigationState.Moving:
                _status = $"Returning to {DescribeChest(waypoint.Type)} interaction range";
                return true;
            case NavigationState.StuckRepathing:
                _status = $"Reapproaching {DescribeChest(waypoint.Type)} ({_navHelper.StuckRetryCount}/3)";
                return true;
            case NavigationState.Arrived:
                FinishChestReapproach(attempt, waypoint, true, "returned-to-chest");
                return false;
            case NavigationState.StuckGiveUp:
                _status = $"Retrying route to {DescribeChest(waypoint.Type)}";
                return true;
            case NavigationState.Failed:
                _status = $"Waiting to retry route to {DescribeChest(waypoint.Type)}";
                return true;
            default:
                return true;
        }
    }

    private void FinishChestReapproach(
        ChestInteractionAttempt attempt,
        RoomWaypoint waypoint,
        bool succeeded,
        string reason)
    {
        _ctx?.ChestInteraction.FinishReapproach(attempt);
        _navHelper?.Cancel();
        RecordReplayEvent(succeeded ? "chest-recovery-completed" : "chest-recovery-failed", new
        {
            roomIndex = _executor?.RoomContext?.RoomIndex ?? -1,
            waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1,
            chestType = waypoint.Type.ToString(),
            entityId = attempt.EntityId,
            reason
        });
    }

    private unsafe bool TryHandleGoldChestOvercap(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        var chestAttempt = ActiveChestAttempt;
        if (chestAttempt == null ||
            _activeWaypoint == null ||
            _activeWaypoint.Value.Type != RoomObjectiveType.ChestGold ||
            chestAttempt.EntityId == 0 ||
            !chestAttempt.PendingGoldOvercapSlotIndex.HasValue)
        {
            return false;
        }
        uint slotIndex = chestAttempt.PendingGoldOvercapSlotIndex.Value;
        if (this.ItemUse.Pending is
            {
                Purpose: FloorItemUsePurpose.GoldChestOvercap,
                Key:
                {
                    Kind: FloorItemUseKind.Pomander,
                    ItemId: var pendingSlotIndex
                }
            } &&
            pendingSlotIndex == slotIndex)
        {
            return true;
        }

        if (ShouldUseGoldChestOvercapPomander(slotIndex))
        {
            if (CanAttemptPomanderUse() &&
                TryUsePomander(
                    slotIndex,
                    dd,
                    $"gold overcap relief ({DescribePomanderSlot(slotIndex)})",
                    FloorItemUsePurpose.GoldChestOvercap))
            {
                return true;
            }

            return false;
        }

        Service.Log.Info($"[FloorPhase] Gold chest overcap on {DescribePomanderSlot(slotIndex)}, skipping chest");
        chestAttempt.PendingGoldOvercapSlotIndex = null;
        var taskPhaseBefore = _taskRunner!.Phase;
        double elapsedBefore = _taskRunner.ElapsedSeconds;
        _taskRunner!.Reset();
        HandleTaskSkip(dd, player, "GoldChestOvercapSkip", WaypointOutcomeKind.PolicySkipped, elapsedBefore, taskPhaseBefore);
        return true;
    }

    internal void HandleGoldChestOvercapObserved(uint? slotIndex)
    {
        var attempt = ActiveChestAttempt;
        if (!slotIndex.HasValue ||
            this.IsDisposed ||
            _activeWaypoint?.Type != RoomObjectiveType.ChestGold ||
            attempt == null ||
            attempt.EntityId == 0)
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
            roomIndex = _executor?.RoomContext?.RoomIndex ?? -1,
            waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1,
            entityId = attempt.EntityId,
            slotIndex = slotIndex.Value
        });
    }

    internal unsafe void HandleSilverChestOvercapObserved(uint? demicloneRowId)
    {
        var efw = EventFramework.Instance();
        var dd = efw != null ? efw->GetInstanceContentDeepDungeon() : null;
        var attempt = ActiveChestAttempt;
        if (dd == null || dd->DeepDungeonId != 4 ||
            IsDisposed || this.Floor != dd->Floor ||
            _activeWaypoint?.Type != RoomObjectiveType.ChestSilver ||
            attempt == null || attempt.EntityId == 0 ||
            _taskRunner?.Phase != TaskPhase.WaitingPost ||
            !demicloneRowId.HasValue)
            return;
        attempt.PendingSilverOvercapDemicloneRowId = demicloneRowId;
        RecordReplayEvent("silver-chest-overcap-correlated", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            entityId = attempt.EntityId,
            demicloneRowId
        });
    }

    private unsafe bool TryHandleSilverChestOvercap(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        var attempt = ActiveChestAttempt;
        if (attempt?.PendingSilverOvercapDemicloneRowId == null ||
            _activeWaypoint?.Type != RoomObjectiveType.ChestSilver)
            return false;
        if (this.ItemUse.Pending != null)
            return true;
        // All three incense types share this inventory. Use Serenity only to make space.
        int count = _pomanderManager.GetStoneCount(1) + _pomanderManager.GetStoneCount(2) + _pomanderManager.GetStoneCount(3);
        var decision = SilverChestOvercapPolicy.Decide(count,
            GetStoneCountAvailableForFloorUse(3) > 0,
            DeepDungeonFloorItemUsePolicy.CanUsePtIncense(dd->DeepDungeonBanId));
        if (decision == SilverChestOvercapDecision.RetryChest)
        {
            attempt.PendingSilverOvercapDemicloneRowId = null;
            attempt.NextInteractAt = DateTime.MinValue;
            return false;
        }
        if (decision == SilverChestOvercapDecision.UseSerenityIncense &&
            _taskRunner!.ElapsedSeconds < _ctx!.ChestInteraction.GetOpenTimeoutSeconds(_activeWaypoint!.Value))
        {
            if (!CanAttemptPomanderUse() ||
                TryDispatchFloorStone(3, dd, "silver overcap relief (serenity incense)", FloorItemUsePurpose.SilverChestOvercap))
                return true;
        }
        Service.Log.Info("[FloorPhase] Silver incense overcap cannot be relieved, skipping chest");
        attempt.PendingSilverOvercapDemicloneRowId = null;
        var phase = _taskRunner!.Phase;
        double elapsed = _taskRunner.ElapsedSeconds;
        _taskRunner.Reset();
        HandleTaskSkip(dd, player, "SilverChestOvercapSkip", WaypointOutcomeKind.PolicySkipped, elapsed, phase);
        return true;
    }

    private void ConfigureTaskForWaypoint(RoomWaypoint waypoint)
    {
        _activeWaypoint = waypoint;
        if (IsChestWaypoint(waypoint))
            this.ActiveExecution!.ChestAttempt = new ChestInteractionAttempt(waypoint);
        RecordWaypointStarted(waypoint);
        StartActiveWaypointTelemetry(waypoint);

        switch (waypoint.Type)
        {
            case RoomObjectiveType.Trap:
                float trapArrivalRadius = waypoint.HasExplicitArrivalRadius
                    ? waypoint.ArrivalRadius
                    : 1.4f;
                _taskRunner!.Configure(
                    waypoint.Position, trapArrivalRadius,
                    preCondition: null, preTimeoutSeconds: 0,
                    postCondition: elapsed => elapsed >= TrapStandDurationSeconds,
                    postTimeoutSeconds: TrapStandDurationSeconds + 1);
                break;

            case RoomObjectiveType.ChestBanded:
                _chatWatchers?.ExpectHoardCofferFound(this.Floor);
                _taskRunner!.Configure(
                    waypoint.Position, 1.5f,
                    preCondition: null, preTimeoutSeconds: 0,
                    postCondition: _ => IsChestAccepted(waypoint),
                    postTimeoutSeconds: _ctx!.ChestInteraction.GetOpenTimeoutSeconds(waypoint));
                break;

            case RoomObjectiveType.ChestSilver:
                _taskRunner!.Configure(
                    waypoint.Position, 1.5f,
                    preCondition: () => _ctx?.ChestInteraction.CanStart(ActiveChestAttempt, waypoint) == true,
                    preTimeoutSeconds: SilverWaitTimeoutSeconds,
                    postCondition: _ => IsChestAccepted(waypoint),
                    postTimeoutSeconds: _ctx!.ChestInteraction.GetOpenTimeoutSeconds(waypoint));
                break;

            default:
                _taskRunner!.Configure(
                    waypoint.Position, 1.5f,
                    preCondition: null, preTimeoutSeconds: 0,
                    postCondition: _ => IsChestAccepted(waypoint),
                    postTimeoutSeconds: _ctx!.ChestInteraction.GetOpenTimeoutSeconds(waypoint));
                break;
        }
    }

    private unsafe void HandleTaskArrived(InstanceContentDeepDungeon* dd, TaskPhase taskPhaseAfter)
    {
        if (_activeWaypoint == null)
            return;

        var waypoint = _activeWaypoint.Value;
        int roomIndex = _executor?.RoomContext?.RoomIndex ?? -1;
        int waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1;
        RecordReplayEvent("waypoint-arrived", new
        {
            roomIndex,
            waypointIndex,
            executionKind = _searchExecutionKind.ToString(),
            objectiveType = waypoint.Type.ToString(),
            nextTaskPhase = taskPhaseAfter.ToString(),
            x = waypoint.Position.X,
            y = waypoint.Position.Y,
            z = waypoint.Position.Z
        });

        if (waypoint.Type == RoomObjectiveType.Trap)
        {
            SearchState.LastTrapTriggered = new TrapObservation(
                DateTime.UtcNow,
                roomIndex,
                waypointIndex,
                _searchExecutionKind,
                waypoint.Position);
            RecordReplayEvent("trap-triggered", new
            {
                roomIndex,
                waypointIndex,
                executionKind = _searchExecutionKind.ToString(),
                x = waypoint.Position.X,
                y = waypoint.Position.Y,
                z = waypoint.Position.Z
            });
        }
        else
        {
            this.ObjectEvidence.Invalidate();
        }
    }

    private unsafe void HandleTaskComplete(InstanceContentDeepDungeon* dd, IPlayerCharacter player, double elapsedSeconds, TaskPhase taskPhaseBefore)
    {
        if (_activeWaypoint == null)
            return;

        var completedWaypoint = _activeWaypoint.Value;
        if (completedWaypoint.Type == RoomObjectiveType.ChestBanded)
            _chatWatchers?.CancelExpectedHoardCofferFound();

        RecordWaypointResolved(completedWaypoint, WaypointOutcomeKind.Completed, "Completed", elapsedSeconds, taskPhaseBefore);
        EndActiveWaypointTelemetry(RunWaypointTerminalOutcome.Completed, "Completed", taskPhaseBefore);
        _executor!.RoomContext?.RecordWaypointOutcome(completedWaypoint, WaypointOutcomeKind.Completed, "Completed");
        _executor!.AdvanceWaypoint();
        _activeWaypoint = null;

        FinishFinalWaypoint(dd, player);
    }

    private unsafe void HandleTaskSkip(
        InstanceContentDeepDungeon* dd,
        IPlayerCharacter player,
        string reason,
        WaypointOutcomeKind outcome,
        double elapsedSeconds,
        TaskPhase taskPhaseBefore)
    {
        if (_activeWaypoint != null)
        {
            if (_activeWaypoint.Value.Type == RoomObjectiveType.ChestBanded)
                _chatWatchers?.CancelExpectedHoardCofferFound();
            RecordWaypointResolved(_activeWaypoint.Value, outcome, reason, elapsedSeconds, taskPhaseBefore);
            EndActiveWaypointTelemetry(
                reason == "NavigationFailed"
                    ? RunWaypointTerminalOutcome.NavigationFailed
                    : RunWaypointTerminalOutcome.Skipped,
                reason,
                taskPhaseBefore);
            _executor!.RoomContext?.RecordWaypointOutcome(_activeWaypoint.Value, outcome, reason);
        }

        _executor!.AdvanceWaypoint();
        _activeWaypoint = null;

        FinishFinalWaypoint(dd, player);
    }

    private unsafe void FinishFinalWaypoint(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        if (_executor!.CurrentWaypoint != null || _executor.RoomContext == null)
            return;

        if (_searchExecutionKind == SearchExecutionKind.PlannedRoom &&
            _executor.CurrentPlanEntry?.ShouldVisitForIntel == true)
        {
            BeginRoomIntelSettle(_executor.RoomContext.RoomIndex);
            return;
        }

        FinalizeRoomSearch(
            dd,
            player,
            _executor.RoomContext.RoomIndex,
            useRoomFinishPomander: true,
            allowBandedRevealExpectation: _searchExecutionKind == SearchExecutionKind.PlannedRoom);
    }

    private unsafe void FinalizeRoomSearch(
        InstanceContentDeepDungeon* dd,
        IPlayerCharacter player,
        int roomIndex,
        bool useRoomFinishPomander,
        bool allowBandedRevealExpectation,
        RoomObjectiveOutcomeSnapshot? explicitOutcome = null,
        string finalizeReason = "RoomExecutionFinished")
    {
        if (!RefreshCachedHoardIndicator(dd))
        {
            _status = "Waiting for hoard indicator evidence...";
            return;
        }

        var completedEntry = _executor!.CurrentPlanEntry;
        if (_searchExecutionKind == SearchExecutionKind.BandedReentry)
        {
            completedEntry = new RoomPlanEntry(
                roomIndex,
                shouldProbeHoard: true,
                shouldSearchChests: false,
                shouldVisitForIntel: false,
                HoardEvidenceState.IntuitionDirect);
        }

        bool completedRoomWasHoardSearch =
            completedEntry.HasValue &&
            completedEntry.Value.RoomIndex == roomIndex &&
            completedEntry.Value.ShouldSearchHoard;

        bool authoritativeHoardResolved = _executor.HasOpenedHoardThisFloor || dd->HoardCount > PlanningState.LastKnownHoardCount;
        var roomContext = _executor.RoomContext;
        var outcomeSnapshot = explicitOutcome ?? roomContext?.BuildOutcomeSnapshot(authoritativeHoardResolved)
            ?? BuildTerminalOutcomeSnapshot(completedEntry, ObjectiveOutcomeKind.Deferred);
        var objectiveOutcome = RoomObjectiveOutcomePlanner.Decide(outcomeSnapshot);
        objectiveOutcome = RoomObjectiveOutcomePlanner.RequireAuthoritativeDirectHoard(
            objectiveOutcome,
            completedEntry?.HoardEvidenceState == HoardEvidenceState.IntuitionDirect,
            authoritativeHoardResolved);
        objectiveOutcome = ApplyRoomObjectiveRetryPolicy(roomIndex, completedEntry, objectiveOutcome);
        string outcomeReason = finalizeReason == "RoomExecutionFinished" && !string.IsNullOrEmpty(roomContext?.LastOutcomeReason)
            ? roomContext.LastOutcomeReason
            : finalizeReason;
        if (!TryApplyActiveObjectiveOutcomes(roomIndex, objectiveOutcome, outcomeReason))
            return;

        _taskRunner!.Reset();
        _activeWaypoint = null;
        ClearRoomIntelSettle();
        _executor.ClearRoomContext();
        ObserveHoardCount(dd, "room-search-finalized");
        RecordReplayEvent("room-objective-outcome", new
        {
            roomIndex,
            executionKind = _searchExecutionKind.ToString(),
            reason = outcomeReason,
            hoard = objectiveOutcome.Hoard.ToString(),
            chests = objectiveOutcome.Chests.ToString(),
            intel = objectiveOutcome.Intel.ToString(),
            markHoardSearched = objectiveOutcome.MarkHoardSearched,
            markChestsSearched = objectiveOutcome.MarkChestsSearched,
            markIntelVisited = objectiveOutcome.MarkIntelVisited
        });

        var pomanderOutcome = RoomFinishPomanderOutcome.NotNeeded;
        if (useRoomFinishPomander)
        {
            pomanderOutcome = ProcessRoomFinishPomander(dd);
        }

        var normalGraph = this.NormalGraph;
        if (normalGraph == null)
        {
            _status = "Waiting for room graph...";
            return;
        }

        _executor.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
        RecordReplayEvent("room-search-finished", new
        {
            roomIndex,
            executionKind = _searchExecutionKind.ToString(),
            useRoomFinishPomander,
            allowBandedRevealExpectation,
            pomanderOutcome = pomanderOutcome.ToString(),
            remainingPlanCount = _executor.PlannedRouteCount,
            completedEntry = completedEntry.HasValue
                ? new
                {
                    completedEntry.Value.RoomIndex,
                    completedEntry.Value.ShouldProbeHoard,
                    completedEntry.Value.ShouldSearchChests,
                    completedEntry.Value.ShouldVisitForIntel,
                    hoardEvidenceState = completedEntry.Value.HoardEvidenceState.ToString()
                }
                : null
        });
        _searchExecutionKind = SearchExecutionKind.PlannedRoom;

        if (allowBandedRevealExpectation &&
            completedRoomWasHoardSearch &&
            objectiveOutcome.Hoard == ObjectiveOutcomeKind.Succeeded &&
            _executor.ConfigSnapshot.BandedEnabled &&
            !_executor.HasOpenedHoardThisFloor)
        {
            StartBandedRevealExpectation(roomIndex);
        }

        if (pomanderOutcome == RoomFinishPomanderOutcome.PendingRetry)
            StartPostRoomPomanderRetry(roomIndex, _executor.IsComplete);
        else
            ClearPostRoomPomanderRetry();

        TryCompleteSearchExecution();
    }

    private static RoomObjectiveOutcomeSnapshot BuildTerminalOutcomeSnapshot(RoomPlanEntry? entry, ObjectiveOutcomeKind outcome)
    {
        bool shouldProbeHoard = entry?.ShouldProbeHoard == true;
        bool shouldSearchChests = entry?.ShouldSearchChests == true;
        bool shouldVisitForIntel = entry?.ShouldVisitForIntel == true;
        return new RoomObjectiveOutcomeSnapshot(
            BuildTerminalCategory(shouldProbeHoard, outcome),
            BuildTerminalCategory(shouldSearchChests, outcome),
            BuildTerminalCategory(shouldVisitForIntel, outcome));
    }

    private static RoomObjectiveOutcomeSnapshot BuildBandedTerminalOutcome(ObjectiveOutcomeKind outcome)
    {
        return new RoomObjectiveOutcomeSnapshot(
            BuildTerminalCategory(true, outcome),
            BuildTerminalCategory(false, outcome),
            BuildTerminalCategory(false, outcome));
    }

    private static ObjectiveCategoryProgress BuildTerminalCategory(bool requested, ObjectiveOutcomeKind outcome)
    {
        return new ObjectiveCategoryProgress(
            requested,
            0,
            0,
            0,
            false,
            requested && outcome == ObjectiveOutcomeKind.Failed,
            requested && outcome == ObjectiveOutcomeKind.Deferred,
            requested && outcome == ObjectiveOutcomeKind.Preempted);
    }

    private RoomObjectiveOutcomeResult ApplyRoomObjectiveRetryPolicy(
        int roomIndex,
        RoomPlanEntry? entry,
        RoomObjectiveOutcomeResult outcome)
    {
        var mandatoryExecution = FindFailedMandatoryExecution(outcome);
        int previousFailures = 0;
        if (mandatoryExecution.HasValue &&
            this.Objectives.ObjectiveLedger.TryGetObjective(mandatoryExecution.Value.Identity.ObjectiveId, out var mandatoryObjective) == true)
        {
            previousFailures = mandatoryObjective.FailureCount;
        }
        var decision = RoomObjectiveOutcomePlanner.DecideRetry(new RoomObjectiveRetrySnapshot(
            HoardRequested: entry?.ShouldProbeHoard == true,
            HoardOutcome: outcome.Hoard,
            IntelRequested: entry?.ShouldVisitForIntel == true,
            IntelOutcome: outcome.Intel,
            ChestsRequested: entry?.ShouldSearchChests == true,
            ChestsOutcome: outcome.Chests,
            PreviousMandatoryFailureCount: previousFailures));

        if (decision.SkipOptionalChests)
            outcome = outcome with { Chests = ObjectiveOutcomeKind.Skipped };

        if (decision.BlockMandatory)
        {
            if (!mandatoryExecution.HasValue)
                throw new InvalidOperationException("Mandatory retry policy blocked without an active mandatory objective identity.");
            SearchState.MandatoryObjectiveBlocked = true;
            SearchState.MandatoryObjectiveBlockedRoom = roomIndex;
            SearchState.MandatoryObjectiveBlockedIdentity = mandatoryExecution.Value.Identity;
            SearchState.MandatoryObjectiveBlockedCategory = mandatoryExecution.Value.Category;
            SearchState.MandatoryObjectiveBlockedKind = mandatoryExecution.Value.Kind;
            SearchState.MandatoryObjectiveBlockedEvidenceVersion = PlanningState.PendingEvidenceVersion;
            SearchState.ObjectiveRetryNotBefore = DateTime.MinValue;
            _status = BuildMandatoryObjectiveBlockedStatus();
            Service.Log.Error($"[FloorPhase] {_status}");
            RecordReplayEvent("mandatory-room-objective-blocked", new
            {
                roomIndex,
                failureCount = decision.MandatoryFailureCount,
                failureLimit = RoomObjectiveOutcomePlanner.MandatoryFailureLimit,
                retryBackoffMilliseconds = RoomObjectiveOutcomePlanner.RetryBackoffMilliseconds,
                hoard = outcome.Hoard.ToString(),
                intel = outcome.Intel.ToString()
            });
        }
        else if (decision.RetryMandatory)
        {
            SearchState.ObjectiveRetryNotBefore = DateTime.UtcNow.AddMilliseconds(RoomObjectiveOutcomePlanner.RetryBackoffMilliseconds);
            _status = $"Mandatory hoard work failed in room {roomIndex}; retry {decision.MandatoryFailureCount + 1}/{RoomObjectiveOutcomePlanner.MandatoryFailureLimit} in {RoomObjectiveOutcomePlanner.RetryBackoffMilliseconds / 1000.0:F1}s";
        }

        return outcome;
    }

    private ActiveObjectiveExecution? FindFailedMandatoryExecution(in RoomObjectiveOutcomeResult outcome)
    {
        var objectiveRecords = this.ActiveExecution?.ObjectiveRecords;
        if (objectiveRecords == null)
            return null;

        foreach (var execution in objectiveRecords)
        {
            if (execution.Required && RoomObjectiveOutcomePlanner.IsRetryableFailure(GetCategoryOutcome(execution.Category, outcome)))
                return execution;
        }

        return null;
    }

    private bool ShouldPauseForObjectiveRetry()
    {
        if (SearchState.MandatoryObjectiveBlocked)
        {
            bool newEvidence = PlanningState.PendingEvidenceVersion > SearchState.MandatoryObjectiveBlockedEvidenceVersion;
            if ((_executor?.HasAuthoritativeHoardResolution ?? false) || newEvidence)
            {
                if (SearchState.MandatoryObjectiveBlockedIdentity.HasValue &&
                    this != null &&
                    _executor?.HasAuthoritativeHoardResolution != true)
                {
                    if (!this.Objectives.ObjectiveLedger.ResetFailureCount(SearchState.MandatoryObjectiveBlockedIdentity.Value))
                    {
                        RecordObjectiveExecutionRejected(
                            SearchState.MandatoryObjectiveBlockedIdentity.Value,
                            SearchState.MandatoryObjectiveBlockedKind,
                            SearchState.MandatoryObjectiveBlockedCategory,
                            "EvidenceFailureReset",
                            "StaleIdentity");
                        _status = "Blocked mandatory objective failure count could not reset: stale identity";
                        return true;
                    }
                }
                SearchState.MandatoryObjectiveBlocked = false;
                SearchState.MandatoryObjectiveBlockedRoom = -1;
                SearchState.MandatoryObjectiveBlockedIdentity = null;
                SearchState.MandatoryObjectiveBlockedCategory = default;
                SearchState.MandatoryObjectiveBlockedKind = FloorObjectiveKind.None;
                SearchState.MandatoryObjectiveBlockedEvidenceVersion = 0;
                _status = newEvidence
                    ? "New hoard evidence received; resuming mandatory work"
                    : "Mandatory hoard work resolved; resuming floor flow";
                return false;
            }

            _status = BuildMandatoryObjectiveBlockedStatus();
            return true;
        }

        if (DateTime.UtcNow < SearchState.ObjectiveRetryNotBefore)
        {
            double remaining = Math.Max(0, (SearchState.ObjectiveRetryNotBefore - DateTime.UtcNow).TotalSeconds);
            _status = $"Waiting {remaining:F1}s before retrying mandatory hoard work";
            return true;
        }

        SearchState.ObjectiveRetryNotBefore = DateTime.MinValue;
        return false;
    }

    private string BuildMandatoryObjectiveBlockedStatus()
    {
        return $"Blocked: mandatory hoard work in room {SearchState.MandatoryObjectiveBlockedRoom} failed {RoomObjectiveOutcomePlanner.MandatoryFailureLimit} times; waiting for new evidence or manual intervention";
    }

    private unsafe void HandleVisibleBandedDetection(InstanceContentDeepDungeon* dd, IPlayerCharacter player, int roomIndex, Vector3 bandedPosition)
    {
        var executor = _executor;
        if (executor == null)
            return;
        ClearPostRoomPomanderRetry();
        if (!RefreshCachedHoardIndicator(dd))
        {
            _status = "Waiting for hoard indicator evidence...";
            return;
        }

        _taskRunner!.Reset();
        _activeWaypoint = null;
        if (!PreemptActiveObjectiveExecutions("BandedReentry"))
            return;
        this.ActiveExecution!.Objective = FloorObjectiveKind.OpenVisibleBandedChest;
        _searchExecutionKind = SearchExecutionKind.BandedReentry;

        bool started = roomIndex >= 0 && executor.StartBandedOnlyRoomSearch(dd, roomIndex, player.Position, bandedPosition);
        if (roomIndex >= 0 && !BeginRoomObjectiveExecutions(roomIndex, null))
            return;
        if (started)
        {
            _status = "Banded detected, opening it";
            RecordReplayEvent("room-search-switched-to-banded", new
            {
                roomIndex,
                executionKind = _searchExecutionKind.ToString(),
                x = bandedPosition.X,
                y = bandedPosition.Y,
                z = bandedPosition.Z
            });
        }
        else if (roomIndex >= 0)
        {
            FinalizeRoomSearch(
                dd,
                player,
                roomIndex,
                useRoomFinishPomander: true,
                allowBandedRevealExpectation: false,
                explicitOutcome: BuildBandedTerminalOutcome(ObjectiveOutcomeKind.Failed),
                finalizeReason: "BandedRoomSearchBuildFailed");
        }
    }

    private unsafe bool TryCompleteSearchExecution()
    {
        var executor = _executor!;
        if (executor.IsComplete && executor.IsHoardEvidenceUnstable)
        {
            _status = $"Waiting for hoard evidence ({executor.HoardEvidenceState})";
            RecordHoardEvidenceWait("search-waiting-hoard-evidence");
            return false;
        }
        EndHoardEvidenceWait("search-wait-ended", "search-waiting-hoard-evidence");

        if (!executor.IsComplete)
            return false;

        _taskRunner!.Reset();
        _navDriver!.Cancel();
        _activeWaypoint = null;
        _searchExecutionKind = SearchExecutionKind.PlannedRoom;
        _chaseHelper.Reset();
        ResetPatrolPlan();
        _status = _ctx!.Duty.PassageOpen
            ? "Search complete, passage ready"
            : "Search complete, activating passage";
        RecordReplayEvent("floor-active-mechanic-completed", new
        {
            mechanic = "Search",
            reason = "search-complete",
            nextObjective = (_ctx.Duty.PassageOpen
                ? FloorObjectiveKind.EnterPassage
                : FloorObjectiveKind.ActivatePassage).ToString()
        });
        return true;
    }

    private unsafe void RegeneratePlanForEvidenceIfIdle(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        if (_executor == null ||
            !PlanningState.RefreshRequested ||
            _taskRunner?.Phase != TaskPhase.Idle ||
            _executor.RoomContext != null)
        {
            return;
        }

        var normalGraph = this.NormalGraph;
        if (normalGraph == null)
        {
            return;
        }

        long evidenceVersion = PlanningState.PendingEvidenceVersion;
        _executor.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
        MarkPlanRefreshConsumed(evidenceVersion);
        RecordReplayEvent("floor-plan-regenerated-evidence", BuildPlanReplayPayload(dd->Floor, "search-idle-evidence-refresh"));
    }

    private void UpdateTaskStatus()
    {
        if (_activeWaypoint == null) return;
        var wp = _activeWaypoint.Value;

        switch (_taskRunner!.Phase)
        {
            case TaskPhase.Traveling:
                if (_navHelper!.StuckRetryCount > 0)
                    _status = $"Stuck, repathing ({_navHelper.StuckRetryCount}/3)";
                else if (wp.Type == RoomObjectiveType.Trap)
                    _status = $"Navigating to trap ({_executor!.RemainingWaypointCount} remaining)";
                else
                    _status = $"Navigating to {DescribeChest(wp.Type)}";
                break;

            case TaskPhase.WaitingPost:
                if (wp.Type == RoomObjectiveType.Trap)
                {
                    double remaining = Math.Max(0, TrapStandDurationSeconds - _taskRunner.ElapsedSeconds);
                    _status = $"Standing to reveal ({remaining:F1}s)";
                }
                else
                {
                    _status = $"Opening {DescribeChest(wp.Type)}... ({_taskRunner.ElapsedSeconds:F1}s)";
                }
                break;

            case TaskPhase.WaitingPre:
                {
                    var p = Service.LocalPlayer;
                    if (p != null)
                    {
                        float hpPct = (float)p.CurrentHp / Math.Max(1u, p.MaxHp);
                        double remaining = Math.Max(0, SilverWaitTimeoutSeconds - _taskRunner.ElapsedSeconds);
                        _status = $"Waiting for HP ({hpPct * 100:F0}%, {remaining:F0}s)";
                    }
                    break;
                }
        }
    }

    private static bool IsChestWaypoint(RoomWaypoint waypoint)
    {
        return waypoint.Type != RoomObjectiveType.Trap;
    }

    private void RecordChestInteractionStarted(
        RoomWaypoint waypoint,
        ChestLifecycleSnapshot snapshot,
        bool retry)
    {
        var chest = snapshot.Evidence;
        RecordReplayEvent("chest-interaction-started", new
        {
            roomIndex = _executor?.RoomContext?.RoomIndex ?? -1,
            waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1,
            executionKind = _searchExecutionKind.ToString(),
            chestType = waypoint.Type.ToString(),
            entityId = snapshot.ExpectedEntityId,
            interactionStartedAtUtc = snapshot.InteractionStartedAtUtc,
            completionSource = DescribeNativeChestCompletionSource(chest.NativeCompletionKind),
            nativeStateAvailable = chest.NativeStateAvailable,
            nativeCompletionKind = chest.NativeCompletionKind.ToString(),
            nativeIsTargetable = chest.Object.IsTargetable,
            nativeTreasureState = chest.State.ToString(),
            nativeTreasureFlags = chest.Flags.ToString(),
            evidenceSequenceAtStart = snapshot.EvidenceSequenceAtStart,
            retry,
            x = waypoint.Position.X,
            y = waypoint.Position.Y,
            z = waypoint.Position.Z
        });
    }

    private bool IsChestAccepted(RoomWaypoint waypoint)
    {
        if (_ctx?.ChestInteraction.IsAccepted(
                ActiveChestAttempt,
                waypoint,
                this.ObjectEvidence.Current,
                out var snapshot,
                out bool newlyAccepted) != true)
            return false;

        if (newlyAccepted)
        {
            var chest = snapshot.Evidence;
            RecordReplayEvent("chest-native-state-accepted", new
            {
                roomIndex = _executor?.RoomContext?.RoomIndex ?? -1,
                waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1,
                executionKind = _searchExecutionKind.ToString(),
                chestType = waypoint.Type.ToString(),
                completionSource = DescribeNativeChestCompletionSource(chest.NativeCompletionKind),
                expectedEntityId = snapshot.ExpectedEntityId,
                evidenceEntityId = chest.Object.EntityId,
                nativeStateAvailable = chest.NativeStateAvailable,
                nativeCompletionKind = chest.NativeCompletionKind.ToString(),
                nativeIsTargetable = chest.Object.IsTargetable,
                nativeTreasureState = chest.State.ToString(),
                nativeTreasureFlags = chest.Flags.ToString(),
                evidenceSequenceAtStart = snapshot.EvidenceSequenceAtStart,
                evidenceSequence = snapshot.EvidenceSequence,
                elapsedSinceInteractionStartedMilliseconds =
                    (DateTime.UtcNow - snapshot.InteractionStartedAtUtc).TotalMilliseconds,
                completionStatus = snapshot.Decision.Status.ToString()
            });
        }

        return true;
    }

    private static string DescribeNativeChestCompletionSource(NativeTreasureCompletionKind kind)
    {
        return kind switch
        {
            NativeTreasureCompletionKind.TreasureState => "FFXIVClientStructs.Treasure",
            NativeTreasureCompletionKind.EventObjectTargetable => "global::Dalamud.EventObj.IsTargetable",
            _ => "None"
        };
    }


    private static string DescribeChest(RoomObjectiveType type)
    {
        return type switch
        {
            RoomObjectiveType.ChestBanded => "banded chest",
            RoomObjectiveType.ChestGold => "gold chest",
            RoomObjectiveType.ChestSilver => "silver chest",
            RoomObjectiveType.ChestBronze => "bronze chest",
            _ => "chest"
        };
    }

    private unsafe bool RefreshCachedHoardIndicator(InstanceContentDeepDungeon* dd)
    {
        if (_ctx?.ControlledPtSurvey != null)
            return true;

        if (_chatWatchers?.ChatSaysNoHoard == true)
        {
            if (HandleNoHoardEvidenceInvalidated("no-hoard-refresh"))
                RequestPlanRefresh("no-hoard-refresh");
            return true;
        }
        if (_executor?.CanAcceptHoardIndicator != true)
            return true;

        var before = _executor?.CachedHoardIndicatorPos;
        if (!BandedChestLocator.TryFindHoardIndicatorMatch(this.ObjectEvidence.Current!, out var indicator))
            return false;
        if (indicator.HasValue)
        {
            var match = indicator.Value;
            _executor!.UpdateCachedHoardIndicator(match.Position);
            var after = _executor.CachedHoardIndicatorPos;
            if (after.HasValue && (!before.HasValue || Vector3.DistanceSquared(before.Value, after.Value) > 0.01f))
            {
                RequestPlanRefresh("hoard-indicator-updated");
                RecordReplayEvent("cached-hoard-indicator-updated", new
                {
                    x = after.Value.X,
                    y = after.Value.Y,
                    z = after.Value.Z
                });

                var now = DateTime.UtcNow;
                var player = Service.LocalPlayer;
                float? distanceToPlayer = player != null
                    ? Vector3.Distance(player.Position, after.Value)
                    : null;
                int playerRoom = dd != null ? RoomGraph.GetLocalPlayerRoomIndex(dd) : -1;
                var activeWaypoint = _activeWaypoint;
                var lastTrapTriggered = SearchState.LastTrapTriggered;
                var lastTrapCompleted = SearchState.LastTrapCompleted;
                int? elapsedSinceLastTrapTriggeredMs = lastTrapTriggered.HasValue
                    ? (int)Math.Max(0, (now - lastTrapTriggered.Value.TimestampUtc).TotalMilliseconds)
                    : null;
                int? elapsedSinceLastTrapCompletedMs = lastTrapCompleted.HasValue
                    ? (int)Math.Max(0, (now - lastTrapCompleted.Value.TimestampUtc).TotalMilliseconds)
                    : null;
                string? activeWaypointType = activeWaypoint.HasValue ? activeWaypoint.Value.Type.ToString() : null;
                float? activeWaypointX = activeWaypoint.HasValue ? activeWaypoint.Value.Position.X : null;
                float? activeWaypointY = activeWaypoint.HasValue ? activeWaypoint.Value.Position.Y : null;
                float? activeWaypointZ = activeWaypoint.HasValue ? activeWaypoint.Value.Position.Z : null;
                string? lastTrapTriggeredExecutionKind = lastTrapTriggered?.ExecutionKind.ToString();
                string? lastTrapCompletedExecutionKind = lastTrapCompleted?.ExecutionKind.ToString();
                float? lastTrapTriggeredX = lastTrapTriggered?.Position.X;
                float? lastTrapTriggeredY = lastTrapTriggered?.Position.Y;
                float? lastTrapTriggeredZ = lastTrapTriggered?.Position.Z;
                float? lastTrapCompletedX = lastTrapCompleted?.Position.X;
                float? lastTrapCompletedY = lastTrapCompleted?.Position.Y;
                float? lastTrapCompletedZ = lastTrapCompleted?.Position.Z;
                RecordReplayEvent("hoard-indicator-visible", new
                {
                    floor = dd != null ? dd->Floor : (byte)0,
                    playerRoom,
                    roomIndex = _executor?.RoomContext?.RoomIndex ?? -1,
                    waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1,
                    activeWaypointType,
                    activeWaypointAtObservationX = activeWaypointX,
                    activeWaypointAtObservationY = activeWaypointY,
                    activeWaypointAtObservationZ = activeWaypointZ,
                    executionKind = _searchExecutionKind.ToString(),
                    duringTrapSearch = activeWaypoint.HasValue && activeWaypoint.Value.Type == RoomObjectiveType.Trap,
                    lastTrapTriggeredAtUtc = lastTrapTriggered?.TimestampUtc,
                    lastTrapTriggeredRoom = lastTrapTriggered?.RoomIndex,
                    lastTrapTriggeredWaypoint = lastTrapTriggered?.WaypointIndex,
                    lastTrapTriggeredExecutionKind,
                    lastTrapTriggeredX,
                    lastTrapTriggeredY,
                    lastTrapTriggeredZ,
                    elapsedSinceLastTrapTriggeredMs,
                    lastTrapCompletedAtUtc = lastTrapCompleted?.TimestampUtc,
                    lastTrapCompletedRoom = lastTrapCompleted?.RoomIndex,
                    lastTrapCompletedWaypoint = lastTrapCompleted?.WaypointIndex,
                    lastTrapCompletedExecutionKind,
                    lastTrapCompletedX,
                    lastTrapCompletedY,
                    lastTrapCompletedZ,
                    elapsedSinceLastTrapCompletedMs,
                    distanceToPlayer,
                    matchedObjectBaseId = match.BaseId,
                    matchedObjectGameObjectId = match.GameObjectId,
                    matchedObjectEntityId = match.EntityId,
                    matchedObjectIndex = match.ObjectIndex,
                    matchedObjectName = match.Name,
                    matchedObjectKind = match.ObjectKind,
                    matchedObjectSubKind = match.SubKind,
                    matchedObjectIsTargetable = match.IsTargetable,
                    matchedObjectIsBandedChest = match.IsBandedChest,
                    matchedObjectAddress = match.Address,
                    x = after.Value.X,
                    y = after.Value.Y,
                    z = after.Value.Z
                });
            }
        }

        return true;
    }

    private void RecordWaypointStarted(RoomWaypoint waypoint)
    {
        int roomIndex = _executor?.RoomContext?.RoomIndex ?? -1;
        int waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1;
        RecordReplayEvent("waypoint-started", new
        {
            roomIndex,
            waypointIndex,
            executionKind = _searchExecutionKind.ToString(),
            objectiveType = waypoint.Type.ToString(),
            arrivalRadius = waypoint.ArrivalRadius,
            x = waypoint.Position.X,
            y = waypoint.Position.Y,
            z = waypoint.Position.Z
        });
    }

    private void RecordWaypointResolved(
        RoomWaypoint waypoint,
        WaypointOutcomeKind outcome,
        string reason,
        double elapsedSeconds,
        TaskPhase taskPhaseBefore)
    {
        int roomIndex = _executor?.RoomContext?.RoomIndex ?? -1;
        int waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1;
        string recordedOutcome = outcome switch
        {
            WaypointOutcomeKind.Completed => ObjectiveOutcomeKind.Succeeded.ToString(),
            WaypointOutcomeKind.PolicySkipped => "Skipped",
            _ => outcome.ToString()
        };
        var payload = new
        {
            roomIndex,
            waypointIndex,
            executionKind = _searchExecutionKind.ToString(),
            objectiveType = waypoint.Type.ToString(),
            outcome = recordedOutcome,
            taskPhase = taskPhaseBefore.ToString(),
            reason,
            elapsedSeconds,
            x = waypoint.Position.X,
            y = waypoint.Position.Y,
            z = waypoint.Position.Z
        };

        RecordReplayEvent(outcome == WaypointOutcomeKind.Completed ? "waypoint-completed" : "waypoint-skipped", payload);

        if (waypoint.Type == RoomObjectiveType.Trap)
        {
            if (outcome == WaypointOutcomeKind.Completed)
            {
                SearchState.LastTrapCompleted = new TrapObservation(
                    DateTime.UtcNow,
                    roomIndex,
                    waypointIndex,
                    _searchExecutionKind,
                    waypoint.Position);
                this.ObjectEvidence.Invalidate();
            }
        }
        else
        {
            var snapshot = _ctx?.ChestInteraction.Observe(ActiveChestAttempt, this.ObjectEvidence.Current) ?? default;
            var nativeChest = snapshot.Evidence;
            RecordReplayEvent("chest-resolved", new
            {
                roomIndex,
                waypointIndex,
                executionKind = _searchExecutionKind.ToString(),
                chestType = waypoint.Type.ToString(),
                outcome = recordedOutcome,
                reason,
                taskPhase = taskPhaseBefore.ToString(),
                elapsedSeconds,
                elapsedSinceInteractionStartedMilliseconds = snapshot.InteractionStartedAtUtc == DateTime.MinValue
                    ? (double?)null
                    : (DateTime.UtcNow - snapshot.InteractionStartedAtUtc).TotalMilliseconds,
                completionSource = outcome == WaypointOutcomeKind.Completed
                    ? DescribeNativeChestCompletionSource(nativeChest.NativeCompletionKind)
                    : "None",
                expectedEntityId = snapshot.ExpectedEntityId,
                evidenceEntityId = nativeChest.Object.EntityId,
                nativeStateAvailable = nativeChest.NativeStateAvailable,
                nativeCompletionKind = nativeChest.NativeCompletionKind.ToString(),
                nativeIsTargetable = nativeChest.Object.IsTargetable,
                nativeTreasureState = nativeChest.State.ToString(),
                nativeTreasureFlags = nativeChest.Flags.ToString(),
                evidenceSequenceAtStart = snapshot.EvidenceSequenceAtStart,
                evidenceSequence = snapshot.EvidenceSequence,
                completionStatus = snapshot.Decision.Status.ToString(),
                nativeAcceptanceRecorded = snapshot.AcceptanceRecorded,
                x = waypoint.Position.X,
                y = waypoint.Position.Y,
                z = waypoint.Position.Z
            });
        }
    }

    private void StartBandedRevealExpectation(int sourceRoomIndex)
    {
        if (this.IsDisposed)
            return;

        var expectation = new BandedRevealPending(
            DateTime.UtcNow.AddSeconds(BandedRevealExpectationSeconds),
            this.ObjectEvidence.Current?.RefreshSequence ?? 0);
        this.BandedRevealExpectation = expectation;
        RecordReplayEvent("banded-reveal-expectation-started", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            sourceRoomIndex,
            evidenceSequence = expectation.EvidenceSequence,
            durationSeconds = BandedRevealExpectationSeconds,
            reason = "hoard-room-search-finished"
        });
    }

    private bool IsBandedRevealExpectationPending()
    {
        if (this.IsDisposed || this.BandedRevealExpectation is not { } expectation)
            return false;

        if (_executor?.HasOpenedHoardThisFloor == true)
        {
            ClearBandedRevealExpectation("hoard-opened");
            return false;
        }
        if (_executor?.ConfigSnapshot.BandedEnabled != true)
        {
            ClearBandedRevealExpectation("banded-disabled");
            return false;
        }
        if (DateTime.UtcNow >= expectation.ExpiresAtUtc &&
            this.ObjectEvidence.Current is { Available: true } evidence &&
            evidence.RefreshSequence > expectation.EvidenceSequence)
        {
            ClearBandedRevealExpectation("expired");
            return false;
        }

        return true;
    }

    private void ClearBandedRevealExpectation(string reason)
    {
        if (this.BandedRevealExpectation is not { } expectation)
            return;

        RecordReplayEvent("banded-reveal-expectation-ended", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            evidenceSequence = this.ObjectEvidence.Current?.RefreshSequence ?? 0,
            startedAtEvidenceSequence = expectation.EvidenceSequence,
            reason
        });
        this.BandedRevealExpectation = null;
    }

    private void StartPostRoomPomanderRetry(int roomIndex, bool searchCompletePending)
    {
        SearchState.PostRoomPomanderRetry = new PostRoomPomanderRetry
        {
            FinishedRoomIndex = roomIndex,
            ExpiresAt = DateTime.UtcNow.AddSeconds(PostRoomPomanderRetrySeconds)
        };
        RecordReplayEvent("post-room-pomander-retry-started", new
        {
            roomIndex,
            searchCompletePending,
            reason = "optional-pomander-retry"
        });
        Service.Log.Info(searchCompletePending
            ? $"[FloorPhase] Room {roomIndex} complete, starting {PostRoomPomanderRetrySeconds:F1}s post-room pomander retry alongside search-complete transition"
            : $"[FloorPhase] Room {roomIndex} complete, starting {PostRoomPomanderRetrySeconds:F1}s post-room pomander retry");
    }

    private void ClearPostRoomPomanderRetry()
    {
        SearchState.PostRoomPomanderRetry = null;
    }

    private void BeginRoomIntelSettle(int roomIndex)
    {
        SearchState.IntelSettleRoomIndex = roomIndex;
        SearchState.IntelSettleUntil = DateTime.UtcNow.AddSeconds(IntelSettleDurationSeconds);
        _status = $"Waiting for room {roomIndex} hoard evidence";
        RecordReplayEvent("room-intel-settle-started", new
        {
            roomIndex,
            durationSeconds = IntelSettleDurationSeconds
        });
    }

    private unsafe bool UpdateRoomIntelSettle(InstanceContentDeepDungeon* dd, IPlayerCharacter player, int roomIndex)
    {
        if (SearchState.IntelSettleRoomIndex != roomIndex)
            return false;

        if (!RefreshCachedHoardIndicator(dd))
        {
            _status = "Waiting for hoard indicator evidence...";
            return true;
        }
        if (PlanningState.RefreshRequested)
        {
            var normalGraph = this.NormalGraph;
            if (normalGraph == null)
            {
                _status = "Waiting for room graph...";
                return true;
            }

            long evidenceVersion = PlanningState.PendingEvidenceVersion;
            var roomContext = _executor!.RoomContext;
            roomContext?.MarkIntelCompleted();
            bool authoritativeHoardResolved = _executor.HasOpenedHoardThisFloor || dd->HoardCount > PlanningState.LastKnownHoardCount;
            var committedOutcome = RoomObjectiveOutcomePlanner.Decide(
                roomContext?.BuildOutcomeSnapshot(authoritativeHoardResolved) ?? default);
            if (!TryApplyActiveObjectiveOutcomes(roomIndex, committedOutcome, "RoomIntelSettleEvidenceRefresh"))
                return true;
            ClearRoomIntelSettle();
            _executor.ClearRoomContext();
            _executor.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
            MarkPlanRefreshConsumed(evidenceVersion);
            RecordReplayEvent("room-intel-settle-committed", new
            {
                roomIndex,
                intel = committedOutcome.Intel.ToString(),
                markIntelVisited = committedOutcome.MarkIntelVisited,
                evidenceVersion
            });
            RecordReplayEvent("room-intel-settle-replanned", BuildPlanReplayPayload(dd->Floor, "room-intel-settle-evidence-refresh", roomIndex));
            return true;
        }

        if (DateTime.UtcNow < SearchState.IntelSettleUntil)
        {
            _status = $"Waiting for room {roomIndex} hoard evidence";
            return true;
        }

        _executor?.RoomContext?.MarkIntelCompleted();
        ClearRoomIntelSettle();
        return false;
    }

    private void ClearRoomIntelSettle()
    {
        SearchState.IntelSettleRoomIndex = -1;
        SearchState.IntelSettleUntil = DateTime.MinValue;
    }

    private void ExpirePostRoomPomanderRetry()
    {
        if (SearchState.PostRoomPomanderRetry != null)
        {
            Service.Log.Info($"[FloorPhase] Post-room pomander retry expired for room {SearchState.PostRoomPomanderRetry.FinishedRoomIndex}");
        }

        ClearPostRoomPomanderRetry();
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
        if (_ctx?.ControlledPtSurvey != null)
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
        return snapshot.CanAttemptPomanderUse &&
               snapshot.BandedEnabled &&
               !snapshot.HasOpenedHoardThisFloor &&
               FloorsetHoardDistributionPolicy.AllowsHoardPomander(
                   snapshot.HoardOpportunity) &&
               !snapshot.IntuitionActive &&
               !snapshot.IntuitionUsable &&
               !snapshot.SightUseBlocked &&
               !snapshot.SightUsable &&
               GetStoneCountAvailableForFloorUse(2) > 0;
    }

    private unsafe void TryUseGeneralAutoPomander(InstanceContentDeepDungeon* dd)
    {
        if (_ctx?.ControlledPtSurvey != null)
            return;
        if (ActiveChestAttempt is { PendingGoldOvercapSlotIndex: not null } or { PendingSilverOvercapDemicloneRowId: not null })
            return;

        var decision = GeneralAutoPomanderPlanner.Decide(BuildGeneralAutoPomanderSnapshot(allowStatusOverlap: false, HasHarmfulFloorEffect(dd)));
        if (decision.ShouldUse &&
            TryUsePomander(decision.SlotIndex!.Value, dd, decision.Reason!))
        {
            return;
        }
    }

    private unsafe FloorInitSnapshot BuildFloorInitSnapshot(
        InstanceContentDeepDungeon* dd,
        bool hasHarmfulFloorEffect)
    {
        bool pomandersUsableThisFloor =
            DeepDungeonFloorItemUsePolicy.CanUsePomanders(
                dd->DeepDungeonBanId);
        return new FloorInitSnapshot
        {
            CanAttemptPomanderUse = CanAttemptPomanderUse(),
            BandedEnabled = _executor?.ConfigSnapshot.BandedEnabled ?? false,
            HasOpenedHoardThisFloor = _executor?.HasOpenedHoardThisFloor ?? false,
            HoardOpportunity = DeepDungeonFloorsetTracker.GetCurrentOpportunity(dd->Floor),
            IntuitionActive = _nativeIntuitionActive,
            UsedIntuitionThisFloor = _chatWatchers?.UsedIntuitionThisFloor == true,
            SightUseBlocked = IsSightUseBlocked(),
            IntuitionUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.IntuitionPomanderSlotIndex),
            SightUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.SightPomanderSlotIndex),
            AffluenceUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.AffluencePomanderSlotIndex),
            StrengthUsable = pomandersUsableThisFloor && CombatBuffUsable(FloorInitPlanner.StrengthPomanderSlotIndex),
            SteelUsable = pomandersUsableThisFloor && CombatBuffUsable(FloorInitPlanner.SteelPomanderSlotIndex),
            PurityUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.PurityPomanderSlotIndex),
            SerenityUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.SerenityPomanderSlotIndex),
            RaisingUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.RaisingPomanderSlotIndex),
            AffluenceActive = _pomanderManager.IsActive(FloorInitPlanner.AffluencePomanderSlotIndex),
            RaisingActive = _pomanderManager.IsActive(FloorInitPlanner.RaisingPomanderSlotIndex),
            HasStrengthStatus = HasLocalPlayerStatus(StrengthStatusId),
            HasSteelStatus = HasLocalPlayerStatus(SteelStatusId),
            HasCurseStatus = HasLocalPlayerStatus(DeepDungeonCurseStatusId),
            HasHarmfulFloorEffect = hasHarmfulFloorEffect
        };
    }

    private unsafe GeneralAutoPomanderSnapshot BuildGeneralAutoPomanderSnapshot(bool allowStatusOverlap, bool hasHarmfulFloorEffect = false)
    {
        if (!CanAttemptPomanderUse())
            return default;
        var efw = EventFramework.Instance();
        var dd = efw != null ? efw->GetInstanceContentDeepDungeon() : null;
        return new GeneralAutoPomanderSnapshot
        {
            CanAttemptPomanderUse = true,
            AffluenceUsable = IsPomanderAvailableForFloorUse(FloorInitPlanner.AffluencePomanderSlotIndex),
            StrengthUsable = CombatBuffUsable(FloorInitPlanner.StrengthPomanderSlotIndex, allowStatusOverlap),
            SteelUsable = CombatBuffUsable(FloorInitPlanner.SteelPomanderSlotIndex, allowStatusOverlap),
            PurityUsable = IsPomanderAvailableForFloorUse(FloorInitPlanner.PurityPomanderSlotIndex),
            SerenityUsable = IsPomanderAvailableForFloorUse(FloorInitPlanner.SerenityPomanderSlotIndex),
            RaisingUsable = IsPomanderAvailableForFloorUse(FloorInitPlanner.RaisingPomanderSlotIndex),
            AffluenceActive = _pomanderManager.IsActive(FloorInitPlanner.AffluencePomanderSlotIndex),
            RaisingActive = _pomanderManager.IsActive(FloorInitPlanner.RaisingPomanderSlotIndex),
            HasStrengthStatus = HasLocalPlayerStatus(StrengthStatusId),
            HasSteelStatus = HasLocalPlayerStatus(SteelStatusId),
            HasCurseStatus = HasLocalPlayerStatus(DeepDungeonCurseStatusId),
            HasHarmfulFloorEffect = hasHarmfulFloorEffect,
            AllowStatusOverlap = allowStatusOverlap,
            FlightUsable = IsPomanderAvailableForFloorUse(FloorInitPlanner.FlightPomanderSlotIndex),
            FortuneUsable = IsPomanderAvailableForFloorUse(FloorInitPlanner.FortunePomanderSlotIndex),
            HasteUsable = dd != null && dd->DeepDungeonId == 4 && CombatBuffUsable(FloorInitPlanner.HastePomanderSlotIndex, allowStatusOverlap),
            FlightActive = _pomanderManager.IsActive(FloorInitPlanner.FlightPomanderSlotIndex),
            FortuneActive = _pomanderManager.IsActive(FloorInitPlanner.FortunePomanderSlotIndex),
            HasHasteStatus = _pomanderManager.IsActive(FloorInitPlanner.HastePomanderSlotIndex) || HasLocalPlayerStatus(PtHasteStatusId),
            NextFloorIsMob = dd != null && !DeepDungeonHelper.IsBossFloor(dd->DeepDungeonId, (byte)(dd->Floor + 1))
        };
    }

    private unsafe RoomFinishPomanderOutcome ProcessRoomFinishPomander(InstanceContentDeepDungeon* dd)
    {
        if (_ctx?.ControlledPtSurvey != null)
            return RoomFinishPomanderOutcome.NotNeeded;

        if (_chatWatchers == null)
        {
            return RoomFinishPomanderOutcome.NotNeeded;
        }
        var decision = RoomFinishPomanderPlanner.Decide(BuildRoomFinishPomanderSnapshot(dd));
        switch (decision.Kind)
        {
            case RoomFinishPomanderDecisionKind.NotNeeded:
                return RoomFinishPomanderOutcome.NotNeeded;
            case RoomFinishPomanderDecisionKind.PendingRetry:
                return RoomFinishPomanderOutcome.PendingRetry;
            case RoomFinishPomanderDecisionKind.Use:
                TryUsePomander(decision.SlotIndex!.Value, dd, decision.Reason!);
                return RoomFinishPomanderOutcome.PendingRetry;
            default:
                return RoomFinishPomanderOutcome.NotNeeded;
        }
    }

    private unsafe bool ShouldUseGoldChestOvercapPomander(uint slotIndex)
    {
        if (_chatWatchers == null || _executor == null)
        {
            return false;
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
            _executor == null ||
            IsPomanderBlockedForFloor(slotIndex) ||
            !_executor.ConfigSnapshot.BandedEnabled ||
            _executor.HasOpenedHoardThisFloor ||
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
        return new RoomFinishPomanderSnapshot
        {
            CanAttemptPomanderUse = CanAttemptPomanderUse(),
            BandedEnabled = _executor?.ConfigSnapshot.BandedEnabled ?? false,
            HasOpenedHoardThisFloor = _executor?.HasOpenedHoardThisFloor ?? false,
            FloorsetBandedCount = floorsetState.TotalHoardCount,
            HoardOpportunity = FloorsetHoardDistributionPolicy.Decide(
                floorsetState,
                dd->Floor),
            IntuitionActive = _nativeIntuitionActive,
            UsedIntuitionThisFloor = _chatWatchers?.UsedIntuitionThisFloor == true,
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

    private bool CanAttemptPomanderUse(bool allowCombat = false) =>
        FloorItemExecutor.CanAttemptPomanderUse(this.Items, allowCombat);
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
            if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
            {
                _chatWatchers?.MarkIntuitionUsedThisFloor();
                if (_ctx?.ControlledPtSurvey != null)
                {
                    this.Survey.ObserveConfirmedIntuition(pending);
                }
                RecordNativeIntuitionState(
                    $"after-intuition-use:{pending.Reason}:confirmed",
                    force: true);
            }
            else if (slotIndex == FloorInitPlanner.SightPomanderSlotIndex)
            {
                _chatWatchers?.MarkSightAttemptedThisFloor();
                if (pending.Purpose == FloorItemUsePurpose.ControlledReveal)
                    this.Survey.ApplyConfirmedControlledReveal(pending);
                else
                    this.Survey.ApplyConfirmedNaturalReveal(pending,
                        SightResearchRevealResource.Sight);
            }

            if (pending.Purpose == FloorItemUsePurpose.ControlledStrength)
                this.Survey.MarkControlledStrengthHandled();
            if (pending.Purpose == FloorItemUsePurpose.GoldChestOvercap &&
                ActiveChestAttempt?.PendingGoldOvercapSlotIndex == slotIndex)
            {
                ActiveChestAttempt.PendingGoldOvercapSlotIndex = null;
                Service.Log.Info(
                    $"[FloorPhase] Used {DescribePomanderSlot(slotIndex)} to resolve capped gold chest");
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
            byte stoneId = (byte)pending.Key.ItemId;
            if (stoneId is 1 or 2) this.FarmingPassageItemConfirmed = true;
            if (pending.Purpose == FloorItemUsePurpose.SilverChestOvercap &&
                ActiveChestAttempt is { PendingSilverOvercapDemicloneRowId: not null } chestAttempt)
            {
                chestAttempt.PendingSilverOvercapDemicloneRowId = null;
                chestAttempt.NextInteractAt = DateTime.MinValue;
            }
            switch (pending.Purpose)
            {
                case FloorItemUsePurpose.NaturalReveal:
                    _chatWatchers?.MarkSightAttemptedThisFloor();
                    this.Survey.ApplyConfirmedNaturalReveal(pending,
                        SightResearchRevealResource.Mazeroot);
                    break;
                case FloorItemUsePurpose.ControlledReveal:
                    this.Survey.ApplyConfirmedControlledReveal(pending);
                    break;
                case FloorItemUsePurpose.NaturalPoisonfruit:
                    this.Survey.ObserveConfirmedStone(pending.Purpose);
                    break;
                case FloorItemUsePurpose.NaturalPassageMazeroot:
                    this.Survey.ObserveConfirmedStone(pending.Purpose);
                    break;
                case FloorItemUsePurpose.BossSerenity:
                    this.Boss.OnSerenityConfirmed();
                    break;
                case FloorItemUsePurpose.ControlledPoisonfruit:
                    this.Survey.ObserveConfirmedStone(pending.Purpose);
                    break;
            }

            string eventType = pending.Purpose is
                FloorItemUsePurpose.ControlledReveal or
                FloorItemUsePurpose.ControlledPoisonfruit
                    ? "controlled-incense-used"
                    : "incense-used";
            Service.Log.Info(
                $"[FloorPhase] Confirmed PT incense {stoneId} use ({pending.Reason}) on floor {dd->Floor}");
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
        int previous = PlanningState.LastKnownHoardCount;
        _executor!.ObserveHoardCount(current);
        PlanningState.ObserveHoardCount(current);
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

    private unsafe bool UpdatePostRoomPomanderRetry(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        var retry = SearchState.PostRoomPomanderRetry;
        if (retry == null)
            return false;
        if (_activeWaypoint is { } activeWaypoint && IsChestWaypoint(activeWaypoint))
            return false;

        if (_ctx?.Duty.PassageOpen == true && !Service.Condition[ConditionFlag.InCombat])
        {
            RecordReplayEvent("post-room-pomander-retry-discarded", new
            {
                reason = "passage-open-optional-pomander-only",
                finishedRoomIndex = retry.FinishedRoomIndex
            });
            ClearPostRoomPomanderRetry();
            return false;
        }

        if (DateTime.UtcNow >= retry.ExpiresAt)
        {
            ExpirePostRoomPomanderRetry();
            return false;
        }

        switch (ProcessRoomFinishPomander(dd))
        {
            case RoomFinishPomanderOutcome.NotNeeded:
                ClearPostRoomPomanderRetry();
                break;
            case RoomFinishPomanderOutcome.PendingRetry:
                break;
        }

        return false;
    }

    private unsafe void SyncLiveRunOptions(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        if (_executor == null)
            return;

        var options = SnapshotRunOptions();
        if (!_executor.ApplyRunOptions(options))
            return;

        RecordReplayEvent("run-options-synchronized", new
        {
            floor = dd->Floor,
            phase = _phase.ToString(),
            openGold = options.OpenGold,
            openSilver = options.OpenSilver,
            openBronze = options.OpenBronze,
            bandedEnabled = options.BandedEnabled
        });

        var normalGraph = this.NormalGraph;
        if (_phase != FloorPhase.FloorActive ||
            normalGraph == null ||
            _taskRunner?.Phase != TaskPhase.Idle ||
            _executor.RoomContext != null)
        {
            return;
        }

        _executor.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
        RecordReplayEvent("floor-plan-regenerated-options", BuildPlanReplayPayload(dd->Floor, "run-options-changed"));
    }

    private bool ShouldRunGeneralTick() =>
        PlanningState.TryBeginGeneralTick(DateTime.UtcNow, GeneralTickInterval);
    internal DateTime? AttackWindowStartedAt => ActiveExecution?.Recovery.AttackWindowStartedAt;
    internal bool IsAttackHolding(DateTime nowUtc) => ActiveExecution?.Recovery.IsAttackHolding(nowUtc) == true;
    private ObjectiveExecution PatrolExecution =>
        this.ActiveExecution ?? throw new InvalidOperationException("No active patrol objective execution.");
    private List<int> _patrolRooms => PatrolExecution.PatrolRooms;
    private int _patrolIndex { get => PatrolExecution.PatrolIndex; set => PatrolExecution.PatrolIndex = value; }
    private int _currentPatrolRoom { get => PatrolExecution.CurrentPatrolRoom; set => PatrolExecution.CurrentPatrolRoom = value; }
    private unsafe void UpdateClearingMechanics(InstanceContentDeepDungeon* dd)
    {
        var player = Service.LocalPlayer;
        if (player == null) return;

        bool runGeneralTick = ShouldRunGeneralTick();
        if (runGeneralTick)
        {
            SyncLiveRunOptions(dd, player);
        }

        if (_ctx!.Duty.PassageOpen && Service.Condition[ConditionFlag.InCombat])
        {
            RecordPassageExitDelayedByCombat(dd);
        }

        if (runGeneralTick)
        {
            if (UpdatePostRoomPomanderRetry(dd, player))
                return;
        }
        if (!RequireMovementPermission(
                "clearing chase or patrol",
                primaryOwnsOperation: ClearingMovementOwnedByCurrentObjective()))
            return;

        var target = _chaseHelper.GetClearingTarget(
            dd,
            this.NormalGraph,
            this.LastObservedRoomIndex,
            player.Position,
            out var acquisitionFailure,
            allowNewTargets: !_ctx.Duty.PassageOpen);
        if (target != null)
        {
            ClearChaseAcquisitionFailure();
            RecordChaseTargetEvent("clearing-target-selected", target.Value);
            _ctx.SetPreferredAggroTarget(target.Value.GameObjectId, target.Value.Position);

            var currentTarget = CombatTargetingHelpers.GetBattleCharaByGameObjectId(target.Value.GameObjectId);
            bool targetSpecificAggro =
                target.Value.Reason == EnemyChaseTargetReason.Aggro ||
                EnemyChaseHelper.IsAggroedToPlayer(target.Value.GameObjectId);
            bool casting = Service.Condition[ConditionFlag.Casting];
            bool withinLiveTargetHoldRange = IsWithinLiveTargetHoldRange(target.Value, player.Position);
            if (PatrolExecution.Recovery.Update(dd, target.Value, currentTarget, player, targetSpecificAggro,
                    casting || withinLiveTargetHoldRange))
                return;

            bool engaged = targetSpecificAggro;
            float attackRange = _ctx.CombatAssist.GetCachedEngageRange(_ctx.Configuration);
            var targetDelta = new Vector2(target.Value.LivePosition.X - player.Position.X,
                target.Value.LivePosition.Z - player.Position.Z);
            bool engagedInRange = engaged && targetDelta.LengthSquared() <= attackRange * attackRange;
            if (engagedInRange || casting || withinLiveTargetHoldRange || PatrolExecution.Recovery.IsAttackHolding(DateTime.UtcNow))
            {
                _chaseHelper.CompleteCurrentLeg();
                _status = engaged
                    ? "Hostile engaged"
                    : casting
                        ? "Holding position while casting"
                        : "Hostile within attack hold range";
                if (_navHelper!.HasActiveTarget)
                    _navHelper.Cancel();
            }
            else
            {
                var state = _navHelper!.Navigate(
                    target.Value.Position,
                    player.Position,
                    EnemyChaseHelper.NavigationArrivalTolerance);
                if (state == NavigationState.Moving)
                {
                    _status = "Chasing hostile...";
                }
                else if (state == NavigationState.Arrived)
                {
                    _chaseHelper.CompleteCurrentLeg();
                    _status = "Hostile moved; starting next chase leg";
                    PatrolExecution.Recovery.ObserveChaseLegArrival(DateTime.UtcNow);
                    RecordChaseTargetEvent("clearing-chase-leg-completed", target.Value);
                }
                else
                {
                    HandleDirectNavState(state, "chasing hostile");
                    if (state == NavigationState.StuckGiveUp || state == NavigationState.Failed)
                        _chaseHelper.Reset();
                }
            }

            ResetPatrolPlan();
            return;
        }

        ResetEngagedTargetProgress();
        _ctx.ClearPreferredAggroTarget();
        if (_ctx.Duty.PassageOpen && !Service.Condition[ConditionFlag.InCombat])
        {
            _navHelper.Cancel();
            _status = "Passage open; waiting for passage objective";
            return;
        }
        if (RecoveryBudget.AllRecordedTargetsExhausted)
        {
            _ctx.StatusIsError = true;
            _ctx.StatusLine = _status = "No remaining target within the combat position recovery retry limits.";
            RecordReplayEvent("sight-recovery-stopped", new { reason = "all-observed-targets-exhausted", failures = RecoveryBudget.TotalFailures });
            return;
        }
        if (acquisitionFailure != EnemyChaseAcquisitionFailure.None)
        {
            _navHelper!.Cancel();
            ResetPatrolPlan();
            _status = $"Cannot acquire hostile: {acquisitionFailure}";
            RecordChaseAcquisitionFailure(dd, acquisitionFailure);
            return;
        }

        EnsurePatrolPlan(dd);
        if (_patrolRooms.Count == 0)
        {
            _status = "No patrol path available";
            _navHelper!.Cancel();
            return;
        }

        int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
        if (playerRoom >= 0 && playerRoom == _currentPatrolRoom)
            AdvancePatrol();

        for (int attempt = 0; attempt < _patrolRooms.Count; attempt++)
        {
            int targetRoom = _patrolRooms[_patrolIndex];
            _currentPatrolRoom = targetRoom;

            if (CombatPositionRecoveryController.TryResolveRoomDestination(dd, targetRoom, out var dest))
            {
                var state = _navHelper!.Navigate(dest, player.Position, arrivalRadius: 1.5f);
                if (state == NavigationState.Arrived)
                {
                    AdvancePatrol();
                }
                else if (state == NavigationState.Moving)
                {
                    _status = $"Patrolling room {targetRoom}...";
                    return;
                }
                else
                {
                    HandleDirectNavState(state, $"patrolling room {targetRoom}");
                }
                return;
            }

            AdvancePatrol();
        }

        _status = "Patrol complete, idling";
        _navHelper!.Cancel();
    }

    private static bool IsWithinLiveTargetHoldRange(EnemyChaseTarget target, Vector3 playerPosition)
    {
        float dx = target.LivePosition.X - playerPosition.X;
        float dz = target.LivePosition.Z - playerPosition.Z;
        float maxCenterDistance =
            Math.Max(0f, target.HitboxRadius) + EnemyChaseHelper.LiveTargetHoldRange;
        return dx * dx + dz * dz <= maxCenterDistance * maxCenterDistance;
    }

    internal void ResetPatrolPlan()
    {
        var execution = this.ActiveExecution;
        if (execution == null)
            return;
        execution.PatrolRooms.Clear();
        execution.PatrolIndex = 0;
        execution.CurrentPatrolRoom = -1;
    }

    private unsafe void EnsurePatrolPlan(InstanceContentDeepDungeon* dd)
    {
        if (dd == null || _patrolRooms.Count > 0)
            return;

        int startRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
        if (startRoom < 0)
            startRoom = RoomGraph.GetHomeRoomIndex(dd);
        if (startRoom < 0)
            startRoom = 0;

        var order = RoomGraph.BuildReachableRoomOrder(dd, startRoom);
        if (order.Count == 0 && startRoom >= 0)
            order.Add(startRoom);

        _patrolRooms.AddRange(order);
        _patrolIndex = 0;
        _currentPatrolRoom = _patrolRooms.Count > 0 ? _patrolRooms[0] : -1;
    }

    private void AdvancePatrol()
    {
        if (_patrolRooms.Count == 0)
        {
            _currentPatrolRoom = -1;
            return;
        }

        _patrolIndex = (_patrolIndex + 1) % _patrolRooms.Count;
        _currentPatrolRoom = _patrolRooms[_patrolIndex];
    }


    internal void ResetEngagedTargetProgress() => this.ActiveExecution?.Recovery.ResetEngagedTargetProgress();
    internal unsafe void UpdatePassageNavigation(InstanceContentDeepDungeon* dd)
    {
        var player = Service.LocalPlayer;
        if (player == null) return;

        _chaseHelper.Reset();
        ResetPatrolPlan();
        _ctx?.ClearPreferredAggroTarget();
        if (!RequireTransitionPermission("passage navigation", FloorObjectiveKind.EnterPassage))
            return;
        NavigateToPassage(dd, player);
    }

    private unsafe void NavigateToPassage(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        var objectEvidence = this.ObjectEvidence.Current;
        if (objectEvidence?.Available != true ||
            !PassageLocator.TryResolvePassageDestination(dd, objectEvidence, out var dest, out bool usedActor, out var passageRoomIndex))
        {
            _status = "Passage position unknown";
            RecordPassageNavigationEvent("passage-navigation", "Unknown", usedActor: false, passageRoomIndex: -1, playerRoom: RoomGraph.GetLocalPlayerRoomIndex(dd));
            _navDriver!.Reset();
            return;
        }

        int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
        // Knowing the actor does not make a failing cross-room path reachable.
        // Keep the room graph fallback until inside its room, then approach the exact walking point.
        int? targetRoom = PassageNavigationPolicy.RoutingRoom(usedActor, passageRoomIndex, playerRoom);
        if (usedActor) dest = ResolvePassageWalkingPosition(dest);

        var result = _navDriver!.Drive(dest, player.Position, 0.5f, dd, playerRoom, targetRoom);

        switch (result)
        {
            case NavDriveResult.Moving:
                RecordPassageNavigationEvent("passage-navigation", result.ToString(), usedActor, passageRoomIndex, playerRoom);
                _status = _navDriver.IsStaging
                    ? $"Navigating to passage ({_navDriver.StageLabel})"
                    : "Navigating to passage...";
                break;
            case NavDriveResult.Staging:
                RecordPassageNavigationEvent("passage-navigation", result.ToString(), usedActor, passageRoomIndex, playerRoom);
                _status = $"Navigating to passage ({_navDriver.StageLabel})";
                break;
            case NavDriveResult.Arrived:
                if (!usedActor)
                {
                    _status = "At passage room, waiting for passage actor";
                    RecordPassageNavigationEvent("passage-room-center-arrived", result.ToString(), usedActor, passageRoomIndex, playerRoom);
                    _navDriver.Reset();
                    break;
                }
                this.RunTelemetry?.ObservePassageCommit(DateTime.UtcNow);
                bool newlyPositioned = _status != "At passage, waiting for transition";
                _status = "At passage, waiting for transition";
                if (newlyPositioned)
                {
                    Service.Log.Info("[FloorPhase] Positioned at passage, waiting for transition");
                    RecordPassageNavigationEvent("passage-arrived", result.ToString(), usedActor, passageRoomIndex, playerRoom);
                }
                break;
            case NavDriveResult.StuckRetrying:
                this.RunTelemetry?.ObserveNavigationIssue();
                RecordPassageNavigationEvent("passage-navigation", result.ToString(), usedActor, passageRoomIndex, playerRoom);
                _status = $"Stuck, repathing ({_navDriver.StuckRetryCount}/3)";
                break;
            case NavDriveResult.Failed:
                this.RunTelemetry?.ObserveNavigationIssue();
                RecordPassageNavigationEvent("passage-navigation", result.ToString(), usedActor, passageRoomIndex, playerRoom);
                _status = "Passage navigation failed";
                break;
        }
    }
    private void StartActiveWaypointTelemetry(RoomWaypoint waypoint)
    {
        if (_runTelemetryObserver == null ||
            IsDisposed ||
            this.ActiveExecution == null ||
            Service.LocalPlayer is not { } player)
            return;

        if (this.ActiveExecution.WaypointTelemetry != null)
            EndActiveWaypointTelemetry(RunWaypointTerminalOutcome.Aborted, "WaypointReplaced");

        this.ActiveExecution.WaypointTelemetry = new RunWaypointTelemetryTrace(
            DateTime.UtcNow,
            player.ClassJob.RowId,
            this.DungeonId,
            ((this.Floor - 1) / 10) * 10 + 1,
            this.Floor,
            this.Generation,
            _ctx?.ControlledPtSurvey != null,
            _executor?.RoomContext?.RoomIndex ?? -1,
            _executor?.RoomContext?.CurrentWaypointIndex ?? -1,
            _searchExecutionKind.ToString(),
            waypoint.Type.ToString(),
            player.Position,
            waypoint.Position);
    }

    private void SampleActiveWaypointTelemetry(TaskPhase phase, Vector3 position)
    {
        this.ActiveExecution?.WaypointTelemetry?.Sample(
            DateTime.UtcNow,
            position,
            phase,
            Service.Condition[ConditionFlag.InCombat],
            Service.Condition[ConditionFlag.Casting]);
    }

    internal void EndActiveWaypointTelemetry(
        RunWaypointTerminalOutcome outcome,
        string reason,
        TaskPhase? observedPhase = null)
    {
        var execution = this.ActiveExecution;
        var trace = execution?.WaypointTelemetry;
        if (execution == null || trace == null)
            return;

        execution.WaypointTelemetry = null;
        Vector3 position = Service.LocalPlayer?.Position ?? trace.From;
        TaskPhase phase = observedPhase ?? execution.TaskRunner.Phase;
        var terminal = trace.Finish(
            DateTime.UtcNow,
            position,
            phase,
            Service.Condition[ConditionFlag.InCombat],
            Service.Condition[ConditionFlag.Casting],
            execution.TaskRunner.NavigationIssueCount,
            outcome,
            reason);
        this.RunTelemetry?.ObserveNavigationIssue(
            terminal.NavigationIssueCount);
        try
        {
            _runTelemetryObserver?.ObserveWaypointTerminal(terminal);
        }
        catch (Exception ex)
        {
            Service.Log.Error($"[RunTelemetry] Host waypoint observer failed: {ex}");
        }
    }


}
internal enum EnemyChaseRecoveryDecision
{
    None,
    Start,
    Continue,
    TargetProgress
}

internal static class EnemyChaseRecoveryPolicy
{

    public static EnemyChaseRecoveryDecision Decide(
        bool targetAvailable,
        bool targetDead,
        bool targetSpecificAggro,
        bool targetHpDecreased,
        bool attackAttemptWindow,
        TimeSpan noProgress,
        bool recoveryActive)
    {
        if (!targetAvailable || targetDead || targetSpecificAggro || targetHpDecreased)
            return EnemyChaseRecoveryDecision.TargetProgress;
        if (recoveryActive)
            return EnemyChaseRecoveryDecision.Continue;
        if (!attackAttemptWindow)
            return EnemyChaseRecoveryDecision.None;
        return noProgress >= EnemyChaseAttackWindow.NoProgressLimit
            ? EnemyChaseRecoveryDecision.Start
            : EnemyChaseRecoveryDecision.None;
    }
}
