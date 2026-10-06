using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using global::Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using DeepDungeon.Fsd.Dalamud.Map;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor
{
	/// <summary>
	/// In-duty controller with floor lifecycle states and objective-driven active-floor mechanics.
	/// Split across partial files: Search, Patrol, Passage.
	/// </summary>
	public sealed partial class FloorPhaseController
	{
		private RunContext? _ctx;
        private readonly TerminalRoomController _terminalRooms;
		private readonly IFloorEvidenceObserver? _floorEvidenceObserver;
		private readonly IRunTelemetryObserver? _runTelemetryObserver;
		private readonly NativeDeepDungeonLogMessageSource _logMessageSource;
		private NavigationHelper? _navHelper;
		private NavigationDriver? _navDriver;
		private AutoPilotExecutor? _executor => _floorRuntime?.Executor;
		private ChatWatchers? _chatWatchers;
		private DeepDungeonRunRecorder? _runRecorder;
		private FloorEvidenceJournal? _floorEvidenceJournal;
		private readonly PomanderManager _pomanderManager = new();
		private readonly EnemyChaseHelper _chaseHelper = new();

		private FloorPhase _phase = FloorPhase.FloorSetup;
		private FloorExplorationController? _floorRuntime;
        private readonly PassageDestinationResolver _passageDestination = new();
        private Vector3 ResolvePassageWalkingPosition(Vector3 actor) =>
            _passageDestination.Resolve(actor, _floorRuntime?.Generation ?? -1);
		private long _nextFloorGeneration;
		private long _nextRoomSearchRequestId;
		private string _status = "Idle";
		private Pt30DivineFavorFlashHelper? _pt30DivineFavorFlashHelper;
        private Pt50ChaseOutputGuard? _pt50ChaseOutputGuard;
		private bool _wasTransitioning;
		private byte _lastGraphPendingFloor = 255;
		private uint _lastGraphPendingDungeonId;
		private string _activeHoardEvidenceWaitEventType = string.Empty;
		private string _activeHoardEvidenceWaitState = string.Empty;
		private DateTime _activeHoardEvidenceWaitStartedAt = DateTime.MinValue;
		private NativeIntuitionState? _lastNativeIntuitionState;
		private bool _nativeIntuitionActive;
		private bool _nativeIntuitionSampleAvailable;
		private bool? _lastNativeIntuitionReadAvailable;
		private DateTime _nextNativeIntuitionPollAt = DateTime.MinValue;
		private DateTime _lastPassageExitDelayEventAt = DateTime.MinValue;
		private DateTime _lastChaseTargetEventAt = DateTime.MinValue;
		private string _lastChaseTargetEventKey = string.Empty;
		private string _lastChaseAcquisitionFailureKey = string.Empty;
		private DateTime _lastPassageNavigationEventAt = DateTime.MinValue;
		private string _lastPassageNavigationEventKey = string.Empty;
		private DateTime _lastObjectEvidenceTelemetryAt = DateTime.MinValue;
		private DateTime _lastObjectEvidenceUnavailableAt = DateTime.MinValue;
		private bool _controlledReusableSaveSurveyArmed;
		private static readonly TimeSpan NativeIntuitionPollInterval = TimeSpan.FromMilliseconds(250);
		private readonly record struct NativeIntuitionState(bool IsActive, int Count, bool IsUsable);
		private FloorPlanningSession PlanningState =>
			_floorRuntime?.PlanningState ?? throw new InvalidOperationException("No active floor planning state.");
		private PendingIntuitionState PendingIntuition =>
			_floorRuntime?.PendingIntuition ?? throw new InvalidOperationException("No active floor Intuition attempt state.");
		public FloorPhase CurrentPhase => _phase;
		public string Status => _status;
		public string? RunRecorderPath => _runRecorder?.FilePath;
		public ObjectiveArbiterDecision CurrentObjectiveDecision => _floorRuntime?.Objectives.ObjectiveDecision ?? default;
		public bool AllowsCombatChannel =>
			_floorRuntime is { IsDisposed: false } &&
			(Service.Condition[ConditionFlag.InCombat] ||
			 (_floorRuntime.Objectives.HasObjectiveDecision &&
			  _floorRuntime.Objectives.ObjectiveDecision.Channels.Combat != CommandChannelPermission.Blocked));
		public bool AllowsMovementChannel =>
			_floorRuntime?.Objectives.HasObjectiveDecision == true &&
			_floorRuntime.Objectives.ObjectiveDecision.Channels.Movement != CommandChannelPermission.Blocked;
		public bool AllowsTransitionChannel =>
			_floorRuntime?.Objectives.HasObjectiveDecision == true &&
			_floorRuntime.Objectives.ObjectiveDecision.Channels.Transition == CommandChannelPermission.PrimaryObjective;
		public bool AllowsChestSidecarInteraction =>
			_floorRuntime?.Objectives.HasObjectiveDecision == true &&
			_floorRuntime.Objectives.ObjectiveDecision.Channels.Interaction != CommandChannelPermission.Blocked;

		public FloorPhaseController(
			NativeDeepDungeonLogMessageSource logMessageSource,
			IFloorEvidenceObserver? floorEvidenceObserver = null,
			IRunTelemetryObserver? runTelemetryObserver = null)
		{
			_logMessageSource = logMessageSource ?? throw new ArgumentNullException(nameof(logMessageSource));
			_floorEvidenceObserver = floorEvidenceObserver;
			_runTelemetryObserver = runTelemetryObserver;
            _terminalRooms = new TerminalRoomController(status => _status = status, RecordReplayEvent, DestroyFloorRuntime, ResolvePassageWalkingPosition);
		}

		public object ArmControlledReusableSaveSurveyCapture()
		{
			if (_floorRuntime != null)
			{
				return new
				{
					ok = false,
					error = "Controlled reusable-save survey capture must be armed before the stable floor session is created."
				};
			}

			_controlledReusableSaveSurveyArmed = true;
			Service.Log.Info("[FloorEvidenceJournal] Armed one controlled reusable-save survey capture; runtime gate requires Pilgrim's Traverse floor <30.");
			return new
			{
				ok = true,
				mode = FloorEvidenceAcquisitionMode.ControlledReusableSaveSurvey.ToString(),
				constraint = "Pilgrim's Traverse floor <30",
				oneShot = true
			};
		}

		public void Initialize(RunContext context)
		{
			Dispose();
			_ctx = context;
			context.RecordDiagnostic = RecordReplayEvent;
			_chaseHelper.CanSelectNewTarget = context.CanSelectNewCombatTarget;
			_ctx.ClearPreferredAggroTarget();
			_navHelper = new NavigationHelper(_ctx.Navigator);
            _terminalRooms.Initialize(context, _navHelper);
			_navDriver = new NavigationDriver(_navHelper);
			DisposeChatWatchers();
			_chatWatchers = new ChatWatchers(_logMessageSource);
			_chatWatchers.StateChanged += OnChatWatchersStateChanged;
			CloseRunRecording("controller-reinitialized");
			_chaseHelper.Reset();
			_phase = FloorPhase.FloorSetup;
			_nextRoomSearchRequestId = 0;
			_wasTransitioning = false;
			_lastGraphPendingFloor = 255;
			_lastGraphPendingDungeonId = 0;
			_activeHoardEvidenceWaitEventType = string.Empty;
			_activeHoardEvidenceWaitState = string.Empty;
			_activeHoardEvidenceWaitStartedAt = DateTime.MinValue;
			_lastNativeIntuitionState = null;
			_nativeIntuitionActive = false;
			_nativeIntuitionSampleAvailable = false;
			_lastNativeIntuitionReadAvailable = null;
			_nextNativeIntuitionPollAt = DateTime.MinValue;
			_lastPassageExitDelayEventAt = DateTime.MinValue;
			_lastChaseTargetEventAt = DateTime.MinValue;
			_lastChaseTargetEventKey = string.Empty;
			_lastChaseAcquisitionFailureKey = string.Empty;
			_lastPassageNavigationEventAt = DateTime.MinValue;
			_lastPassageNavigationEventKey = string.Empty;
			_lastObjectEvidenceTelemetryAt = DateTime.MinValue;
			_lastObjectEvidenceUnavailableAt = DateTime.MinValue;
			_floorRuntime?.ResetPermissionBlocks();
			_floorRuntime?.ResetEngagedTargetProgress();
			_pt30DivineFavorFlashHelper?.Dispose();
			_pt30DivineFavorFlashHelper = new Pt30DivineFavorFlashHelper(active =>
                _ctx?.SetBossMovementOverride?.Invoke(active) ?? _ctx?.Configuration.BossMechanicsActive != true);
            _pt50ChaseOutputGuard?.Reset();
            _pt50ChaseOutputGuard = new Pt50ChaseOutputGuard(active => _ctx?.SetBossRotationSuppressed?.Invoke(active));
			_floorRuntime?.ResetPatrolPlan();
			_runRecorder = new DeepDungeonRunRecorder(BuildRecorderSessionName());
			try
			{
				_floorEvidenceJournal = new FloorEvidenceJournal(_floorEvidenceObserver);
				Service.Log.Info($"[FloorEvidenceJournal] Local raw journal -> {_floorEvidenceJournal.FilePath}");
			}
			catch (Exception ex)
			{
				_floorEvidenceJournal = null;
				Service.Log.Error($"[FloorEvidenceJournal] Initialization failed; floor evidence will not be recorded: {ex}");
			}
			_status = "Initialized";
			RecordReplayEvent("controller-initialized", new
			{
				mode = _ctx?.Duty.IsInDuty == true ? "in-duty" : "unknown",
				recorderFile = System.IO.Path.GetFileName(_runRecorder.FilePath)
			});
			Service.Log.Info("[FloorPhase] Controller initialized");
			Service.Log.Info($"[FloorPhase] DD run recorder -> {_runRecorder.FilePath}");
		}

		public unsafe void Update(IFramework _)
		{
			_floorRuntime?.ItemUse.BeginUpdate();
			if (_ctx == null)
				return;

			if (!_ctx.Duty.IsInDuty)
			{
				DestroyFloorRuntime(0, "outside-duty");
				return;
			}

			var efw = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.Instance();
			if (efw == null)
			{
				_floorRuntime?.Objectives.ClearObjectiveDecision();
				return;
			}
			var dd = efw->GetInstanceContentDeepDungeon();
			if (dd == null)
			{
				_floorRuntime?.Objectives.ClearObjectiveDecision();
				return;
			}

			try
			{
				bool isTransitioning = _ctx.Duty.IsTransitioning || dd->Floor == 0;
				ObserveDutyTransitionState(dd->Floor, isTransitioning);
				if (TryMarkPlayerDeathFatal(dd))
				{
					DestroyFloorRuntime(dd->Floor, "player-death");
					return;
				}

				if (isTransitioning)
				{
					DestroyFloorRuntime(dd->Floor, "transitioning");
					_status = "Waiting for map to load...";
					_navHelper?.Cancel();
					return;
				}

				if (!IsLoadedFloorReady(dd))
				{
					DestroyFloorRuntime(dd->Floor, "floor-not-ready");
					_status = "Waiting for floor state...";
					_navHelper?.Cancel();
					return;
				}

				bool requiresIntuitionState = !_ctx.Duty.IsBossFloor;
                if (DeepDungeon.Fsd.Runtime.DeepDungeonFloorClassifier.Classify(dd->DeepDungeonId, dd->Floor) ==
                    DeepDungeon.Fsd.Runtime.DeepDungeonFloorKind.Result)
                {
                    _terminalRooms.UpdateResultRoom();
                    return;
                }
				bool nativeIntuitionActive = false;
				bool nativeStateAvailable = !requiresIntuitionState ||
				                            TryGetNativeIntuitionState(out nativeIntuitionActive);
				var readyIntuition = ReadyFloorIntuitionPlanner.Decide(new ReadyFloorIntuitionSnapshot(
					nativeStateAvailable,
					requiresIntuitionState && nativeIntuitionActive));
				if (readyIntuition.Kind == ReadyFloorIntuitionDecisionKind.Wait)
				{
					_floorRuntime?.Objectives.ClearObjectiveDecision();
					_status = "Waiting for native Intuition state...";
					RecordNativeIntuitionState("native-state-unavailable", force: false, nativeStateAvailable, nativeIntuitionActive);
					return;
				}
				_nativeIntuitionActive = readyIntuition.IntuitionActive;

				if (_floorRuntime != null &&
				    (_floorRuntime.Floor != dd->Floor ||
				     _floorRuntime.DungeonId != dd->DeepDungeonId))
				{
					DestroyFloorRuntime(dd->Floor, "floor-changed");
				}

				if (_floorRuntime == null)
				{
					if (!TryBuildFloorRuntime(dd))
						return;
				}

				var activeRuntime = _floorRuntime;
				if (activeRuntime == null || activeRuntime.IsDisposed)
					return;
				activeRuntime.RunTelemetry?.SampleStable(DateTime.UtcNow);
				activeRuntime.ResolvePendingFloorItemUse(dd);
				if (activeRuntime.Kind == FloorRuntimeKind.Normal && !RefreshFloorObjectEvidence(dd, activeRuntime))
					return;
				activeRuntime.Survey.ResolveInheritedIntuition();
				if (_ctx.ControlledPtSurvey?.LeaveRequested == true)
				{
					CancelActiveMovement();
					activeRuntime.Objectives.ClearObjectiveDecision();
					_status = "Controlled PT capture complete; abandoning before further floor movement";
					return;
				}
				activeRuntime.ObserveFloorRuntimeNativeIntuitionEdge();

				if (_ctx?.ControlledPtSurvey == null &&
				    activeRuntime.TryReconcileDelayedHoardEvidence(dd))
				{
					activeRuntime.RefreshObjectiveDecision(dd);
					return;
				}

                if (activeRuntime.TryFinishHarvest(dd)) return;
				activeRuntime.RefreshObjectiveDecision(dd);
				switch (_phase)
				{
					case FloorPhase.FloorSetup:
						activeRuntime.UpdateFloorSetup(dd);
						break;
					case FloorPhase.FloorActive:
						activeRuntime.UpdateFloorActive(dd);
						break;
					case FloorPhase.BossFloor:
						activeRuntime.Boss.Update(dd);
						break;
					case FloorPhase.Done:
						break;
				}

				activeRuntime.RefreshObjectiveDecision(dd);
			}
			catch (Exception ex)
			{
				_floorRuntime?.Objectives.ClearObjectiveDecision();
				Service.Log.Error($"[FloorPhase] Update error: {ex.Message}\n{ex.StackTrace}");
			}
		}

		public void Dispose()
		{
			DestroyFloorRuntime(0, "controller-disposed");
			_controlledReusableSaveSurveyArmed = false;
			EndHoardEvidenceWait("controller-disposed");
			CancelActiveMovement();
			_chaseHelper.Reset();
			DisposeChatWatchers();
			if (_runRecorder != null)
			{
				RecordReplayEvent("controller-disposed", new
				{
					phase = _phase.ToString(),
					status = _status
				});
				_runRecorder.Dispose();
				_runRecorder = null;
			}
			_pt30DivineFavorFlashHelper?.Dispose();
			_pt30DivineFavorFlashHelper = null;
            _pt50ChaseOutputGuard?.Reset();
            _pt50ChaseOutputGuard = null;
			_floorEvidenceJournal?.Dispose();
			_floorEvidenceJournal = null;
		}

		public void CloseRunRecording(string reason, object? details = null)
		{
			if (_runRecorder == null)
				return;

			DeepDungeonRunRecorder recorder = _runRecorder;
			RecordReplayEvent("run-recorder-closing", new
			{
				reason,
				phase = _phase.ToString(),
				status = _status,
				floor = _floorRuntime?.Floor ?? 0,
				details
			});
			recorder.Dispose();
			_runRecorder = null;

			try
			{
				_runTelemetryObserver?.ObserveRunRecordingClosed(
					new RunRecordingClosedTelemetry(
						recorder.StartedAtUtc,
						DateTime.UtcNow,
						recorder.FilePath,
						reason,
						_ctx?.DetailedMap.ScenarioKey,
						_ctx?.DetailedMap.Policy == DetailedMapRuntimePolicy.DetailedMap,
						_ctx?.ControlledPtSurvey != null));
			}
			catch (Exception ex)
			{
				Service.Log.Error($"[RunTelemetry] Host run-recording observer failed: {ex}");
			}
		}

		public void CancelActiveMovement()
		{
			_floorRuntime?.EndActiveWaypointTelemetry(RunWaypointTerminalOutcome.Aborted, "MovementCanceled");
			bool navigationActive = _navHelper?.HasActiveTarget == true;
			_floorRuntime?.ResetTaskRunner(cancelNavigation: false);
			_navDriver?.Reset();
			if (navigationActive)
				_navHelper?.Cancel();
			_floorRuntime?.ClearActiveWaypoint();
			_pt30DivineFavorFlashHelper?.Reset();
			_pt50ChaseOutputGuard?.Reset();
		}

		public bool TickCombatChannel(out string status)
		{
			status = string.Empty;
			var context = _ctx;
			if (context == null || context.FarmingPlan?.ReusesSave == true || !AllowsCombatChannel)
				return false;

			context.CombatAssist.Tick(
				context.Configuration,
				context,
				context.Duty.IsBossFloor,
				context.Duty.PassageOpen,
				out status,
				out _,
				out _);
			return true;
		}

		public Vector3? CachedHoardIndicatorPos => _executor?.CachedHoardIndicatorPos;

		public IReadOnlyList<Vector3> ObservedSightTrapPositions =>
			_executor?.ObservedSightTrapPositions ?? Array.Empty<Vector3>();

		public AutoPilotExecutor.AutoPilotDebugSnapshot? GetDebugSnapshot()
		{
			var snap = _executor?.GetDebugSnapshot();
			if (snap != null)
			{
				snap.Phase = _phase;
				snap.TaskPhase = _floorRuntime?.CurrentTaskPhase ?? TaskPhase.Idle;
				snap.Status = _status;
			}
			return snap;
		}

		private unsafe bool RefreshFloorObjectEvidence(InstanceContentDeepDungeon* dd, FloorExplorationController runtime)
		{
			runtime.Survey.TryArmControlledCandidateObjectAudit(dd);
			IReadOnlyList<ControlledCandidateAuditPoint>? auditUniverse =
				_ctx?.ControlledPtSurvey != null &&
				runtime.Survey.ControlledCandidateObjectAuditArmed
					? runtime.Survey.ControlledCandidateObjectAuditUniverse
					: null;
			var refresh = runtime.ObjectEvidence.RefreshIfDue(runtime.DungeonId, auditUniverse);
			if (refresh.Attempted)
			{
				runtime.ObserveCurrentRoom(dd);
				runtime.Survey.ObserveFloorEvidence(dd);
				runtime.PublishAuthoritativeRunFloorStateIfChanged(dd);
				runtime.Survey.ObserveControlledCandidateObjectMatches(refresh);
			}
			var snapshot = runtime.ObjectEvidence.Current;
			var now = DateTime.UtcNow;
			if (refresh.Attempted &&
			    (refresh.MaterialChanged || now - _lastObjectEvidenceTelemetryAt >= TimeSpan.FromSeconds(1)))
			{
				_lastObjectEvidenceTelemetryAt = now;
				RecordReplayEvent("floor-object-evidence-refreshed", new
				{
					floor = runtime.Floor,
					floorGeneration = runtime.Generation,
					available = snapshot?.Available == true,
					version = snapshot?.Version ?? 0,
					refreshSequence = snapshot?.RefreshSequence ?? 0,
					refreshCount = runtime.ObjectEvidence.RefreshCount,
					fullScanCount = runtime.ObjectEvidence.FullScanCount,
					invalidationCount = runtime.ObjectEvidence.InvalidationCount,
					wasInvalidated = refresh.WasInvalidated,
					materialChanged = refresh.MaterialChanged,
					scanCompleted = refresh.ScanCompleted,
					scannedObjectCount = snapshot?.ScannedObjectCount ?? 0,
					chestCount = snapshot?.Chests.Count ?? 0,
					hoardIndicatorCount = snapshot?.HoardIndicators.Count ?? 0,
					sightTrapIndicatorCount = snapshot?.SightTrapIndicators.Count ?? 0,
					passageActorCount = snapshot?.PassageActors.Count ?? 0
				});
			}

			if (snapshot?.Available == true)
				return true;

			runtime.Objectives.ClearObjectiveDecision();
			_status = "Waiting for floor object evidence...";
			if (now - _lastObjectEvidenceUnavailableAt >= TimeSpan.FromSeconds(2))
			{
				_lastObjectEvidenceUnavailableAt = now;
				RecordReplayEvent("floor-object-evidence-unavailable", new
				{
					floor = runtime.Floor,
					floorGeneration = runtime.Generation,
					version = snapshot?.Version ?? 0,
					refreshCount = runtime.ObjectEvidence.RefreshCount,
					fullScanCount = runtime.ObjectEvidence.FullScanCount
				});
			}
			return false;
		}

		private unsafe FloorEvidenceAcquisitionMode ConsumeFloorEvidenceAcquisitionMode(InstanceContentDeepDungeon* dd)
		{
			if (_ctx?.ControlledPtSurvey != null)
				return FloorEvidenceAcquisitionMode.ControlledReusableSaveSurvey;

			if (!_controlledReusableSaveSurveyArmed)
				return FloorEvidenceAcquisitionMode.NaturalGameplay;

			_controlledReusableSaveSurveyArmed = false;
			if (dd->DeepDungeonId == DungeonCatalog.PilgrimsTraverse.DungeonId && dd->Floor < 30)
				return FloorEvidenceAcquisitionMode.ControlledReusableSaveSurvey;

			Service.Log.Error(
				$"[FloorEvidenceJournal] Rejected controlled reusable-save survey tag for dungeon={dd->DeepDungeonId}, floor={dd->Floor}; required Pilgrim's Traverse floor <30.");
			return FloorEvidenceAcquisitionMode.NaturalGameplay;
		}

		private unsafe bool TryBuildFloorRuntime(InstanceContentDeepDungeon* dd)
		{
			var readyAtUtc = DateTime.UtcNow;
			bool isBossFloor = DeepDungeonHelper.IsBossFloor(dd->DeepDungeonId, dd->Floor);
			NormalFloorGraphSnapshot? normalGraph = null;
			var runtimeKind = isBossFloor ? FloorRuntimeKind.Boss : FloorRuntimeKind.Normal;
			bool controlledInheritedFloor =
				_ctx?.ControlledPtSurvey != null &&
				!isBossFloor &&
				dd->Floor > ControlledPtSurveyPolicy.FirstFloor &&
				dd->Floor <= ControlledPtSurveyPolicy.LastResearchFloor;
			var controlledNativeGate = controlledInheritedFloor
				? ControlledPtSurveyPolicy.DecideInheritedNativeGate(
					_nativeIntuitionSampleAvailable,
					_nativeIntuitionActive)
				: ControlledPtInheritedNativeGateAction.ProceedInherited;
			if (controlledNativeGate == ControlledPtInheritedNativeGateAction.WaitForNativeState)
			{
				_status = "Controlled PT: waiting for authoritative inherited Intuition state";
				return false;
			}

			if (!isBossFloor && !MapPosGeneration.EnsureCentersAvailable(dd))
			{
				_status = "Waiting for room centers...";
				return false;
			}

			if (!isBossFloor && !TryBuildNormalFloorGraph(dd, out normalGraph))
			{
				_status = "Waiting for room graph...";
				_navHelper?.Cancel();
				if (_lastGraphPendingFloor != dd->Floor || _lastGraphPendingDungeonId != dd->DeepDungeonId)
				{
					_lastGraphPendingFloor = dd->Floor;
					_lastGraphPendingDungeonId = dd->DeepDungeonId;
					RecordReplayEvent("normal-floor-graph-pending", new
					{
						floor = dd->Floor,
						dungeonId = dd->DeepDungeonId,
						status = _status
					});
				}
				return false;
			}

			long floorGeneration = ++_nextFloorGeneration;
			_floorRuntime = new FloorExplorationController(
				floorGeneration,
				dd->DeepDungeonId,
				dd->Floor,
				runtimeKind,
				readyAtUtc,
				normalGraph,
				isBossFloor ? null : _nativeIntuitionActive,
				_ctx?.DetailedMap ??
					throw new InvalidOperationException(
						"Floor runtime requires the run-scoped detailed-map policy."),
				_runTelemetryObserver == null
					? null
					: new RunFloorTelemetryTrace(
						readyAtUtc,
						Service.LocalPlayer?.ClassJob.RowId ?? 0,
						dd->DeepDungeonId,
						((dd->Floor - 1) / 10) * 10 + 1,
						dd->Floor,
						floorGeneration,
						_ctx?.ControlledPtSurvey != null,
						!isBossFloor),
                new FloorItemExecutor(_ctx!, _pomanderManager, _chatWatchers, () => _phase,
                    (checkpoint, force) => RecordNativeIntuitionState(checkpoint, force), RecordReplayEvent),
                _ctx!, _navHelper!, _navDriver!, _chaseHelper, _pomanderManager, _chatWatchers,
                _runTelemetryObserver, _passageDestination, () => _phase, value => _phase = value,
                () => _status, value => _status = value, () => _nativeIntuitionActive,
                CancelActiveMovement, RecordReplayEvent,
                (checkpoint, force) => RecordNativeIntuitionState(checkpoint, force),
                RecordHoardEvidenceWait, EndHoardEvidenceWait, SnapshotRunOptions,
                () => ++_nextRoomSearchRequestId, ReadMobForbiddenZoneCount, ReadBossMovementDiagnostics,
                RecordPassageExitDelayedByCombat, RecordChaseTargetEvent,
                RecordChaseAcquisitionFailure, RecordPassageNavigationEvent,
                () => _lastChaseAcquisitionFailureKey = string.Empty, RecordNativeIntuitionState);
            _floorRuntime.AttachBoss(new FloorBossController(dd->Floor, _ctx!, _navHelper!,
                _pomanderManager, _floorRuntime.Items, _terminalRooms, _pt30DivineFavorFlashHelper,
                _pt50ChaseOutputGuard, (operation, objective) => _floorRuntime.RequireMovementPermission(operation, objective),
                value => _status = value, RecordReplayEvent, RecordBossCombatSnapshot));
            _floorRuntime.AttachSurvey(new FloorSurveyController(
                _floorRuntime.Generation, _floorRuntime.DungeonId, _floorRuntime.Floor, !isBossFloor,
                _floorRuntime.ReadyAtUtc, normalGraph, _floorRuntime.ObjectEvidence, _floorRuntime.Items,
                _ctx!, _floorRuntime.Executor, _pomanderManager, _chatWatchers, _navDriver, _floorEvidenceJournal,
                () => _phase, () => _nativeIntuitionActive, () => _status, value => _status = value,
                CancelActiveMovement, RecordReplayEvent, _floorRuntime.RequestPlanRefresh, _floorRuntime.HandleNoHoardEvidenceInvalidated,
                outcome => EndHoardEvidenceWait(outcome), _floorRuntime.NavigateToRoom));
            _floorRuntime.Survey.InitializeNaturalInventory();
			if (runtimeKind == FloorRuntimeKind.Normal)
			{
				try
				{
					var acquisitionMode = ConsumeFloorEvidenceAcquisitionMode(dd);
					var roomBindings = FloorEvidenceSession.BuildRoomBindings(dd, normalGraph!.ReachableRooms);
					_floorRuntime.Survey.OpenEvidenceSession(new FloorEvidenceSession(
						FsdEngineIdentity.InformationalVersion,
						dd->DeepDungeonId,
						dd->Floor,
						Service.ClientState.TerritoryType,
						dd->ActiveLayoutIndex,
						acquisitionMode,
						roomBindings));
					if (_ctx?.ControlledPtSurvey is { } controlled)
					{
						_floorRuntime.Survey.EvidenceSession!.ConfigureControlledSurvey(
							ControlledPtSurveyPolicy.IsResearchFloor(dd->Floor)
								? ControlledSurveyFloorRole.SelectedTarget
								: ControlledSurveyFloorRole.Transit,
							controlled.ResearchFloors);
					}
				}
				catch (Exception ex)
				{
					Service.Log.Error($"[FloorEvidenceJournal] Failed to open floor session for dungeon={dd->DeepDungeonId}, floor={dd->Floor}: {ex}");
				}
			}
			_lastObjectEvidenceTelemetryAt = DateTime.MinValue;
			_lastObjectEvidenceUnavailableAt = DateTime.MinValue;
			_phase = isBossFloor ? FloorPhase.BossFloor : FloorPhase.FloorSetup;
			_floorRuntime?.ResetTaskRunner();
			_navDriver?.Cancel();
			_chaseHelper.Reset();
			_floorRuntime?.ResetPatrolPlan();
			_floorRuntime?.ResetPermissionBlocks();
			_ctx?.ClearPreferredAggroTarget();
			PlanningState.ObserveHoardCount(dd->HoardCount);
			_lastGraphPendingFloor = 255;
			_lastGraphPendingDungeonId = 0;
			_lastPassageExitDelayEventAt = DateTime.MinValue;
			_lastChaseAcquisitionFailureKey = string.Empty;
			_floorRuntime?.ResetEngagedTargetProgress();
			bool controlledFirstFloor =
				_ctx?.ControlledPtSurvey != null &&
				!isBossFloor &&
				dd->Floor == ControlledPtSurveyPolicy.FirstFloor;
			_floorRuntime!.Survey.ConfigureCurrentIntuitionUse(controlledFirstFloor ||
                controlledNativeGate == ControlledPtInheritedNativeGateAction.ReactivateWithCurrentUse);
			_chatWatchers?.BeginReadyFloor(
				controlledFirstFloor
					? false
					: !isBossFloor && _nativeIntuitionActive);
			if (!controlledFirstFloor && !isBossFloor && _nativeIntuitionActive)
			{
				_floorRuntime.Survey.BeginInheritedIntuition();
			}
			_pt30DivineFavorFlashHelper?.Reset();
			_pt50ChaseOutputGuard?.Reset();
			_status = isBossFloor ? "Boss floor" : $"Floor {_floorRuntime.Floor} - initializing";
			RecordNativeIntuitionState("stable-floor-session-created", force: true, nativeStateAvailable: true, nativeIntuitionActive: _nativeIntuitionActive);
			Service.Log.Info($"[FloorPhase] Floor changed to {_floorRuntime.Floor} (generation {_floorRuntime.Generation})");
			RecordReplayEvent("floor-changed", new
			{
				floor = _floorRuntime.Floor,
				floorGeneration = _floorRuntime.Generation,
				sessionKind = runtimeKind.ToString(),
				hoardCount = dd->HoardCount,
				phase = _phase.ToString(),
				status = _status
			});

			if (isBossFloor)
			{
				RecordReplayEvent("boss-floor-session-built", new
				{
					floor = _floorRuntime.Floor,
					floorGeneration = _floorRuntime.Generation,
					dungeonId = dd->DeepDungeonId,
					phase = _phase.ToString(),
					status = _status
				});
			}
			else if (normalGraph != null)
			{
				DeepDungeonFloorsetTracker.TryGetCurrentFloorsetState(
					_floorRuntime.Floor,
					out FloorsetHoardDistributionState floorsetState);
				RecordReplayEvent("normal-floor-graph-built", new
				{
					floor = _floorRuntime.Floor,
					floorGeneration = _floorRuntime.Generation,
					dungeonId = dd->DeepDungeonId,
					homeRoomIndex = normalGraph.HomeRoomIndex,
					initialPlayerRoomIndex = normalGraph.InitialPlayerRoomIndex,
					reachableRoomCount = normalGraph.ReachableRooms.Count,
					floorsetHoardCount = floorsetState.TotalHoardCount,
					floorsetSegmentMask = floorsetState.SatisfiedSegmentMask,
					floorsetHoardOpportunity =
						FloorsetHoardDistributionPolicy.Decide(
							floorsetState,
							_floorRuntime.Floor).ToString()
				});
			}

			return true;
		}

		private unsafe bool TryBuildNormalFloorGraph(InstanceContentDeepDungeon* dd, out NormalFloorGraphSnapshot? graph)
		{
			graph = null;
			int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
			int homeRoom = RoomGraph.GetHomeRoomIndex(dd);

			if (playerRoom < 0 || playerRoom >= RoomGraph.MaxRooms)
				return false;

			if (homeRoom < 0 || homeRoom >= RoomGraph.MaxRooms)
				return false;

			var reachableRooms = RoomGraph.BuildReachableRoomOrder(dd, playerRoom);
			if (reachableRooms.Count == 0 || !reachableRooms.Contains(playerRoom))
				return false;

			graph = new NormalFloorGraphSnapshot(
				homeRoom,
				playerRoom,
				reachableRooms.ToArray(),
				RoomGraph.BuildDistanceCache(dd, reachableRooms));
			return true;
		}

		private void DestroyFloorRuntime(byte observedFloor, string reason)
		{
			var runtime = _floorRuntime;
			if (runtime == null)
				return;

			var previousFloor = runtime.Floor;
			var previousRuntimeKind = runtime.Kind.ToString();
			var previousGeneration = runtime.Generation;
			if (runtime.Survey.EvidenceSession != null)
			{
				try
				{
					_floorEvidenceJournal?.Enqueue(runtime.Survey.EvidenceSession.Finalize(reason));
				}
				catch (Exception ex)
				{
					Service.Log.Error($"[FloorEvidenceJournal] Failed to finalize floor {runtime.Floor} generation {runtime.Generation}: {ex}");
				}
			}
			EndHoardEvidenceWait(reason);
			_floorRuntime?.PreemptActiveObjectiveExecutions($"FloorRuntimeDestroyed:{reason}");
			_floorRuntime?.EndActiveWaypointTelemetry(
				RunWaypointTerminalOutcome.Aborted,
				$"FloorRuntimeDestroyed:{reason}");
			CancelActiveMovement();
			ObserveFloorTerminalTelemetry(runtime, reason);
			ObserveFloorTelemetryBoundary(runtime, reason);
			_chatWatchers?.CancelExpectedIntuitionResult(runtime.Survey.InheritedIntuitionAttemptId);
			if (runtime.ItemUse.Pending is { } pendingFloorItemUse)
				runtime.Items.CancelPendingIntuitionAttempt(pendingFloorItemUse);
			runtime.Dispose();
			_floorRuntime = null;
			_phase = FloorPhase.FloorSetup;
			_chaseHelper.Reset();
			_floorRuntime?.ResetPatrolPlan();
			_floorRuntime?.ResetPermissionBlocks();
			_ctx?.ClearPreferredAggroTarget();
			_lastGraphPendingFloor = 255;
			_lastGraphPendingDungeonId = 0;
			_pt30DivineFavorFlashHelper?.Reset();
			_pt50ChaseOutputGuard?.Reset();

			RecordReplayEvent("floor-session-destroyed", new
			{
				previousFloor,
				floorGeneration = previousGeneration,
				observedFloor,
				reason,
				sessionKind = previousRuntimeKind,
				phase = _phase.ToString(),
				status = _status
			});
		}

		private unsafe bool IsLoadedFloorReady(InstanceContentDeepDungeon* dd)
		{
			if (dd == null || dd->Floor == 0 || Service.LocalPlayer == null)
				return false;

			if (_ctx?.Duty.IsPlayerPositionStable() == false)
				return false;

			return true;
		}

		private string BuildRecorderSessionName()
		{
			string mode = _ctx?.Duty.IsInDuty == true ? "DeepDungeon" : "DeepDungeonIdle";
			return $"{mode}-ddrun";
		}

		private void OnChatWatchersStateChanged(ChatWatchers.StateChangedInfo info)
		{
			if (info.Reason is "LogMessage7256" or "LogMessage11251Mazeroot")
				_floorRuntime?.ObjectEvidence.Invalidate();

			if (info.EvidenceAccepted && info.EvidenceTargetFloor != 0)
			{
				var runtime = _floorRuntime;
				if (runtime == null || runtime.IsDisposed || runtime.Floor != info.EvidenceTargetFloor)
				{
					RecordReplayEvent("chat-watchers-state-rejected", new
					{
						info.Reason,
						info.EvidenceAttemptId,
						info.EvidenceTargetFloor,
						activeFloor = runtime?.Floor ?? 0,
						activeFloorGeneration = runtime?.Generation ?? 0,
						reason = "stale-floor-runtime"
					});
					return;
				}
			}

			if (info.EvidenceAccepted &&
			    (info.Reason is "LogMessage7272" or "LogMessage7273") &&
			    _floorRuntime?.ItemUse.Pending is
			    {
				    Key:
				    {
					    Kind: FloorItemUseKind.Pomander,
					    ItemId: FloorInitPlanner.IntuitionPomanderSlotIndex
				    }
			    } pendingIntuitionUse &&
			    pendingIntuitionUse.IntuitionAttemptId == info.EvidenceAttemptId)
			{
				pendingIntuitionUse.ObserveAuthoritativeConfirmation();
			}

			_floorRuntime?.Survey.ObserveInheritedMessage(info);

			RecordReplayEvent("chat-watchers-state", info);
			uint semanticMessageId = info.Reason switch
			{
				"LogMessage7272" or "LogMessage7272Rejected" => 7272,
				"LogMessage7273" or "LogMessage7273Rejected" => 7273,
				"LogMessage7274" or "LogMessage7274Rejected" => 7274,
				_ => 0
			};
			if (semanticMessageId != 0)
				_floorRuntime?.Survey.EvidenceSession?.ObserveSemanticMessage(semanticMessageId, info.EvidenceAccepted);

			switch (info.Reason)
			{
				case "GoldChestOvercapObserved":
					_floorRuntime?.HandleGoldChestOvercapObserved(info.GoldChestOvercapSlotIndex);
					break;
				case "SilverChestOvercapObserved":
					_floorRuntime?.HandleSilverChestOvercapObserved(info.SilverChestOvercapDemicloneRowId);
					break;
				case "LogMessage7272":
					PendingIntuition.TryMarkResolved(info.EvidenceAttemptId);
					if (info.EvidenceAccepted)
						_floorRuntime?.ObjectEvidence.Invalidate();
					RequestPlanRefresh(info.Reason);
					break;
				case "LogMessage7273":
					PendingIntuition.TryMarkResolved(info.EvidenceAttemptId);
					if (info.EvidenceExpectationKind != IntuitionEvidenceExpectationKind.InheritedFloorResult)
						HandleNoHoardEvidenceInvalidated(info.Reason);
					RequestPlanRefresh(info.Reason);
					break;
				case "LogMessage7274":
					if (info.EvidenceAccepted)
						_floorRuntime?.ObjectEvidence.Invalidate();
					RequestPlanRefresh(info.Reason);
					break;
			}

			if (info.Reason is "LogMessage7272" or "LogMessage7273" or "LogMessage7274")
			{
				RecordNativeIntuitionState($"chat-{info.Reason}", force: true);
				EndHoardEvidenceWait(info.Reason);
			}
		}

		private void RecordHoardEvidenceWait(string eventType, int? remainingWaitMilliseconds = null)
		{
			string evidenceState = _executor?.HoardEvidenceState.ToString() ?? string.Empty;
			if (string.IsNullOrEmpty(_activeHoardEvidenceWaitEventType))
			{
				_activeHoardEvidenceWaitEventType = eventType;
				_activeHoardEvidenceWaitState = evidenceState;
				_activeHoardEvidenceWaitStartedAt = DateTime.UtcNow;
				RecordReplayEvent(eventType, new
				{
					waitStage = "start",
					floor = _floorRuntime?.Floor ?? 0,
					hoardEvidenceState = evidenceState,
					remainingWaitMilliseconds,
					status = _status
				});
				return;
			}

			if (string.Equals(_activeHoardEvidenceWaitEventType, eventType, StringComparison.Ordinal) &&
			    string.Equals(_activeHoardEvidenceWaitState, evidenceState, StringComparison.Ordinal))
			{
				return;
			}

			RecordReplayEvent($"{_activeHoardEvidenceWaitEventType}-changed", new
			{
				waitStage = "material-change",
				floor = _floorRuntime?.Floor ?? 0,
				previousEventType = _activeHoardEvidenceWaitEventType,
				nextEventType = eventType,
				previousHoardEvidenceState = _activeHoardEvidenceWaitState,
				nextHoardEvidenceState = evidenceState,
				remainingWaitMilliseconds,
				status = _status
			});
			_activeHoardEvidenceWaitEventType = eventType;
			_activeHoardEvidenceWaitState = evidenceState;
		}

		private void EndHoardEvidenceWait(string outcome, string? expectedEventType = null)
		{
			if (string.IsNullOrEmpty(_activeHoardEvidenceWaitEventType))
				return;
			if (expectedEventType != null &&
			    !string.Equals(_activeHoardEvidenceWaitEventType, expectedEventType, StringComparison.Ordinal))
			{
				return;
			}

			var now = DateTime.UtcNow;
			RecordReplayEvent($"{_activeHoardEvidenceWaitEventType}-ended", new
			{
				waitStage = "end",
				floor = _floorRuntime?.Floor ?? 0,
				hoardEvidenceState = _activeHoardEvidenceWaitState,
				outcome,
				durationMilliseconds = _activeHoardEvidenceWaitStartedAt == DateTime.MinValue
					? 0
					: (int)Math.Max(0, (now - _activeHoardEvidenceWaitStartedAt).TotalMilliseconds),
				status = _status
			});
			_activeHoardEvidenceWaitEventType = string.Empty;
			_activeHoardEvidenceWaitState = string.Empty;
			_activeHoardEvidenceWaitStartedAt = DateTime.MinValue;
		}

		private bool TryGetNativeIntuitionState(out bool nativeIntuitionActive)
		{
			var now = DateTime.UtcNow;
			if (_lastNativeIntuitionReadAvailable.HasValue && now < _nextNativeIntuitionPollAt)
			{
				nativeIntuitionActive = _nativeIntuitionActive;
				return _nativeIntuitionSampleAvailable;
			}

			_nextNativeIntuitionPollAt = now.Add(NativeIntuitionPollInterval);
			_nativeIntuitionSampleAvailable = _pomanderManager.TryIsActive(
				FloorInitPlanner.IntuitionPomanderSlotIndex,
				out nativeIntuitionActive);
			if (_nativeIntuitionSampleAvailable)
				_nativeIntuitionActive = nativeIntuitionActive;
			RecordNativeIntuitionState("material-change", force: false, _nativeIntuitionSampleAvailable, nativeIntuitionActive);
			return _nativeIntuitionSampleAvailable;
		}

		private void RecordNativeIntuitionState(string checkpoint, bool force)
		{
			bool nativeStateAvailable = _pomanderManager.TryIsActive(
				FloorInitPlanner.IntuitionPomanderSlotIndex,
				out bool nativeIntuitionActive);
			RecordNativeIntuitionState(checkpoint, force, nativeStateAvailable, nativeIntuitionActive);
		}

		private void RecordNativeIntuitionState(
			string checkpoint,
			bool force,
			bool nativeStateAvailable,
			bool nativeIntuitionActive)
		{
			if (!nativeStateAvailable)
			{
				bool availabilityChanged = _lastNativeIntuitionReadAvailable != false;
				if (!force && !availabilityChanged)
					return;

				_lastNativeIntuitionReadAvailable = false;
				RecordReplayEvent("intuition-native-state-unavailable", new
				{
					checkpoint,
					availabilityChanged,
					floor = _floorRuntime?.Floor ?? 0,
					floorGeneration = _floorRuntime?.Generation ?? 0,
					dungeonId = _floorRuntime?.DungeonId ?? _ctx?.Duty.DungeonId ?? 0,
					phase = _phase.ToString(),
					status = _status
				});
				return;
			}

			bool availabilityChangedToAvailable = _lastNativeIntuitionReadAvailable != true;
			_lastNativeIntuitionReadAvailable = true;
			var state = new NativeIntuitionState(
				nativeIntuitionActive,
				_pomanderManager.GetCount(FloorInitPlanner.IntuitionPomanderSlotIndex),
				_pomanderManager.IsUsable(FloorInitPlanner.IntuitionPomanderSlotIndex));
			bool materialChanged = availabilityChangedToAvailable || !_lastNativeIntuitionState.HasValue || _lastNativeIntuitionState.Value != state;
			if (!force && !materialChanged)
				return;

			_lastNativeIntuitionState = state;
			RecordReplayEvent("intuition-native-state", new
			{
				checkpoint,
				materialChanged,
				floor = _floorRuntime?.Floor ?? 0,
				floorGeneration = _floorRuntime?.Generation ?? 0,
				dungeonId = _floorRuntime?.DungeonId ?? _ctx?.Duty.DungeonId ?? 0,
				phase = _phase.ToString(),
				nativeIsActive = state.IsActive,
				nativeCount = state.Count,
				nativeIsUsable = state.IsUsable,
				chatIntuitionActive = _chatWatchers?.IntuitionActive ?? false,
				chatSaysHoard = _chatWatchers?.ChatSaysHoard ?? false,
				chatSaysNoHoard = _chatWatchers?.ChatSaysNoHoard ?? false,
				usedIntuitionThisFloor = _chatWatchers?.UsedIntuitionThisFloor ?? false,
				status = _status
			});
		}

		private void ObserveDutyTransitionState(byte currentFloor, bool isTransitioning)
		{
			if (isTransitioning == _wasTransitioning)
				return;

			_wasTransitioning = isTransitioning;
			if (isTransitioning)
			{
				EndHoardEvidenceWait("floor-transition-started");
				RecordNativeIntuitionState("floor-transition-started", force: true);
			}
			RecordReplayEvent(isTransitioning ? "floor-transition-started" : "floor-transition-ended", new
			{
				floor = currentFloor,
				phase = _phase.ToString(),
				status = _status
			});
		}

		internal void RecordReplayEvent(string eventType, object data)
		{
			if (_runRecorder == null)
				return;

			_runRecorder.Record(eventType, data);
		}

		private void DisposeChatWatchers()
		{
			if (_chatWatchers == null)
				return;

			_chatWatchers.StateChanged -= OnChatWatchersStateChanged;
			try { _chatWatchers.Dispose(); } catch { }
			_chatWatchers = null;
		}

		private unsafe bool TryMarkPlayerDeathFatal(InstanceContentDeepDungeon* dd)
		{
			var player = Service.LocalPlayer;
			if (player == null || !player.IsDead)
				return false;

            if (_ctx?.FarmingPlan?.Mode == FarmingMode.DeepProgression)
            {
                _ctx.MarkDutyFailed();
                if (_ctx.TryHoldFailedAttempt("角色死亡"))
                {
                    CancelActiveMovement();
                    return true;
                }
                _ctx.RunOptions.Update(o => o.LeaveMode = LeaveMode.Immediate);
                CancelActiveMovement();
                return true;
            }
			_status = "Deep Dungeon run failed - player died";
			if (_ctx != null && !_ctx.StatusIsError)
			{
				_ctx.StatusLine = "Deep Dungeon run failed: player died; manual leave required.";
				_ctx.StatusIsError = true;
				RecordReplayEvent("floor-fatal-player-dead", new
				{
					floor = dd->Floor,
					dungeonId = dd->DeepDungeonId,
					phase = _phase.ToString(),
					status = _status
				});
			}
			CancelActiveMovement();
			_chaseHelper.Reset();
			return true;
		}

		private RunOptions SnapshotRunOptions()
		{
			var source = _ctx?.RunOptions?.Current ?? new RunOptions();
			if (_ctx?.ControlledPtSurvey != null)
			{
				return new RunOptions
				{
					OpenGold = false,
					OpenSilver = false,
					OpenBronze = false,
					BandedEnabled = false,
					LeaveMode = source.LeaveMode,
					LeaveAfterMinutes = 0,
					RequireValidatedAbandonPrompt = true
				};
			}
			return new RunOptions
			{
				OpenGold = source.OpenGold,
				OpenSilver = source.OpenSilver,
				OpenBronze = source.OpenBronze,
				BandedEnabled = source.BandedEnabled,
                HarvestChestsRequired = source.HarvestChestsRequired,
                DiscoveryOnly = source.DiscoveryOnly,
				LeaveMode = source.LeaveMode,
				LeaveAfterMinutes = source.LeaveAfterMinutes,
				RequireValidatedAbandonPrompt = source.RequireValidatedAbandonPrompt
			};
		}

		private unsafe void RecordPassageExitDelayedByCombat(InstanceContentDeepDungeon* dd)
		{
			var now = DateTime.UtcNow;
			if ((now - _lastPassageExitDelayEventAt).TotalMilliseconds < 1000)
				return;

			_lastPassageExitDelayEventAt = now;
			RecordReplayEvent("passage-open-exit-delayed", new
			{
				floor = dd->Floor,
				reason = "in-combat",
				phase = _phase.ToString(),
				status = _status
			});
		}

		private void RecordChaseTargetEvent(string eventType, EnemyChaseTarget target)
		{
			var now = DateTime.UtcNow;
			string key = $"{eventType}:{target.GameObjectId}:{target.Reason}";
			if (string.Equals(key, _lastChaseTargetEventKey, StringComparison.Ordinal) &&
			    (now - _lastChaseTargetEventAt).TotalMilliseconds < 1000)
			{
				return;
			}

			_lastChaseTargetEventKey = key;
			var npc = CombatTargetingHelpers.GetBattleCharaByGameObjectId(target.GameObjectId);
			_lastChaseTargetEventAt = now;
			RecordReplayEvent(eventType, new
			{
				phase = _phase.ToString(),
				targetId = target.GameObjectId,
				nameId = npc?.NameId,
				baseId = npc?.BaseId,
				hp = npc?.CurrentHp, maxHp = npc?.MaxHp,
				livePosition = new { target.LivePosition.X, target.LivePosition.Y, target.LivePosition.Z },
				inCombat = Service.Condition[ConditionFlag.InCombat],
				currentTargetId = Service.TargetManager.Target?.GameObjectId,
				attackWindowStartedUtc = _floorRuntime?.AttackWindowStartedAt,
				attackHoldActive = _floorRuntime?.IsAttackHolding(now),
				reason = target.Reason.ToString(),
				acquisitionPlayerRoom = target.AcquisitionPlayerRoomIndex,
				acquisitionTargetRoom = target.AcquisitionTargetRoomIndex,
				acquisitionGraphHops = target.AcquisitionGraphHops,
				x = target.Position.X,
				y = target.Position.Y,
				z = target.Position.Z
			});
		}

		private unsafe void RecordChaseAcquisitionFailure(
			InstanceContentDeepDungeon* dd,
			EnemyChaseAcquisitionFailure failure)
		{
			string key = $"{dd->Floor}:{_floorRuntime?.Generation ?? 0}:{failure}";
			if (string.Equals(key, _lastChaseAcquisitionFailureKey, StringComparison.Ordinal))
				return;

			_lastChaseAcquisitionFailureKey = key;
			RecordReplayEvent("clearing-target-acquisition-failed", new
			{
				floor = dd->Floor,
				floorGeneration = _floorRuntime?.Generation ?? 0,
				phase = _phase.ToString(),
				reason = failure.ToString()
			});
		}

		private void RecordPassageNavigationEvent(string eventType, string result, bool usedActor, int passageRoomIndex, int playerRoom)
		{
			var now = DateTime.UtcNow;
			string key = $"{eventType}:{result}:{usedActor}:{passageRoomIndex}:{playerRoom}";
			if (string.Equals(key, _lastPassageNavigationEventKey, StringComparison.Ordinal) &&
			    (now - _lastPassageNavigationEventAt).TotalMilliseconds < 1000)
			{
				return;
			}

			_lastPassageNavigationEventKey = key;
			_lastPassageNavigationEventAt = now;
			RecordReplayEvent(eventType, new
			{
				phase = _phase.ToString(),
				result,
				usedActor,
				passageRoomIndex,
				playerRoom,
				playerPosition = Service.LocalPlayer is { } player ? new { player.Position.X, player.Position.Y, player.Position.Z } : null,
				actorPosition = usedActor ? new { _passageDestination.ActorPosition.X, _passageDestination.ActorPosition.Y, _passageDestination.ActorPosition.Z } : null,
				walkingPosition = usedActor ? new { _passageDestination.WalkingPosition.X, _passageDestination.WalkingPosition.Y, _passageDestination.WalkingPosition.Z } : null,
				projected = usedActor && _passageDestination.Projected,
				pathRunning = moveHelper.VNav.Path.IsRunning(),
				pathfindPending = moveHelper.VNav.SimpleMove.PathfindInProgress()
			});
		}

        public void TickInteractionChannel(IFramework framework) => _floorRuntime?.TickInteractionChannel(framework);
        private void RequestPlanRefresh(string reason) => _floorRuntime?.RequestPlanRefresh(reason);
        private bool HandleNoHoardEvidenceInvalidated(string reason) => _floorRuntime?.HandleNoHoardEvidenceInvalidated(reason) == true;
	}
}
