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
		private readonly IFloorEvidenceObserver? _floorEvidenceObserver;
		private readonly IRunTelemetryObserver? _runTelemetryObserver;
		private readonly NativeDeepDungeonLogMessageSource _logMessageSource;
		private NavigationHelper? _navHelper;
		private NavigationDriver? _navDriver;
		private WaypointTaskRunner? _taskRunner => _floorRuntime?.ActiveExecution?.TaskRunner;
		private AutoPilotExecutor? _executor => _floorRuntime?.Executor;
		private ChatWatchers? _chatWatchers;
		private DeepDungeonRunRecorder? _runRecorder;
		private FloorEvidenceJournal? _floorEvidenceJournal;
		private readonly PomanderManager _pomanderManager = new();
		private readonly EnemyChaseHelper _chaseHelper = new();

		private FloorPhase _phase = FloorPhase.FloorSetup;
		private FloorRuntime? _floorRuntime;
		private static readonly TimeSpan GeneralTickInterval = TimeSpan.FromMilliseconds(500);
		private const int CurrentIntuitionResolutionWindowMilliseconds = 1500;
		private const float BossNavigationArrivalTolerance = 3f;
		private long _nextFloorGeneration;
		private long _nextRoomSearchRequestId;
		private string _status = "Idle";
		private DateTime _nextPomanderUseAt = DateTime.MinValue;
		private bool _pomanderDispatchedThisUpdate;
		private Pt30DivineFavorFlashHelper? _pt30DivineFavorFlashHelper;
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
		private string _blockedMovementOperation = string.Empty;
		private string _blockedTransitionOperation = string.Empty;
		private bool _controlledReusableSaveSurveyArmed;
		private static readonly TimeSpan ClearingEngageNoProgressLimit = TimeSpan.FromSeconds(15);
		private static readonly TimeSpan ClearingEngageRecenterFallbackLimit = TimeSpan.FromSeconds(10);
		private static readonly TimeSpan CombatTargetSuppressionDuration = TimeSpan.FromSeconds(20);
		private static readonly TimeSpan NativeIntuitionPollInterval = TimeSpan.FromMilliseconds(250);
		private readonly record struct NativeIntuitionState(bool IsActive, int Count, bool IsUsable);
		private readonly record struct ControlledTrapWitnessKey(
			ulong GameObjectId,
			uint BaseId,
			int X,
			int Y,
			int Z)
		{
			public static ControlledTrapWitnessKey From(in FloorObjectEvidence evidence)
			{
				const float normalization = 10f;
				return new ControlledTrapWitnessKey(
					evidence.GameObjectId,
					evidence.BaseId,
					(int)MathF.Round(evidence.Position.X * normalization),
					(int)MathF.Round(evidence.Position.Y * normalization),
					(int)MathF.Round(evidence.Position.Z * normalization));
			}
		}
		private FloorPlanningState PlanningState =>
			_floorRuntime?.PlanningState ?? throw new InvalidOperationException("No active floor planning state.");
		private PendingIntuitionState PendingIntuition =>
			_floorRuntime?.PendingIntuition ?? throw new InvalidOperationException("No active floor Intuition attempt state.");
		private bool BossNavigationResolved
		{
			get => _floorRuntime?.BossNavigationResolved == true;
			set
			{
				if (_floorRuntime != null)
					_floorRuntime.BossNavigationResolved = value;
			}
		}

		public FloorPhase CurrentPhase => _phase;
		public string Status => _status;
		public string? RunRecorderPath => _runRecorder?.FilePath;
		public ObjectiveArbiterDecision CurrentObjectiveDecision => _floorRuntime?.ObjectiveDecision ?? default;
		public bool AllowsCombatChannel =>
			_floorRuntime is { IsDisposed: false } &&
			(Service.Condition[ConditionFlag.InCombat] ||
			 (_floorRuntime.HasObjectiveDecision &&
			  _floorRuntime.ObjectiveDecision.Channels.Combat != CommandChannelPermission.Blocked));
		public bool AllowsMovementChannel =>
			_floorRuntime?.HasObjectiveDecision == true &&
			_floorRuntime.ObjectiveDecision.Channels.Movement != CommandChannelPermission.Blocked;
		public bool AllowsTransitionChannel =>
			_floorRuntime?.HasObjectiveDecision == true &&
			_floorRuntime.ObjectiveDecision.Channels.Transition == CommandChannelPermission.PrimaryObjective;
		public bool AllowsChestSidecarInteraction =>
			_floorRuntime?.HasObjectiveDecision == true &&
			_floorRuntime.ObjectiveDecision.Channels.Interaction != CommandChannelPermission.Blocked;

		public FloorPhaseController(
			NativeDeepDungeonLogMessageSource logMessageSource,
			IFloorEvidenceObserver? floorEvidenceObserver = null,
			IRunTelemetryObserver? runTelemetryObserver = null)
		{
			_logMessageSource = logMessageSource ?? throw new ArgumentNullException(nameof(logMessageSource));
			_floorEvidenceObserver = floorEvidenceObserver;
			_runTelemetryObserver = runTelemetryObserver;
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
			_ctx.ClearPreferredAggroTarget();
			_navHelper = new NavigationHelper(_ctx.Navigator);
			_navDriver = new NavigationDriver(_navHelper);
			DisposeChatWatchers();
			_chatWatchers = new ChatWatchers(_logMessageSource);
			_chatWatchers.StateChanged += OnChatWatchersStateChanged;
			CloseRunRecording("controller-reinitialized");
			_chaseHelper.Reset();
			_phase = FloorPhase.FloorSetup;
			_nextPomanderUseAt = DateTime.MinValue;
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
			ResetPermissionBlocks();
			ResetEngagedTargetProgress();
			_pt30DivineFavorFlashHelper?.Dispose();
			_pt30DivineFavorFlashHelper = new Pt30DivineFavorFlashHelper();
			ResetPatrolPlan();
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
				recorderPath = _runRecorder.FilePath
			});
			Service.Log.Info("[FloorPhase] Controller initialized");
			Service.Log.Info($"[FloorPhase] DD run recorder -> {_runRecorder.FilePath}");
		}

		public unsafe void Update(IFramework _)
		{
			_pomanderDispatchedThisUpdate = false;
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
				_floorRuntime?.ClearObjectiveDecision();
				return;
			}
			var dd = efw->GetInstanceContentDeepDungeon();
			if (dd == null)
			{
				_floorRuntime?.ClearObjectiveDecision();
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
				bool nativeIntuitionActive = false;
				bool nativeStateAvailable = !requiresIntuitionState ||
				                            TryGetNativeIntuitionState(out nativeIntuitionActive);
				var readyIntuition = ReadyFloorIntuitionPlanner.Decide(new ReadyFloorIntuitionSnapshot(
					nativeStateAvailable,
					requiresIntuitionState && nativeIntuitionActive));
				if (readyIntuition.Kind == ReadyFloorIntuitionDecisionKind.Wait)
				{
					_floorRuntime?.ClearObjectiveDecision();
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
				if (activeRuntime.Kind == FloorRuntimeKind.Normal && !RefreshFloorObjectEvidence(dd, activeRuntime))
					return;
				ResolveInheritedIntuition(activeRuntime);
				if (_ctx.ControlledPtSurvey?.LeaveRequested == true)
				{
					CancelActiveMovement();
					activeRuntime.ClearObjectiveDecision();
					_status = "Controlled PT capture complete; abandoning before further floor movement";
					return;
				}
				ObserveFloorRuntimeNativeIntuitionEdge(activeRuntime);

				if (_ctx?.ControlledPtSurvey == null &&
				    TryReconcileDelayedHoardEvidence(dd, activeRuntime))
				{
					RefreshObjectiveDecision(dd, activeRuntime);
					return;
				}

				RefreshObjectiveDecision(dd, activeRuntime);
				switch (_phase)
				{
					case FloorPhase.FloorSetup:
						UpdateFloorSetup(dd);
						break;
					case FloorPhase.FloorActive:
						UpdateFloorActive(dd);
						break;
					case FloorPhase.BossFloor:
						UpdateBossFloor(dd);
						break;
					case FloorPhase.Done:
						break;
				}

				RefreshObjectiveDecision(dd, activeRuntime);
			}
			catch (Exception ex)
			{
				_floorRuntime?.ClearObjectiveDecision();
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
			_floorEvidenceJournal?.Dispose();
			_floorEvidenceJournal = null;
		}

		public void CloseRunRecording(string reason, object? details = null)
		{
			if (_runRecorder == null)
				return;

			RecordReplayEvent("run-recorder-closing", new
			{
				reason,
				phase = _phase.ToString(),
				status = _status,
				floor = _floorRuntime?.Floor ?? 0,
				details
			});
			_runRecorder.Dispose();
			_runRecorder = null;
		}

		public void CancelActiveMovement()
		{
			EndActiveWaypointTelemetry(RunWaypointTerminalOutcome.Aborted, "MovementCanceled");
			bool navigationActive = _navHelper?.HasActiveTarget == true;
			_taskRunner?.Reset(cancelNavigation: false);
			_navDriver?.Reset();
			if (navigationActive)
				_navHelper?.Cancel();
			_activeWaypoint = null;
			_pt30DivineFavorFlashHelper?.Reset();
		}

		private bool RequireMovementPermission(
			string operation,
			FloorObjectiveKind? requiredObjective = null,
			bool primaryOwnsOperation = true)
		{
			var runtime = _floorRuntime;
			var decision = runtime?.ObjectiveDecision ?? default;
			bool allowed = runtime?.HasObjectiveDecision == true &&
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
				FloorObjectiveKind.DiscoverHoard;

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
			       (_floorRuntime?.ActiveExecution?.ObjectiveRecords.Count ?? 0) > 0;
		}

		private unsafe void UpdateFloorActive(InstanceContentDeepDungeon* dd)
		{
			if (_ctx?.ControlledPtSurvey != null &&
			    _floorRuntime is { ControlledPositiveMessagePendingIndicator: true } indicatorRuntime)
			{
				UpdateControlledIndicatorAcquisition(dd, indicatorRuntime);
				return;
			}

			if (_ctx?.ControlledPtSurvey != null &&
			    _floorRuntime is { ControlledIntuitionResolutionPending: true } &&
			    CurrentObjectiveDecision.PrimaryObjective == FloorObjectiveKind.EnterPassage)
			{
				CancelActiveMovement();
				_status = "Controlled PT: holding passage until Intuition resolution window completes";
				return;
			}

			if (_ctx?.ControlledPtSurvey != null &&
			    _floorRuntime is
			    {
				    ControlledOpportunityCompleted: false,
				    ControlledDispatchBarrierActive: true
			    } barrierRuntime)
			{
				EnsureControlledDispatchOutsidePassage(
					dd,
					barrierRuntime,
					barrierRequired: true,
					"controlled positive 敏慧 capture");
				_status = barrierRuntime.ControlledDispatchBarrierActive
					? "Controlled PT: relocating away from passage before capture"
					: "Controlled PT: passage dispatch barrier cleared";
				return;
			}

			if (_ctx?.ControlledPtSurvey != null &&
			    _floorRuntime is
			    {
				    ControlledOpportunityCompleted: false,
				    ControlledPositiveCapturePending: true
			    } controlledRuntime)
			{
				if (controlledRuntime.ControlledSightConfirmed)
					UpdateControlledCandidateCoverageMovement(dd, controlledRuntime);
				else
					CancelActiveMovement();
				return;
			}

			var objective = CurrentObjectiveDecision.PrimaryObjective;
			if (_floorRuntime is { } ordinaryRuntime &&
			    TryUseNaturalPassageAcceleration(
				    dd,
				    ordinaryRuntime,
				    objective))
			{
				return;
			}
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

		private unsafe void UpdateControlledIndicatorAcquisition(
			InstanceContentDeepDungeon* dd,
			FloorRuntime runtime)
		{
			var player = Service.LocalPlayer;
			var rooms = runtime.NormalGraph?.ReachableRooms;
			if (player == null || rooms == null)
			{
				_status = "Controlled PT: waiting for indicator acquisition geometry";
				return;
			}

			while (runtime.ControlledIndicatorRoomCursor < rooms.Count &&
			       (runtime.EvidenceSession?.HasVisitedRoom(rooms[runtime.ControlledIndicatorRoomCursor]) == true ||
			        MapPos.TryGetRoomCenter(
				        dd,
				        rooms[runtime.ControlledIndicatorRoomCursor],
				        out var coveredCenter) &&
			        Vector3.DistanceSquared(player.Position, coveredCenter) <= 1.5f * 1.5f))
			{
				runtime.ControlledIndicatorRoomCursor++;
			}

			if (runtime.ControlledIndicatorRoomCursor >= rooms.Count)
			{
				CompleteControlledJointSampleIncomplete(
					dd,
					runtime,
					$"Controlled PT floor {runtime.Floor} received 7272 but the exact indicator did not load after room-center coverage.");
				return;
			}

			int targetRoom = rooms[runtime.ControlledIndicatorRoomCursor];
			NavigateToRoom(dd, targetRoom, player);
			_status = $"Controlled PT: acquiring exact indicator via room {targetRoom}";
		}

		private bool EnsureObjectiveExecution(FloorObjectiveKind objective)
		{
			var runtime = _floorRuntime;
			if (runtime == null || runtime.IsDisposed || _navHelper == null)
				return false;
			if (runtime.ActiveExecution?.Objective == objective)
				return true;
			if (runtime.ActiveExecution != null &&
			    HasActiveSearchExecution() &&
			    ShouldContinuePlannedRouteForPassageActivation(objective))
			{
				runtime.ActiveExecution.Objective = objective;
				return true;
			}
			if (runtime.ActiveExecution != null)
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

			runtime.ReplaceObjectiveExecution(objective, _navHelper);
			return true;
		}

		private unsafe bool TryActivateVisibleBandedObjective(InstanceContentDeepDungeon* dd)
		{
			if (CurrentObjectiveDecision.PrimaryObjective != FloorObjectiveKind.OpenVisibleBandedChest ||
			    (_activeWaypoint ?? _executor?.CurrentWaypoint)?.Type == RoomObjectiveType.ChestBanded)
			{
				return false;
			}

			var runtime = _floorRuntime;
			var player = Service.LocalPlayer;
			if (runtime?.NormalGraph == null || player == null || runtime.ObjectEvidence.Current is not { } evidence)
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
				runtime.NormalGraph.ReachableRooms,
				-1);
			if (roomIndex < 0)
			{
				_status = "Waiting to resolve visible banded chest room...";
				return true;
			}

			HandleVisibleBandedDetection(dd, player, roomIndex, bandedPosition.Value);
			return true;
		}

		private bool RequireTransitionPermission(string operation, FloorObjectiveKind requiredObjective)
		{
			var runtime = _floorRuntime;
			var decision = runtime?.ObjectiveDecision ?? default;
			bool allowed = runtime?.HasObjectiveDecision == true &&
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
			var decision = _floorRuntime?.ObjectiveDecision ?? default;
			RecordReplayEvent("objective-permission-blocked", new
			{
				floor = _floorRuntime?.Floor ?? 0,
				floorGeneration = _floorRuntime?.Generation ?? 0,
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
			var decision = _floorRuntime?.ObjectiveDecision ?? default;
			RecordReplayEvent("objective-permission-restored", new
			{
				floor = _floorRuntime?.Floor ?? 0,
				floorGeneration = _floorRuntime?.Generation ?? 0,
				phase = _phase.ToString(),
				channel,
				operation,
				primaryObjective = decision.PrimaryObjective.ToString()
			});
		}

		private void ResetPermissionBlocks()
		{
			_blockedMovementOperation = string.Empty;
			_blockedTransitionOperation = string.Empty;
		}

		public void TickInteractionChannel(IFramework _)
		{
			var runtime = _floorRuntime;
			if (_pomanderDispatchedThisUpdate ||
			    runtime == null ||
			    runtime.IsDisposed ||
			    !AllowsChestSidecarInteraction)
			{
				return;
			}

			var waypoint = _activeWaypoint;
			var evidence = runtime.ObjectEvidence.Current;
			if (!waypoint.HasValue || !IsChestWaypoint(waypoint.Value) || evidence?.Available != true)
				return;

			if (_ctx?.ChestInteraction.TryInteract(ActiveChestAttempt, evidence, out var snapshot, out bool retry) == true)
			{
				runtime.ObjectEvidence.Invalidate();
				RecordChestInteractionStarted(waypoint.Value, snapshot, retry);
			}
		}

		public bool TickCombatChannel(out string status)
		{
			status = string.Empty;
			var context = _ctx;
			if (context == null || !AllowsCombatChannel)
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
				snap.TaskPhase = _taskRunner?.Phase ?? TaskPhase.Idle;
				snap.Status = _status;
			}
			return snap;
		}

		private unsafe void RefreshObjectiveDecision(InstanceContentDeepDungeon* dd, FloorRuntime runtime)
		{
			if (runtime.IsDisposed ||
			    runtime.Floor != dd->Floor ||
			    runtime.DungeonId != dd->DeepDungeonId)
			{
				return;
			}
			var objectEvidence = runtime.Kind == FloorRuntimeKind.Normal
				? runtime.ObjectEvidence.Current
				: null;
			if (runtime.Kind == FloorRuntimeKind.Normal && objectEvidence?.Available != true)
			{
				runtime.ClearObjectiveDecision();
				return;
			}

			var executor = _executor;
			var evidenceState = executor?.HoardEvidenceState ?? HoardEvidenceState.Disabled;
			var activeWaypoint = _activeWaypoint ?? executor?.CurrentWaypoint;
			bool passageOpen = _ctx?.Duty.PassageOpen == true;
			bool combatInProgress = Service.Condition[ConditionFlag.InCombat];
			bool routineCombatAllowed = combatInProgress || !passageOpen;
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
				runtime.Kind == FloorRuntimeKind.Boss ||
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
				runtime.Kind == FloorRuntimeKind.Normal &&
				!passageOpen &&
				hoardWorkResolved &&
				evidenceState != HoardEvidenceState.IntuitionPending;
			if (_ctx?.ControlledPtSurvey != null && runtime.Kind == FloorRuntimeKind.Normal)
			{
				visibleBandedChest = false;
				knownOrConfirmedHoard = false;
				requiredHoardDiscovery = false;
				mandatoryHoardTerminal = runtime.ControlledIntuitionResolved;
				passageActivationRequired = !passageOpen && runtime.ControlledIntuitionResolved;
			}
			var snapshot = new ObjectiveArbiterSnapshot(
				BossObjective: runtime.Kind == FloorRuntimeKind.Boss,
				VisibleBandedChest: visibleBandedChest,
				KnownOrConfirmedHoard: knownOrConfirmedHoard,
				RequiredHoardDiscovery: requiredHoardDiscovery,
				MandatoryHoardTerminal: mandatoryHoardTerminal,
				PassageOpen: passageOpen,
				PassageActivationRequired: passageActivationRequired,
				CombatInProgress: combatInProgress,
				RoutineCombatAllowed: routineCombatAllowed,
				ActiveChestInteraction: chestInteractionAllowed);
			if (!runtime.RefreshObjectiveDecision(
					snapshot,
					objectEvidence?.Version ?? 0,
					_ctx?.RunOptions.Version ?? 0,
					runtime.ObjectiveLedger.Version,
					out var decision))
				return;

			RecordReplayEvent("objective-arbiter-decision", new
			{
				floor = runtime.Floor,
				floorGeneration = runtime.Generation,
				primaryObjective = decision.PrimaryObjective.ToString(),
				movement = decision.Channels.Movement.ToString(),
				combat = decision.Channels.Combat.ToString(),
				interaction = decision.Channels.Interaction.ToString(),
				interactionOwner = "ChestSidecar",
				transition = decision.Channels.Transition.ToString(),
				evidenceState = evidenceState.ToString(),
				bossObjective = runtime.Kind == FloorRuntimeKind.Boss,
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

		private unsafe bool RefreshFloorObjectEvidence(InstanceContentDeepDungeon* dd, FloorRuntime runtime)
		{
			var refresh = runtime.ObjectEvidence.RefreshIfDue(runtime.DungeonId);
			if (refresh.Attempted)
			{
				ObserveCurrentRoom(dd);
				ObserveFloorEvidence(dd, runtime);
				PublishAuthoritativeRunFloorStateIfChanged(dd, runtime);
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

			runtime.ClearObjectiveDecision();
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

		private unsafe void ObserveFloorEvidence(InstanceContentDeepDungeon* dd, FloorRuntime runtime)
		{
			var session = runtime.EvidenceSession;
			var snapshot = runtime.ObjectEvidence.Current;
			if (snapshot == null)
				return;

			int intuitionStock = _pomanderManager.GetCount(FloorInitPlanner.IntuitionPomanderSlotIndex);
			int sightStock = _pomanderManager.GetCount(FloorInitPlanner.SightPomanderSlotIndex);
			bool naturalPtStonesSupported =
				DungeonCatalog.SupportsNaturalPtStones(dd->DeepDungeonId);
			bool pomandersUsableThisFloor =
				DeepDungeonFloorItemUsePolicy.CanUsePomanders(
					dd->DeepDungeonBanId);
			bool ptIncenseUsableThisFloor =
				DeepDungeonFloorItemUsePolicy.CanUsePtIncense(
					dd->DeepDungeonBanId);
			int mazerootStock =
				_ctx?.ControlledPtSurvey != null ||
				naturalPtStonesSupported
					? _pomanderManager.GetStoneCount(2)
					: 0;
			bool sightTrapObserved = snapshot.SightTrapIndicators.Count > 0;
			if (sightTrapObserved)
			{
				_executor?.ObserveSightTrapIndicators(snapshot);
				_chatWatchers?.ConfirmSightThisFloor("SightTrapIndicatorObserved");
			}
			bool sightConfirmed = _chatWatchers?.SightState == SightUseState.Confirmed;
			if (_ctx?.ControlledPtSurvey != null)
				TryConfirmControlledAuthoritativeReveal(runtime);
			else
			{
				ObserveNaturalRevealInventory(
					runtime,
					sightStock,
					mazerootStock);
				TryAdoptExternalNaturalReveal(runtime, sightConfirmed);
				TryConfirmNaturalAuthoritativeReveal(
					runtime,
					sightTrapObserved);
			}
			session?.ObserveEffectStates(
				_nativeIntuitionActive,
				intuitionStock,
				sightConfirmed,
				sightStock,
				"scheduled-object-evidence-refresh");
			session?.ObserveObjectEvidence(dd, snapshot);

			if (_ctx?.ControlledPtSurvey != null)
			{
				UpdateControlledPtSurvey(dd, runtime, snapshot, intuitionStock, sightStock, sightConfirmed);
				return;
			}

			var intuitionResolution = _chatWatchers?.ChatSaysHoard == true
				? IntuitionFloorResolution.Positive
				: _chatWatchers?.ChatSaysNoHoard == true
					? IntuitionFloorResolution.Negative
					: IntuitionFloorResolution.Unresolved;
			bool exactHoardEvidenceFromBanded;
			Vector3? exactIndicatorPosition =
				TryResolveNaturalExactHoardPosition(
					dd,
					runtime,
					snapshot,
					out exactHoardEvidenceFromBanded);
			bool exactIndicatorAvailable = exactIndicatorPosition.HasValue;
			bool acceptedIncomingEdgeKnown =
				exactIndicatorPosition.HasValue &&
				HasAcceptedIncomingEdge(
					dd,
					runtime,
					exactIndicatorPosition.Value);
			var decision = SightResearchPolicy.Decide(new SightResearchSnapshot(
				StableFloor: runtime.Kind == FloorRuntimeKind.Normal && runtime.Floor == dd->Floor && runtime.DungeonId == dd->DeepDungeonId,
				IntuitionResolution: intuitionResolution,
				ExactHoardIndicatorAvailable: exactIndicatorAvailable,
				AcceptedIncomingEdgeKnown: acceptedIncomingEdgeKnown,
				SightUseBlocked:
					IsSightUseBlocked() ||
					!pomandersUsableThisFloor,
				SightStock: sightStock,
				MazerootStock: mazerootStock,
				RevealDispatchedThisFloor: runtime.NaturalRevealDispatched,
				AuthoritativeRevealConfirmed: runtime.NaturalRevealConfirmed,
				MazerootSupported: naturalPtStonesSupported,
				MazerootUsableThisFloor: ptIncenseUsableThisFloor,
				BandedHoardEvidenceAvailable: exactHoardEvidenceFromBanded));
			int selectedResourceStock = decision.ShouldUseMazeroot
				? mazerootStock
				: sightStock;
			session?.ObserveResearchDecision(
				decision,
				selectedResourceStock,
				runtime.NaturalRevealResource);

			if (decision.ShouldCollectJointScan && exactIndicatorPosition.HasValue)
				UpdateNaturalJointCapture(dd, runtime, snapshot, exactIndicatorPosition.Value);

			if (!decision.ShouldUseReveal || !CanAttemptPomanderUse())
			{
				return;
			}

			bool dispatched = decision.ShouldUseSight
				? _pomanderManager.IsUsable(FloorInitPlanner.SightPomanderSlotIndex) &&
				  TryUsePomander(
					  FloorInitPlanner.SightPomanderSlotIndex,
					  dd,
					  "automatic exact-hoard research")
				: mazerootStock > 0 &&
				  TryUseNaturalMazeroot(
					  dd,
					  "automatic exact-hoard research");
			session?.ObserveResearchAction(
				dispatched,
				decision.RevealResource,
				selectedResourceStock);
			if (dispatched)
				runtime.SightResearchDispatched = true;
		}

		private unsafe Vector3? TryResolveNaturalExactHoardPosition(
			InstanceContentDeepDungeon* dd,
			FloorRuntime runtime,
			FloorObjectEvidenceSnapshot snapshot,
			out bool fromBandedChest)
		{
			fromBandedChest = false;
			if (snapshot.HoardIndicators.Count > 0)
				return snapshot.HoardIndicators[0].Object.Position;

			if (_executor?.CachedHoardIndicatorPos is { } cachedPosition)
				return cachedPosition;

			if (!BandedChestLocator.TryFindNearestToPlayer(snapshot, out Vector3? bandedPosition) ||
				!bandedPosition.HasValue ||
				!IsPalacePalCandidatePosition(dd, runtime, bandedPosition.Value))
			{
				return null;
			}

			fromBandedChest = true;
			return bandedPosition.Value;
		}

		private unsafe bool IsPalacePalCandidatePosition(
			InstanceContentDeepDungeon* dd,
			FloorRuntime runtime,
			Vector3 position)
		{
			IReadOnlyList<int>? rooms = runtime.NormalGraph?.ReachableRooms;
			if (rooms == null)
				return false;

			int roomIndex = RoomGraph.GetRoomIndexForPosition(dd, position, rooms, -1);
			if (roomIndex < 0)
				return false;

			IReadOnlyList<Vector3>? candidates =
				_executor?.GetPalacePalCandidatesForRoom(dd, roomIndex);
			if (candidates == null)
				return false;

			var rawPosition = new RawWorldPosition(position.X, position.Y, position.Z);
			for (int i = 0; i < candidates.Count; i++)
			{
				Vector3 candidate = candidates[i];
				if (RawWorldPosition.CanonicallyEquals(
					rawPosition,
					new RawWorldPosition(candidate.X, candidate.Y, candidate.Z)))
				{
					return true;
				}
			}

			return false;
		}

		private unsafe bool HasAcceptedIncomingEdge(
			InstanceContentDeepDungeon* dd,
			FloorRuntime runtime,
			Vector3 exactHoardPosition)
		{
			DetailedMapCatalog? catalog = _ctx?.DetailedMap.Catalog;
			IReadOnlyList<int>? rooms = runtime.NormalGraph?.ReachableRooms;
			if (catalog == null || rooms == null)
				return false;

			int roomIndex = RoomGraph.GetRoomIndexForPosition(
				dd,
				exactHoardPosition,
				rooms,
				-1);
			if (roomIndex < 0)
				return false;

			var rawPosition = new RawWorldPosition(
				exactHoardPosition.X,
				exactHoardPosition.Y,
				exactHoardPosition.Z);
			return DetailedMapResearchKnowledge.ResolveHoardPredecessor(
				       catalog,
				       dd->ActiveLayoutIndex,
				       roomIndex,
				       rawPosition) !=
			       DetailedMapHoardPredecessorKnowledge.Unknown;
		}

		private void ObserveNaturalRevealInventory(
			FloorRuntime runtime,
			int sightStock,
			int mazerootStock)
		{
			long sightLogSequence =
				_chatWatchers?.SightLogSequence ?? 0;
			long mazerootLogSequence =
				_chatWatchers?.MazerootLogSequence ?? 0;
			NaturalRevealInventoryDecision decision =
				NaturalRevealInventoryPolicy.Decide(
					new NaturalRevealInventorySnapshot(
						runtime.NaturalRevealInventoryBaselineEstablished,
						runtime.NaturalPreviousSightStock,
						runtime.NaturalPreviousMazerootStock,
						sightStock,
						mazerootStock,
						runtime.NaturalRevealDispatched ||
						runtime.NaturalRevealConfirmed ||
						runtime.NaturalMazerootAttemptedOrAdopted,
						DungeonCatalog.SupportsNaturalPtStones(
							runtime.DungeonId)));
			long previousSightLogSequence =
				runtime.NaturalPreviousSightLogSequence;
			long previousMazerootLogSequence =
				runtime.NaturalPreviousMazerootLogSequence;
			runtime.NaturalRevealInventoryBaselineEstablished = true;
			runtime.NaturalPreviousSightStock = sightStock;
			runtime.NaturalPreviousMazerootStock = mazerootStock;
			runtime.NaturalPreviousSightLogSequence =
				sightLogSequence;
			runtime.NaturalPreviousMazerootLogSequence =
				mazerootLogSequence;
			if (decision.Kind !=
			    NaturalRevealInventoryDecisionKind.AdoptExternalPending)
			{
				return;
			}

			runtime.NaturalRevealDispatched = true;
			runtime.NaturalRevealResource = decision.Resource;
			if (decision.Resource == SightResearchRevealResource.Mazeroot)
				runtime.NaturalMazerootAttemptedOrAdopted = true;
			runtime.NaturalSightLogSequenceAtDispatch =
				previousSightLogSequence;
			runtime.NaturalMazerootLogSequenceAtDispatch =
				previousMazerootLogSequence;
			runtime.NaturalRevealConfirmed = false;
			runtime.NaturalJointScanComplete = false;
			_chatWatchers?.MarkSightAttemptedThisFloor();
			RecordReplayEvent("external-reveal-pending", new
			{
				floor = runtime.Floor,
				revealSource = decision.Resource.ToString()
			});
		}

		private bool TryConfirmNaturalAuthoritativeReveal(
			FloorRuntime runtime,
			bool sightTrapObserved)
		{
			if (runtime.NaturalRevealConfirmed)
				return true;
			if (!runtime.NaturalRevealDispatched)
				return false;

			bool confirmed =
				NaturalRevealInventoryPolicy.IsAuthoritativeConfirmation(
					runtime.NaturalRevealResource,
					(_chatWatchers?.SightLogSequence ?? 0) >
						runtime.NaturalSightLogSequenceAtDispatch,
					(_chatWatchers?.MazerootLogSequence ?? 0) >
						runtime.NaturalMazerootLogSequenceAtDispatch,
					sightTrapObserved,
					DungeonCatalog.SupportsNaturalPtStones(
						runtime.DungeonId));
			if (!confirmed)
				return false;

			runtime.NaturalRevealConfirmed = true;
			runtime.NaturalRevealConfirmationRefreshSequence =
				runtime.ObjectEvidence.Current?.RefreshSequence ??
				runtime.ObjectEvidence.RefreshCount;
			runtime.NaturalRevealConfirmationFullScanCount =
				runtime.ObjectEvidence.FullScanCount;
			runtime.EvidenceSession?.ObserveResearchAuthoritativeRevealConfirmed();
			return true;
		}

		private void TryAdoptExternalNaturalReveal(
			FloorRuntime runtime,
			bool sightConfirmed)
		{
			if (runtime.NaturalRevealDispatched ||
			    runtime.NaturalRevealConfirmed ||
			    runtime.NaturalMazerootAttemptedOrAdopted ||
			    !sightConfirmed)
			{
				return;
			}

			bool sightLogObserved =
				(_chatWatchers?.SightLogSequence ?? 0) > 0;
			bool mazerootLogObserved =
				DungeonCatalog.SupportsNaturalPtStones(
					runtime.DungeonId) &&
				(_chatWatchers?.MazerootLogSequence ?? 0) > 0;
			SightResearchRevealResource resource =
				(sightLogObserved, mazerootLogObserved) switch
				{
					(true, false) => SightResearchRevealResource.Sight,
					(false, true) => SightResearchRevealResource.Mazeroot,
					_ => SightResearchRevealResource.None
				};
			if (resource == SightResearchRevealResource.None)
				return;

			runtime.NaturalRevealResource = resource;
			if (resource == SightResearchRevealResource.Mazeroot)
				runtime.NaturalMazerootAttemptedOrAdopted = true;
			runtime.NaturalRevealConfirmed = true;
			runtime.NaturalRevealConfirmationRefreshSequence =
				runtime.ObjectEvidence.Current?.RefreshSequence ??
				runtime.ObjectEvidence.RefreshCount;
			runtime.NaturalRevealConfirmationFullScanCount =
				runtime.ObjectEvidence.FullScanCount;
			runtime.EvidenceSession?.ObserveResearchAuthoritativeRevealConfirmed();
		}

		private unsafe void UpdateNaturalJointCapture(
			InstanceContentDeepDungeon* dd,
			FloorRuntime runtime,
			FloorObjectEvidenceSnapshot snapshot,
			Vector3 exactHoardPosition)
		{
			if (runtime.NaturalJointScanComplete ||
			    !runtime.NaturalRevealConfirmed ||
			    runtime.EvidenceSession?.Bundle.AcquisitionMode !=
			    FloorEvidenceAcquisitionMode.AutomaticCommunitySurvey ||
			    snapshot.PlayerPosition is not { } playerPosition)
			{
				return;
			}

			if (!runtime.NaturalCandidateUniverseResolved)
			{
				IReadOnlyList<int>? rooms = runtime.NormalGraph?.ReachableRooms;
				if (rooms == null)
					return;

				int hoardRoomIndex = RoomGraph.GetRoomIndexForPosition(
					dd,
					exactHoardPosition,
					rooms,
					-1);
				if (hoardRoomIndex < 0)
					return;

				IReadOnlyList<Vector3>? palacePalCandidates =
					_executor?.GetPalacePalCandidatesForRoom(dd, hoardRoomIndex);
				if (palacePalCandidates == null || palacePalCandidates.Count == 0)
					return;

				var candidates = new RawWorldPosition[palacePalCandidates.Count];
				for (int i = 0; i < palacePalCandidates.Count; i++)
				{
					Vector3 candidate = palacePalCandidates[i];
					candidates[i] = new RawWorldPosition(
						candidate.X,
						candidate.Y,
						candidate.Z);
				}
				runtime.NaturalCandidateUniverse = candidates;
				runtime.NaturalCandidateUniverseResolved = true;
			}

			for (int i = 0; i < snapshot.SightTrapIndicators.Count; i++)
			{
				FloorObjectEvidence trap = snapshot.SightTrapIndicators[i];
				if (!runtime.NaturalObservedTrapWitnesses.Add(
					    ControlledTrapWitnessKey.From(trap)))
				{
					continue;
				}

				float dx = trap.Position.X - playerPosition.X;
				float dz = trap.Position.Z - playerPosition.Z;
				runtime.NaturalMaximumTrapWitnessDistance = MathF.Max(
					runtime.NaturalMaximumTrapWitnessDistance,
					MathF.Sqrt(dx * dx + dz * dz));
			}

			float safeRadius =
				ControlledPtSurveyPolicy.GetProvenTrapLoadSafeRadius(
					runtime.NaturalMaximumTrapWitnessDistance);
			bool trapWitnessAvailable =
				runtime.NaturalObservedTrapWitnesses.Count > 0 &&
				safeRadius > 0f;
			var rawPlayerPosition = new RawWorldPosition(
				playerPosition.X,
				playerPosition.Y,
				playerPosition.Z);
			bool allCandidatesCovered =
				trapWitnessAvailable &&
				ControlledPtSurveyPolicy.AreAllCandidatesCovered(
					rawPlayerPosition,
					runtime.NaturalCandidateUniverse,
					safeRadius);
			bool synchronizedScanAvailable =
				snapshot.Available &&
				snapshot.RefreshSequence >
				runtime.NaturalRevealConfirmationRefreshSequence &&
				runtime.ObjectEvidence.FullScanCount >
				runtime.NaturalRevealConfirmationFullScanCount;
			if (!synchronizedScanAvailable ||
			    !trapWitnessAvailable ||
			    !allCandidatesCovered)
			{
				return;
			}

			runtime.NaturalJointScanComplete = true;
			runtime.EvidenceSession?.ObserveResearchJointScanComplete();
			RecordReplayEvent("natural-joint-scan-complete", new
			{
				floor = dd->Floor,
				revealSource = runtime.NaturalRevealResource.ToString(),
				safeRadius,
				candidateCount = runtime.NaturalCandidateUniverse.Length
			});
		}

		private bool TryConfirmControlledAuthoritativeReveal(FloorRuntime runtime)
		{
			if (runtime.ControlledSightConfirmed)
				return true;

			bool confirmed = ControlledPtSurveyPolicy.IsAuthoritativeCaptureReveal(
				runtime.ControlledCaptureItem,
				(_chatWatchers?.SightLogSequence ?? 0) >
					runtime.ControlledSightLogSequenceAtDispatch,
				(_chatWatchers?.MazerootLogSequence ?? 0) >
					runtime.ControlledMazerootLogSequenceAtDispatch);
			if (!confirmed)
				return false;

			runtime.ControlledSightConfirmed = true;
			runtime.ControlledSightConfirmedAtMilliseconds = Environment.TickCount64;
			runtime.ControlledSightConfirmationRefreshSequence =
				runtime.ObjectEvidence.Current?.RefreshSequence ?? runtime.ObjectEvidence.RefreshCount;
			runtime.ControlledSightConfirmationFullScanCount = runtime.ObjectEvidence.FullScanCount;
			runtime.EvidenceSession?.ObserveAuthoritativeRevealConfirmed();
			return true;
		}

		private unsafe void UpdateControlledPtSurvey(
			InstanceContentDeepDungeon* dd,
			FloorRuntime runtime,
			FloorObjectEvidenceSnapshot snapshot,
			int intuitionStock,
			int sightStock,
			bool sightActive)
		{
			var survey = _ctx?.ControlledPtSurvey;
			var evidence = runtime.EvidenceSession;
			if (survey == null)
				return;

			int mazerootCount = _pomanderManager.GetStoneCount(2);
			int poisonfruitCount = _pomanderManager.GetStoneCount(1);
			int effectiveSightStock =
				DeepDungeonFloorItemUsePolicy.CanUsePomanders(
					dd->DeepDungeonBanId)
					? sightStock
					: 0;
			int effectiveMazerootCount =
				DeepDungeonFloorItemUsePolicy.CanUsePtIncense(
					dd->DeepDungeonBanId)
					? mazerootCount
					: 0;
			ProcessPendingControlledPostCapturePoisonfruit(dd, runtime, poisonfruitCount);
			if (TryUseControlledStrength(dd, runtime))
				return;
			if (runtime.ControlledOpportunityCompleted)
				return;

			evidence?.ConfigureControlledSurvey(
				ControlledPtSurveyPolicy.IsResearchFloor(dd->Floor)
					? ControlledSurveyFloorRole.SelectedTarget
					: ControlledSurveyFloorRole.Transit,
				survey.ResearchFloors);

			if (runtime.ControlledIntuitionRequiresCurrentUse &&
			    runtime.ControlledIntuitionExpectationStartedAtMilliseconds == 0)
			{
				bool firstFloor = dd->Floor == ControlledPtSurveyPolicy.FirstFloor;
				if (firstFloor &&
				    !ControlledPtSurveyPolicy.HasSightCapableResource(sightStock, mazerootCount))
				{
					survey.Fail("Controlled PT capture requires at least one Sight or 敏慧 before arming Intuition on floor 21.");
					survey.RequestSuccessfulLeave();
					return;
				}
				if (firstFloor && _nativeIntuitionActive)
				{
					survey.Fail("Controlled PT floor 21 must arm Intuition only after its explicit current-floor dispatch.");
					survey.RequestSuccessfulLeave();
					return;
				}
				if (!firstFloor && intuitionStock <= 0)
				{
					FailControlledInheritedState(
						runtime,
						$"Controlled PT floor {dd->Floor} lost Intuition with no remaining stock to reactivate it.");
					return;
				}
				if (!CanAttemptPomanderUse())
					return;
				if (!_pomanderManager.IsUsable(FloorInitPlanner.IntuitionPomanderSlotIndex))
				{
					if (firstFloor)
					{
						survey.Fail("Controlled PT capture requires one usable Intuition on floor 21.");
						survey.RequestSuccessfulLeave();
						return;
					}
					_status = $"Controlled PT: waiting to reactivate Intuition on floor {dd->Floor}";
					return;
				}
				TryUsePomander(
					FloorInitPlanner.IntuitionPomanderSlotIndex,
					dd,
					firstFloor
						? "controlled persistent intuition"
						: "controlled Intuition reactivation");
				return;
			}

			ControlledPtIntuitionResolutionDecision intuitionDecision;
			if (runtime.ControlledIntuitionRequiresCurrentUse)
			{
				if (runtime.ControlledIntuitionExpectationStartedAtMilliseconds == 0)
				{
					_status = "Controlled PT: waiting for correlated Intuition expectation";
					return;
				}

				int intuitionElapsedMilliseconds = (int)Math.Clamp(
					Environment.TickCount64 - runtime.ControlledIntuitionExpectationStartedAtMilliseconds,
					0L,
					int.MaxValue);
				intuitionDecision = runtime.ControlledIntuitionDecision ??
					ControlledPtSurveyPolicy.ResolveCurrentIntuition(
						_chatWatchers?.ChatSaysHoard == true,
						_chatWatchers?.ChatSaysNoHoard == true,
						intuitionElapsedMilliseconds,
						CurrentIntuitionResolutionWindowMilliseconds);
			}
			else
			{
				if (!runtime.InheritedIntuitionDecision.HasValue)
				{
					int elapsedMilliseconds = runtime.InheritedIntuitionArmedAtMilliseconds > 0
						? (int)Math.Clamp(
							Environment.TickCount64 - runtime.InheritedIntuitionArmedAtMilliseconds,
							0L,
							int.MaxValue)
						: 0;
					runtime.ControlledIntuitionResolutionPending = true;
					_status =
						$"Controlled PT: waiting for inherited Intuition result ({elapsedMilliseconds}/{CurrentIntuitionResolutionWindowMilliseconds}ms)";
					return;
				}

				var inheritedDecision = runtime.InheritedIntuitionDecision.Value;
				var source = inheritedDecision.Source switch
				{
					InheritedIntuitionResolutionSource.HoardPresent =>
						ControlledPtIntuitionResolutionSource.InheritedHoardPresent,
					InheritedIntuitionResolutionSource.NoHoardInferred =>
						ControlledPtIntuitionResolutionSource.InheritedNoHoardInferred,
					InheritedIntuitionResolutionSource.InvalidNoHoardMessage =>
						ControlledPtIntuitionResolutionSource.InvalidInheritedNoHoardMessage,
					InheritedIntuitionResolutionSource.RejectedEvidence =>
						ControlledPtIntuitionResolutionSource.RejectedInheritedEvidence,
					_ => ControlledPtIntuitionResolutionSource.None
				};
				intuitionDecision = new ControlledPtIntuitionResolutionDecision(
					inheritedDecision.Terminal,
					inheritedDecision.HoardPresent,
					inheritedDecision.NoHoard,
					inheritedDecision.IsError,
					source,
					inheritedDecision.ElapsedMilliseconds);
			}
			if (!intuitionDecision.Terminal)
			{
				runtime.ControlledIntuitionResolutionPending = true;
				_status = "Controlled PT: waiting for Intuition result";
				return;
			}
			runtime.ControlledIntuitionResolutionPending = false;

			if (!runtime.ControlledIntuitionResolved)
			{
				_chatWatchers?.CancelExpectedIntuitionResult(runtime.ControlledIntuitionExpectationAttemptId);
				PendingIntuition.CancelAttempt(runtime.ControlledIntuitionExpectationAttemptId);
				runtime.ControlledIntuitionResolved = true;
				runtime.ControlledIntuitionDecision = intuitionDecision;
				evidence?.ObserveControlledIntuitionResolution(
					intuitionDecision.Source,
					intuitionDecision.ElapsedMilliseconds,
					CurrentIntuitionResolutionWindowMilliseconds);
			}
			if (intuitionDecision.IsError)
			{
				survey.Fail($"Controlled PT floor {dd->Floor} Intuition result failed: {intuitionDecision.Source}.");
				survey.RequestSuccessfulLeave();
				return;
			}

			if (intuitionDecision.NoHoard)
			{
				CompleteControlledOpportunity(
					dd,
					runtime,
					intuitionDecision.Source == ControlledPtIntuitionResolutionSource.InheritedNoHoardInferred
						? ControlledPtSurveyTargetOutcome.InheritedNoHoardInferred
						: ControlledPtSurveyTargetOutcome.IntuitionNegative,
					sightStock,
					mazerootCount,
					poisonfruitCount);
				return;
			}

			bool hasIndicator = snapshot.HoardIndicators.Count > 0;
			var indicatorAction = ControlledPtSurveyPolicy.DecidePositiveIndicatorAction(
				runtime.ControlledHoardPositionResolved,
				hasIndicator);
			if (indicatorAction == ControlledPtPositiveIndicatorAction.AcquireExactIndicator)
			{
				runtime.ControlledPositiveMessagePendingIndicator = true;
				_status = "Controlled PT: positive Intuition result; acquiring exact indicator";
				return;
			}
			runtime.ControlledPositiveMessagePendingIndicator = false;

			if (indicatorAction == ControlledPtPositiveIndicatorAction.ContinueCapture)
			{
				if (!runtime.ControlledHoardPositionResolved)
				{
					var hoardPosition = snapshot.HoardIndicators[0].Object.Position;
					var rooms = runtime.NormalGraph?.ReachableRooms;
					int hoardRoom = rooms == null
						? -1
						: RoomGraph.GetRoomIndexForPosition(
							dd,
							hoardPosition,
							rooms,
							-1);
					if (hoardRoom < 0)
					{
						CompleteControlledJointSampleIncomplete(
							dd,
							runtime,
							$"Controlled PT floor {dd->Floor} could not resolve the exact H coordinate to a reachable room.");
						return;
					}
					runtime.ControlledHoardRoomIndex = hoardRoom;
					runtime.ControlledHoardPosition = hoardPosition;
					runtime.ControlledHoardPositionResolved = true;
				}

				runtime.ControlledPositiveCapturePending = true;
				if (!sightActive && !runtime.ControlledSightDispatched)
				{
					if (!CanAttemptPomanderUse())
						return;
					var action = ControlledPtSurveyPolicy.DecidePositiveCaptureItem(
						dd->Floor,
						sightActive,
						effectiveSightStock,
						effectiveMazerootCount);
					if (action == ControlledPtSurveyItemAction.UseSight &&
					    !_pomanderManager.IsUsable(
						    FloorInitPlanner.SightPomanderSlotIndex))
					{
						_status =
							"Controlled PT: waiting for selected Sight to become usable";
						return;
					}
					if (action == ControlledPtSurveyItemAction.UseMazeroot &&
					    !EnsureControlledDispatchOutsidePassage(
						    dd,
						    runtime,
						    barrierRequired: true,
						    "controlled positive 敏慧 capture"))
					{
						return;
					}
					long sightLogSequenceBeforeDispatch = _chatWatchers?.SightLogSequence ?? 0;
					long mazerootLogSequenceBeforeDispatch = _chatWatchers?.MazerootLogSequence ?? 0;
					bool dispatched = action switch
					{
						ControlledPtSurveyItemAction.UseMazeroot => TryUseControlledStone(
							2,
							dd,
							"controlled positive capture with 敏慧"),
						ControlledPtSurveyItemAction.UseSight => TryUsePomander(
							FloorInitPlanner.SightPomanderSlotIndex,
							dd,
							"controlled positive capture with Sight"),
						_ => false
					};
					if (!dispatched)
					{
						CompleteControlledJointSampleIncomplete(
							dd,
							runtime,
							$"Controlled PT floor {dd->Floor} has an exact hoard indicator but no Sight-capable resource could be dispatched.");
						return;
					}
					evidence?.ObserveControlledCaptureItem(action);
					runtime.ControlledCaptureItem = action;
					runtime.ControlledSightDispatched = true;
					runtime.ControlledSightLogSequenceAtDispatch = sightLogSequenceBeforeDispatch;
					runtime.ControlledMazerootLogSequenceAtDispatch = mazerootLogSequenceBeforeDispatch;
					runtime.ControlledSightDispatchedAt = DateTime.UtcNow;
					runtime.ObjectEvidence.Invalidate();
					evidence?.ObserveResearchAction(
						true,
						action == ControlledPtSurveyItemAction.UseMazeroot
							? SightResearchRevealResource.Mazeroot
							: SightResearchRevealResource.Sight,
						action == ControlledPtSurveyItemAction.UseMazeroot
							? mazerootCount
							: sightStock);
					return;
				}

				bool authoritativeRevealConfirmed =
					TryConfirmControlledAuthoritativeReveal(runtime);
				if (authoritativeRevealConfirmed)
				{
					if (snapshot.PlayerPosition is not { } scanPlayerPosition)
					{
						_status = "Controlled PT: waiting for scan-captured player position";
						return;
					}

					if (!runtime.ControlledCandidateUniverseResolved)
					{
						var palacePalCandidates =
							_executor?.GetPalacePalCandidatesForRoom(
								dd,
								runtime.ControlledHoardRoomIndex);
						if (palacePalCandidates == null || palacePalCandidates.Count == 0)
						{
							CompleteControlledJointSampleIncomplete(
								dd,
								runtime,
								$"Controlled PT floor {dd->Floor} has no PalacePal T/H candidate universe for H room {runtime.ControlledHoardRoomIndex}.");
							return;
						}

						var candidates = new RawWorldPosition[palacePalCandidates.Count];
						for (int i = 0; i < palacePalCandidates.Count; i++)
						{
							var candidate = palacePalCandidates[i];
							candidates[i] = new RawWorldPosition(
								candidate.X,
								candidate.Y,
								candidate.Z);
						}
						runtime.ControlledCandidateUniverse = candidates;
						runtime.ControlledCandidateUniverseResolved = true;
					}

					for (int i = 0; i < snapshot.SightTrapIndicators.Count; i++)
					{
						var trap = snapshot.SightTrapIndicators[i];
						if (!runtime.ControlledObservedTrapWitnesses.Add(
							    ControlledTrapWitnessKey.From(trap)))
						{
							continue;
						}

						float dx = trap.Position.X - scanPlayerPosition.X;
						float dz = trap.Position.Z - scanPlayerPosition.Z;
						float firstAppearanceDistance = MathF.Sqrt(dx * dx + dz * dz);
						runtime.ControlledMaximumTrapWitnessDistance = MathF.Max(
							runtime.ControlledMaximumTrapWitnessDistance,
							firstAppearanceDistance);
						RecordReplayEvent("controlled-trap-load-witness", new
						{
							floor = dd->Floor,
							trap.BaseId,
							trap.GameObjectId,
							trap.Position,
							playerPosition = scanPlayerPosition,
							firstAppearanceDistance,
							provenSafeRadius =
								ControlledPtSurveyPolicy.GetProvenTrapLoadSafeRadius(
									runtime.ControlledMaximumTrapWitnessDistance)
						});
					}

					float safeRadius =
						ControlledPtSurveyPolicy.GetProvenTrapLoadSafeRadius(
							runtime.ControlledMaximumTrapWitnessDistance);
					bool trapWitnessAvailable =
						runtime.ControlledObservedTrapWitnesses.Count > 0 &&
						safeRadius > 0f;
					var rawPlayerPosition = new RawWorldPosition(
						scanPlayerPosition.X,
						scanPlayerPosition.Y,
						scanPlayerPosition.Z);
					bool allCandidatesCovered =
						trapWitnessAvailable &&
						ControlledPtSurveyPolicy.AreAllCandidatesCovered(
							rawPlayerPosition,
							runtime.ControlledCandidateUniverse,
							safeRadius);
					bool synchronizedScanAvailable =
						snapshot.Available &&
						snapshot.RefreshSequence >
							runtime.ControlledSightConfirmationRefreshSequence &&
						runtime.ObjectEvidence.FullScanCount >
							runtime.ControlledSightConfirmationFullScanCount;
					bool postArrivalScanAvailable =
						runtime.ControlledHoardRoomTargetReached &&
						snapshot.Available &&
						snapshot.RefreshSequence >
							Math.Max(
								runtime.ControlledSightConfirmationRefreshSequence,
								runtime.ControlledHoardRoomTargetRefreshSequence) &&
						runtime.ObjectEvidence.FullScanCount >
							Math.Max(
								runtime.ControlledSightConfirmationFullScanCount,
								runtime.ControlledHoardRoomTargetFullScanCount);
					var jointAction = ControlledPtSurveyPolicy.DecideJointCapture(
						authoritativeRevealConfirmed,
						runtime.ControlledCandidateUniverse.Length > 0,
						trapWitnessAvailable,
						allCandidatesCovered,
						synchronizedScanAvailable,
						runtime.ControlledHoardRoomTargetReached,
						postArrivalScanAvailable);
					if (jointAction == ControlledPtJointCaptureAction.Complete)
					{
						CancelActiveMovement();
						CompleteControlledOpportunity(
							dd,
							runtime,
							ControlledPtSurveyTargetOutcome.PositiveCaptured,
							sightStock,
							mazerootCount,
							poisonfruitCount);
						return;
					}

					if (jointAction == ControlledPtJointCaptureAction.Incomplete)
					{
						CompleteControlledJointSampleIncomplete(
							dd,
							runtime,
							trapWitnessAvailable
								? $"Controlled PT floor {dd->Floor} T witness radius {safeRadius:F1}m did not cover every PalacePal candidate after reaching H room {runtime.ControlledHoardRoomIndex}."
								: $"Controlled PT floor {dd->Floor} obtained no T visibility witness after reaching H room {runtime.ControlledHoardRoomIndex}.");
						return;
					}

					_status = jointAction == ControlledPtJointCaptureAction.WaitForHoardRoomScan
						? $"Controlled PT: waiting for synchronized scan in H room {runtime.ControlledHoardRoomIndex}"
						: $"Controlled PT: approaching H room {runtime.ControlledHoardRoomIndex}";
				}
				else if (runtime.ControlledSightDispatched &&
				         DateTime.UtcNow - runtime.ControlledSightDispatchedAt >= TimeSpan.FromSeconds(5))
				{
					CompleteControlledJointSampleIncomplete(
						dd,
						runtime,
						$"Controlled PT floor {dd->Floor} did not expose authoritative reveal confirmation after the capture item was dispatched.");
				}
				return;
			}

		}

		private unsafe void CompleteControlledOpportunity(
			InstanceContentDeepDungeon* dd,
			FloorRuntime runtime,
			ControlledPtSurveyTargetOutcome outcome,
			int sightStock,
			int mazerootCount,
			int poisonfruitCount)
		{
			var survey = _ctx?.ControlledPtSurvey;
			if (survey == null || runtime.ControlledOpportunityCompleted)
				return;

			runtime.ControlledOpportunityCompleted = true;
			runtime.ControlledPositiveCapturePending = false;
			runtime.ControlledPositiveMessagePendingIndicator = false;
			runtime.ControlledDispatchBarrierActive = false;
			runtime.EvidenceSession?.ObserveControlledOutcome(outcome);
			if (!PersistControlledFloorBeforeLeave(runtime, $"target-terminal:{outcome}"))
			{
				survey.Fail($"Controlled PT floor {dd->Floor} evidence could not be persisted.");
				survey.RequestSuccessfulLeave();
				CancelActiveMovement();
				return;
			}
			var decision = ControlledPtSurveyPolicy.DecideFloorAction(
				dd->Floor,
				outcome,
				sightStock,
				mazerootCount,
				poisonfruitCount);

			if (!decision.ShouldAbandon)
			{
				bool negativeOutcome =
					outcome is ControlledPtSurveyTargetOutcome.IntuitionNegative or
						ControlledPtSurveyTargetOutcome.InheritedNoHoardInferred;
				bool sightCaptureOutcome =
					(outcome is ControlledPtSurveyTargetOutcome.PositiveCaptured or
						ControlledPtSurveyTargetOutcome.PositiveJointSampleIncomplete) &&
					runtime.ControlledCaptureItem == ControlledPtSurveyItemAction.UseSight;
				if (negativeOutcome || sightCaptureOutcome)
				{
					runtime.ControlledPendingPostCapturePoisonfruit = true;
					ProcessPendingControlledPostCapturePoisonfruit(dd, runtime, poisonfruitCount);
				}
				return;
			}

			survey.RequestSuccessfulLeave();
			CancelActiveMovement();
		}

		private unsafe void UpdateControlledCandidateCoverageMovement(
			InstanceContentDeepDungeon* dd,
			FloorRuntime runtime)
		{
			var player = Service.LocalPlayer;
			if (player == null)
			{
				_status = "Controlled PT: waiting for player before candidate coverage";
				return;
			}

			if (!runtime.ControlledCandidateUniverseResolved ||
			    runtime.ControlledCandidateUniverse.Length == 0)
			{
				CancelActiveMovement();
				_status = "Controlled PT: waiting for candidate universe";
				return;
			}

			int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
			if (runtime.ControlledHoardRoomTargetReached)
			{
				CancelActiveMovement();
				_status =
					$"Controlled PT: waiting for synchronized scan in H room {runtime.ControlledHoardRoomIndex}";
				return;
			}

			if (runtime.ControlledHoardRoomIndex < 0 ||
			    !MapPos.TryGetRoomCenter(
				    dd,
				    runtime.ControlledHoardRoomIndex,
				    out var hoardRoomCenter))
			{
				CompleteControlledJointSampleIncomplete(
					dd,
					runtime,
					$"Controlled PT floor {dd->Floor} lost the center of H room {runtime.ControlledHoardRoomIndex}.");
				return;
			}

			var navigation = _navDriver?.Drive(
				hoardRoomCenter,
				player.Position,
				1.2f,
				dd,
				playerRoom,
				runtime.ControlledHoardRoomIndex) ?? NavDriveResult.Failed;
			if (navigation == NavDriveResult.Failed)
			{
				CompleteControlledJointSampleIncomplete(
					dd,
					runtime,
					$"Controlled PT floor {dd->Floor} could not approach H room {runtime.ControlledHoardRoomIndex}.");
				return;
			}
			if (navigation == NavDriveResult.Arrived)
			{
				CancelActiveMovement();
				runtime.ControlledHoardRoomTargetReached = true;
				runtime.ControlledHoardRoomTargetRefreshSequence =
					runtime.ObjectEvidence.Current?.RefreshSequence ??
					runtime.ObjectEvidence.RefreshCount;
				runtime.ControlledHoardRoomTargetFullScanCount =
					runtime.ObjectEvidence.FullScanCount;
				_status =
					$"Controlled PT: scanning after reaching H room {runtime.ControlledHoardRoomIndex}";
				return;
			}

			_status =
				$"Controlled PT: approaching H room {runtime.ControlledHoardRoomIndex}";
		}

		private unsafe void CompleteControlledJointSampleIncomplete(
			InstanceContentDeepDungeon* dd,
			FloorRuntime runtime,
			string reason)
		{
			if (_ctx?.ControlledPtSurvey == null || runtime.ControlledOpportunityCompleted)
				return;

			CancelActiveMovement();
			Service.Log.Warning($"[ControlledPT] {reason}");
			RecordReplayEvent("controlled-joint-sample-incomplete", new
			{
				floor = dd->Floor,
				runtime.ControlledHoardRoomIndex,
				reason
			});
			CompleteControlledOpportunity(
				dd,
				runtime,
				ControlledPtSurveyTargetOutcome.PositiveJointSampleIncomplete,
				_pomanderManager.GetCount(FloorInitPlanner.SightPomanderSlotIndex),
				_pomanderManager.GetStoneCount(2),
				_pomanderManager.GetStoneCount(1));
		}

		private void FailControlledInheritedState(FloorRuntime runtime, string reason)
		{
			var survey = _ctx?.ControlledPtSurvey;
			if (survey == null || runtime.ControlledOpportunityCompleted)
				return;

			runtime.ControlledOpportunityCompleted = true;
			runtime.ControlledIntuitionResolutionPending = false;
			runtime.EvidenceSession?.ObserveControlledOutcome(
				ControlledPtSurveyTargetOutcome.InheritedStateInconsistent);
			if (!PersistControlledFloorBeforeLeave(runtime, $"inherited-state-inconsistent:{reason}"))
				reason += " Evidence persistence also failed.";
			survey.Fail(reason);
			survey.RequestSuccessfulLeave();
			CancelActiveMovement();
		}

		private bool PersistControlledFloorBeforeLeave(FloorRuntime runtime, string reason)
		{
			var evidence = runtime.EvidenceSession;
			if (evidence == null)
				return false;

			try
			{
				var bundle = evidence.Finalize($"controlled-exit:{reason}");
				runtime.EvidenceSession = null;
				return _floorEvidenceJournal?.EnqueueAndWait(bundle, TimeSpan.FromSeconds(2)) == true;
			}
			catch (Exception ex)
			{
				Service.Log.Error($"[FloorEvidenceJournal] Controlled pre-leave flush failed: {ex}");
				return false;
			}
		}

		private unsafe bool TryUseControlledStrength(
			InstanceContentDeepDungeon* dd,
			FloorRuntime runtime)
		{
			if (runtime.ControlledStrengthHandled)
				return false;
			if (HasLocalPlayerStatus(StrengthStatusId))
			{
				runtime.ControlledStrengthHandled = true;
				return false;
			}
			if (!_pomanderManager.IsUsable(FloorInitPlanner.StrengthPomanderSlotIndex) ||
			    !CanAttemptPomanderUse())
			{
				return false;
			}
			if (!TryUsePomander(
				    FloorInitPlanner.StrengthPomanderSlotIndex,
				    dd,
				    "controlled combat acceleration"))
			{
				return false;
			}

			runtime.ControlledStrengthHandled = true;
			return true;
		}

		private unsafe bool TryUseControlledStone(
			byte stoneId,
			InstanceContentDeepDungeon* dd,
			string reason)
		{
			if (!DeepDungeonFloorItemUsePolicy.CanUsePtIncense(
				    dd->DeepDungeonBanId) ||
			    !CanAttemptPomanderUse() ||
			    !_pomanderManager.UseStone(stoneId))
				return false;

			_pomanderDispatchedThisUpdate = true;
			_nextPomanderUseAt = DateTime.UtcNow.AddSeconds(3);
			_floorRuntime?.ObjectEvidence.Invalidate();
			Service.Log.Info($"[FloorPhase] Used controlled PT incense {stoneId} ({reason}) on floor {dd->Floor}");
			RecordReplayEvent("controlled-incense-used", new { floor = dd->Floor, stoneId, reason });
			return true;
		}

		private void RegisterNaturalRevealDispatch(
			SightResearchRevealResource resource,
			long sightLogSequenceBeforeDispatch,
			long mazerootLogSequenceBeforeDispatch)
		{
			FloorRuntime? runtime = _floorRuntime;
			if (_ctx?.ControlledPtSurvey != null ||
			    runtime == null ||
			    resource == SightResearchRevealResource.None ||
			    resource == SightResearchRevealResource.Mazeroot &&
			    !DungeonCatalog.SupportsNaturalPtStones(runtime.DungeonId))
			{
				return;
			}

			runtime.NaturalRevealDispatched = true;
			runtime.NaturalRevealResource = resource;
			if (resource == SightResearchRevealResource.Mazeroot)
				runtime.NaturalMazerootAttemptedOrAdopted = true;
			runtime.NaturalSightLogSequenceAtDispatch =
				sightLogSequenceBeforeDispatch;
			runtime.NaturalMazerootLogSequenceAtDispatch =
				mazerootLogSequenceBeforeDispatch;
			runtime.NaturalRevealConfirmed = false;
			runtime.NaturalJointScanComplete = false;
			runtime.ObjectEvidence.Invalidate();
		}

		private unsafe bool TryUseNaturalMazeroot(
			InstanceContentDeepDungeon* dd,
			string reason)
		{
			if (_ctx?.ControlledPtSurvey != null ||
			    !DungeonCatalog.SupportsNaturalPtStones(dd->DeepDungeonId) ||
			    !DeepDungeonFloorItemUsePolicy.CanUsePtIncense(
				    dd->DeepDungeonBanId) ||
			    !CanAttemptPomanderUse() ||
			    _pomanderManager.GetStoneCount(2) <= 0)
			{
				return false;
			}
			if (!CanDispatchNaturalPassageOpeningStoneSafely(dd))
			{
				_status = "Waiting to use 敏慧 safely away from the passage";
				return false;
			}

			long sightLogSequenceBeforeDispatch =
				_chatWatchers?.SightLogSequence ?? 0;
			long mazerootLogSequenceBeforeDispatch =
				_chatWatchers?.MazerootLogSequence ?? 0;
			if (!_pomanderManager.UseStone(2))
				return false;

			_pomanderDispatchedThisUpdate = true;
			_nextPomanderUseAt = DateTime.UtcNow.AddSeconds(3);
			_chatWatchers?.MarkSightAttemptedThisFloor();
			RegisterNaturalRevealDispatch(
				SightResearchRevealResource.Mazeroot,
				sightLogSequenceBeforeDispatch,
				mazerootLogSequenceBeforeDispatch);
			Service.Log.Info(
				$"[FloorPhase] Used PT incense 2 ({reason}) on floor {dd->Floor}");
			RecordReplayEvent(
				"incense-used",
				new { floor = dd->Floor, stoneId = 2, reason });
			return true;
		}

		private unsafe bool CanDispatchNaturalPassageOpeningStoneSafely(
			InstanceContentDeepDungeon* dd)
		{
			var player = Service.LocalPlayer;
			if (player == null)
				return false;

			FloorObjectEvidenceSnapshot? evidence =
				_floorRuntime?.ObjectEvidence.Current;
			int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
			int passageRoom = RoomGraph.GetPassageRoomIndex(dd);
			bool roomRelationAvailable =
				playerRoom >= 0 && passageRoom >= 0;
			Vector3 passagePosition = default;
			bool exactPassageAvailable =
				evidence?.Available == true &&
				PassageLocator.TryGetPassageActorPosition(
					evidence,
					out passagePosition);
			float distanceSquared = exactPassageAvailable
				? Vector3.DistanceSquared(
					player.Position,
					passagePosition)
				: 0f;
			ControlledPtDispatchGateAction decision =
				ControlledPtSurveyPolicy.DecidePassageDispatchGate(
					barrierRequired: true,
					roomRelationAvailable,
					roomRelationAvailable && playerRoom == passageRoom,
					exactPassageAvailable,
					distanceSquared);
			return decision == ControlledPtDispatchGateAction.Allow;
		}

		private unsafe bool TryUseNaturalPassageAcceleration(
			InstanceContentDeepDungeon* dd,
			FloorRuntime runtime,
			FloorObjectiveKind primaryObjective)
		{
			if (_ctx?.ControlledPtSurvey != null ||
			    !DungeonCatalog.SupportsNaturalPtStones(dd->DeepDungeonId) ||
			    !DeepDungeonFloorItemUsePolicy.CanUsePtIncense(
				    dd->DeepDungeonBanId) ||
			    primaryObjective != FloorObjectiveKind.ActivatePassage ||
			    _ctx?.Duty.PassageOpen == true)
			{
				return false;
			}

			bool activePairCapture =
				runtime.EvidenceSession?.Bundle.AcquisitionMode ==
				FloorEvidenceAcquisitionMode.AutomaticCommunitySurvey;
			int poisonfruitStock = _pomanderManager.GetStoneCount(1);
			int mazerootStock = _pomanderManager.GetStoneCount(2);
			bool canDispatch = CanAttemptPomanderUse();
			bool passageDispatchSafe =
				canDispatch &&
				CanDispatchNaturalPassageOpeningStoneSafely(dd);
			var action = NaturalPassageAccelerationPolicy.Decide(
				new NaturalPassageAccelerationSnapshot(
					ControlledSurveyActive: _ctx?.ControlledPtSurvey != null,
					PrimaryObjective: primaryObjective,
					ActivePairCapture: activePairCapture,
					JointScanComplete: runtime.NaturalJointScanComplete,
					PassageOpen: _ctx?.Duty.PassageOpen == true,
					PoisonfruitStock: poisonfruitStock,
					PoisonfruitAttemptedThisFloor:
						runtime.NaturalPoisonfruitAttempted,
					MazerootStock: mazerootStock,
					MazerootAttemptedOrAdopted:
						runtime.NaturalMazerootAttemptedOrAdopted,
					CanDispatch: canDispatch,
					PassageDispatchSafe: passageDispatchSafe,
					PtStoneSupported:
						DungeonCatalog.SupportsNaturalPtStones(
							dd->DeepDungeonId),
					PtStoneUsableThisFloor:
						DeepDungeonFloorItemUsePolicy.CanUsePtIncense(
							dd->DeepDungeonBanId)));
			if (action == NaturalPassageAccelerationAction.DispatchMazeroot)
			{
				return TryUseNaturalPassageMazeroot(
					dd,
					runtime,
					"ordinary passage acceleration fallback");
			}

			if (action != NaturalPassageAccelerationAction.DispatchPoisonfruit)
				return false;

			// PomanderManager.UseStone returns true only after it found a matching
			// slot and invoked the native request.  That is the boundary for
			// suppressing Mazeroot; it is not an effect-confirmation signal.
			if (!_pomanderManager.UseStone(1))
				return false;

			runtime.NaturalPoisonfruitAttempted = true;
			_pomanderDispatchedThisUpdate = true;
			_nextPomanderUseAt = DateTime.UtcNow.AddSeconds(3);
			runtime.ObjectEvidence.Invalidate();
			Service.Log.Info(
				$"[FloorPhase] Used PT incense 1 (ordinary passage acceleration) on floor {dd->Floor}");
			RecordReplayEvent(
				"incense-used",
				new
				{
					floor = dd->Floor,
					stoneId = 1,
					reason = "ordinary passage acceleration"
				});
			return true;
		}

		private unsafe bool TryUseNaturalPassageMazeroot(
			InstanceContentDeepDungeon* dd,
			FloorRuntime runtime,
			string reason)
		{
			if (_ctx?.ControlledPtSurvey != null ||
			    runtime.NaturalMazerootAttemptedOrAdopted ||
			    !DungeonCatalog.SupportsNaturalPtStones(dd->DeepDungeonId) ||
			    !DeepDungeonFloorItemUsePolicy.CanUsePtIncense(
				    dd->DeepDungeonBanId) ||
			    !CanAttemptPomanderUse() ||
			    _pomanderManager.GetStoneCount(2) <= 0)
			{
				return false;
			}
			if (!CanDispatchNaturalPassageOpeningStoneSafely(dd))
			{
				_status = "Waiting to use passage accelerator safely away from the passage";
				return false;
			}
			if (!_pomanderManager.UseStone(2))
				return false;

			runtime.NaturalMazerootAttemptedOrAdopted = true;
			_pomanderDispatchedThisUpdate = true;
			_nextPomanderUseAt = DateTime.UtcNow.AddSeconds(3);
			runtime.ObjectEvidence.Invalidate();
			Service.Log.Info(
				$"[FloorPhase] Used PT incense 2 ({reason}) on floor {dd->Floor}");
			RecordReplayEvent(
				"incense-used",
				new { floor = dd->Floor, stoneId = 2, reason });
			return true;
		}

		private unsafe bool EnsureControlledDispatchOutsidePassage(
			InstanceContentDeepDungeon* dd,
			FloorRuntime runtime,
			bool barrierRequired,
			string operation)
		{
			var player = Service.LocalPlayer;
			var evidence = runtime.ObjectEvidence.Current;
			int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
			int passageRoom = RoomGraph.GetPassageRoomIndex(dd);
			bool roomRelationAvailable = playerRoom >= 0 && passageRoom >= 0;
			Vector3 passagePosition = default;
			bool exactPassageAvailable =
				player != null &&
				evidence?.Available == true &&
				PassageLocator.TryGetPassageActorPosition(evidence, out passagePosition);
			float distanceSquared = exactPassageAvailable && player != null
				? Vector3.DistanceSquared(player.Position, passagePosition)
				: 0f;
			var decision = ControlledPtSurveyPolicy.DecidePassageDispatchGate(
				barrierRequired,
				roomRelationAvailable,
				roomRelationAvailable && playerRoom == passageRoom,
				exactPassageAvailable,
				distanceSquared);
			if (decision == ControlledPtDispatchGateAction.Allow)
			{
				if (runtime.ControlledDispatchBarrierActive)
					CancelActiveMovement();
				runtime.ControlledDispatchBarrierActive = false;
				runtime.ControlledDispatchRelocationStarted = false;
				return true;
			}

			runtime.ControlledDispatchBarrierActive = true;
			if (decision == ControlledPtDispatchGateAction.WaitForExactPassage || player == null)
			{
				CancelActiveMovement();
				_status = $"Controlled PT: waiting for exact passage position before {operation}";
				return false;
			}

			var away = player.Position - passagePosition;
			away.Y = 0f;
			if (away.LengthSquared() < 0.01f)
			{
				if (!MapPos.TryGetRoomCenter(dd, playerRoom, out var roomCenter))
				{
					CancelActiveMovement();
					_status = $"Controlled PT: cannot resolve relocation direction before {operation}";
					return false;
				}
				away = roomCenter - passagePosition;
				away.Y = 0f;
				if (away.LengthSquared() < 0.01f)
				{
					CancelActiveMovement();
					_status = $"Controlled PT: passage relocation direction is degenerate before {operation}";
					return false;
				}
			}

			away = Vector3.Normalize(away);
			var destination = passagePosition +
				away * (ControlledPtSurveyPolicy.PassageDispatchExclusionRadius + 1f);
			destination.Y = player.Position.Y;
			if (!runtime.ControlledDispatchRelocationStarted)
			{
				CancelActiveMovement();
				runtime.ControlledDispatchRelocationStarted = true;
			}
			_navDriver?.Drive(destination, player.Position, 0.4f, dd, playerRoom, playerRoom);
			_status = $"Controlled PT: relocating away from passage before {operation}";
			return false;
		}

		private unsafe void TryUseControlledPoisonfruit(
			InstanceContentDeepDungeon* dd,
			FloorRuntime runtime,
			int poisonfruitCount,
			string reason)
		{
			if (runtime.ControlledPoisonfruitDispatched || poisonfruitCount <= 0 || !CanAttemptPomanderUse())
				return;
			if (TryUseControlledStone(1, dd, reason))
				runtime.ControlledPoisonfruitDispatched = true;
		}

		private unsafe void ProcessPendingControlledPostCapturePoisonfruit(
			InstanceContentDeepDungeon* dd,
			FloorRuntime runtime,
			int poisonfruitCount)
		{
			var action = ControlledPtSurveyPolicy.DecidePostCaptureAcceleration(
				runtime.ControlledPendingPostCapturePoisonfruit,
				runtime.ControlledPoisonfruitDispatched,
				_ctx?.Duty.PassageOpen == true,
				poisonfruitCount,
				CanAttemptPomanderUse());
			switch (action)
			{
				case ControlledPtPostCaptureAccelerationAction.Dispatch:
					if (TryUseControlledStone(1, dd, "controlled post-Sight continuation acceleration"))
					{
						runtime.ControlledPoisonfruitDispatched = true;
						runtime.ControlledPendingPostCapturePoisonfruit = false;
					}
					break;
				case ControlledPtPostCaptureAccelerationAction.CompleteWithoutDispatch:
				case ControlledPtPostCaptureAccelerationAction.None:
					runtime.ControlledPendingPostCapturePoisonfruit = false;
					break;
			}
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
			_floorRuntime = new FloorRuntime(
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
						!isBossFloor));
			if (_ctx?.ControlledPtSurvey == null &&
			    runtimeKind == FloorRuntimeKind.Normal)
			{
				_floorRuntime.NaturalRevealInventoryBaselineEstablished = true;
				_floorRuntime.NaturalPreviousSightStock =
					_pomanderManager.GetCount(
						FloorInitPlanner.SightPomanderSlotIndex);
				_floorRuntime.NaturalPreviousMazerootStock =
					DungeonCatalog.SupportsNaturalPtStones(
						dd->DeepDungeonId)
						? _pomanderManager.GetStoneCount(2)
						: 0;
			}
			if (runtimeKind == FloorRuntimeKind.Normal)
			{
				try
				{
					var acquisitionMode = ConsumeFloorEvidenceAcquisitionMode(dd);
					var roomBindings = FloorEvidenceSession.BuildRoomBindings(dd, normalGraph!.ReachableRooms);
					_floorRuntime.EvidenceSession = new FloorEvidenceSession(
						FsdEngineIdentity.InformationalVersion,
						dd->DeepDungeonId,
						dd->Floor,
						Service.ClientState.TerritoryType,
						dd->ActiveLayoutIndex,
						acquisitionMode,
						roomBindings);
					if (_ctx?.ControlledPtSurvey is { } controlled)
					{
						_floorRuntime.EvidenceSession.ConfigureControlledSurvey(
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
			_taskRunner?.Reset();
			_navDriver?.Cancel();
			_chaseHelper.Reset();
			ResetPatrolPlan();
			ResetPermissionBlocks();
			_ctx?.ClearPreferredAggroTarget();
			PlanningState.LastKnownHoardCount = dd->HoardCount;
			_nextPomanderUseAt = DateTime.MinValue;
			_lastGraphPendingFloor = 255;
			_lastGraphPendingDungeonId = 0;
			_lastPassageExitDelayEventAt = DateTime.MinValue;
			_lastChaseAcquisitionFailureKey = string.Empty;
			ResetEngagedTargetProgress();
			bool controlledFirstFloor =
				_ctx?.ControlledPtSurvey != null &&
				!isBossFloor &&
				dd->Floor == ControlledPtSurveyPolicy.FirstFloor;
			_floorRuntime.ControlledIntuitionRequiresCurrentUse =
				controlledFirstFloor ||
				controlledNativeGate == ControlledPtInheritedNativeGateAction.ReactivateWithCurrentUse;
			_chatWatchers?.BeginReadyFloor(
				controlledFirstFloor
					? false
					: !isBossFloor && _nativeIntuitionActive);
			if (!controlledFirstFloor && !isBossFloor && _nativeIntuitionActive)
			{
				_floorRuntime.InheritedIntuitionArmedAtMilliseconds = Environment.TickCount64;
				_floorRuntime.InheritedIntuitionAttemptId =
					_chatWatchers?.ExpectInheritedIntuitionResult(dd->Floor) ?? 0;
			}
			_pt30DivineFavorFlashHelper?.Reset();
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
			if (runtime.EvidenceSession != null)
			{
				try
				{
					_floorEvidenceJournal?.Enqueue(runtime.EvidenceSession.Finalize(reason));
				}
				catch (Exception ex)
				{
					Service.Log.Error($"[FloorEvidenceJournal] Failed to finalize floor {runtime.Floor} generation {runtime.Generation}: {ex}");
				}
			}
			EndHoardEvidenceWait(reason);
			PreemptActiveObjectiveExecutions($"FloorRuntimeDestroyed:{reason}");
			EndActiveWaypointTelemetry(
				RunWaypointTerminalOutcome.Aborted,
				$"FloorRuntimeDestroyed:{reason}");
			CancelActiveMovement();
			ObserveFloorTerminalTelemetry(runtime, reason);
			ObserveFloorTelemetryBoundary(runtime, reason);
			_chatWatchers?.CancelExpectedIntuitionResult(runtime.InheritedIntuitionAttemptId);
			runtime.Dispose();
			_floorRuntime = null;
			_phase = FloorPhase.FloorSetup;
			_chaseHelper.Reset();
			ResetPatrolPlan();
			ResetPermissionBlocks();
			_ctx?.ClearPreferredAggroTarget();
			_nextPomanderUseAt = DateTime.MinValue;
			_lastGraphPendingFloor = 255;
			_lastGraphPendingDungeonId = 0;
			_pt30DivineFavorFlashHelper?.Reset();

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

		private void RequestPlanRefresh(string reason)
		{
			var runtime = _floorRuntime;
			if (runtime == null || runtime.Kind != FloorRuntimeKind.Normal || runtime.IsDisposed)
				return;

			PlanningState.RefreshRequested = true;
			PlanningState.PendingEvidenceVersion++;
			PlanningState.PendingEvidenceFloor = runtime.Floor;
			PlanningState.PendingEvidenceDungeonId = runtime.DungeonId;
			PlanningState.PendingEvidenceReason = reason;
		}

		private void ObserveFloorRuntimeNativeIntuitionEdge(FloorRuntime runtime)
		{
			if (runtime.Kind != FloorRuntimeKind.Normal || runtime.IsDisposed)
				return;

			if (!runtime.NativeIntuitionActive.HasValue)
			{
				runtime.NativeIntuitionActive = _nativeIntuitionActive;
				return;
			}

			bool previous = runtime.NativeIntuitionActive.Value;
			if (!ReadyFloorIntuitionPlanner.ShouldRequestPlanRefresh(previous, _nativeIntuitionActive))
				return;

			runtime.NativeIntuitionActive = _nativeIntuitionActive;
			string reason = _nativeIntuitionActive
				? "native-intuition-activated"
				: "native-intuition-deactivated";
			RequestPlanRefresh(reason);
			RecordReplayEvent("native-intuition-edge", new
			{
				floor = runtime.Floor,
				floorGeneration = runtime.Generation,
				previous,
				current = _nativeIntuitionActive,
				reason
			});
		}

		private void MarkPlanRefreshConsumed(long consumedVersion)
		{
			var runtime = _floorRuntime;
			if (runtime != null &&
			    runtime.Kind == FloorRuntimeKind.Normal &&
			    !runtime.IsDisposed &&
			    runtime.Floor == PlanningState.PendingEvidenceFloor &&
			    runtime.DungeonId == PlanningState.PendingEvidenceDungeonId)
			{
				var acknowledgement = LateHoardEvidencePlanner.AcknowledgeVersion(
					PlanningState.PendingEvidenceVersion,
					PlanningState.ReconciledEvidenceVersion,
					consumedVersion);
				PlanningState.ReconciledEvidenceVersion = acknowledgement.ReconciledVersion;
				PlanningState.RefreshRequested = acknowledgement.RefreshPending;
			}
		}

		private unsafe bool TryReconcileDelayedHoardEvidence(InstanceContentDeepDungeon* dd, FloorRuntime runtime)
		{
			if (runtime.IsDisposed ||
			    runtime.Kind != FloorRuntimeKind.Normal ||
			    _phase != FloorPhase.FloorActive ||
			    HasActiveSearchExecution() ||
			    _executor == null)
			{
				return false;
			}

			var now = DateTime.UtcNow;
			if (now >= PlanningState.NextLateEvidencePollAt)
			{
				PlanningState.NextLateEvidencePollAt = now.Add(GeneralTickInterval);
				if (!RefreshCachedHoardIndicator(dd))
				{
					_status = "Waiting for hoard indicator evidence...";
					return true;
				}
			}

			bool matchesCurrentFloor =
				PlanningState.PendingEvidenceFloor == runtime.Floor &&
				PlanningState.PendingEvidenceDungeonId == runtime.DungeonId;
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
					PlanningState.ReconciledEvidenceVersion = PlanningState.PendingEvidenceVersion;
					PlanningState.RefreshRequested = false;
					RecordReplayEvent("late-hoard-evidence-ignored", new
					{
						floor = runtime.Floor,
						floorGeneration = runtime.Generation,
						dungeonId = runtime.DungeonId,
						evidenceFloor = PlanningState.PendingEvidenceFloor,
						evidenceDungeonId = PlanningState.PendingEvidenceDungeonId,
						evidenceVersion = PlanningState.PendingEvidenceVersion,
						reason = "noncurrent-floor"
					});
				}
				return false;
			}

			var player = Service.LocalPlayer;
			var normalGraph = runtime.NormalGraph;
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
			if (bandedEligible && !BandedChestLocator.TryFindNearestToPlayer(runtime.ObjectEvidence.Current!, out visibleBanded))
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
						floor = runtime.Floor,
						floorGeneration = runtime.Generation,
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
					floor = runtime.Floor,
					floorGeneration = runtime.Generation,
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
					floor = runtime.Floor,
					floorGeneration = runtime.Generation,
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
				floor = runtime.Floor,
				floorGeneration = runtime.Generation,
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

		private unsafe void UpdateFloorSetup(InstanceContentDeepDungeon* dd)
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

			var normalGraph = _floorRuntime?.NormalGraph;
			if (normalGraph == null)
			{
				_status = "Waiting for room graph...";
				return;
			}

			if (_floorRuntime is
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
					PlanningState.LastKnownHoardCount = dd->HoardCount;
					PlanningState.SetupPlanGenerated = true;
				}

				if (_floorRuntime?.ControlledIntuitionResolved != true)
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
				PlanningState.LastKnownHoardCount = dd->HoardCount;
				TryUseFloorInitPomander(dd);
				if (!RefreshCachedHoardIndicator(dd))
				{
					_status = "Waiting for hoard indicator evidence...";
					return;
				}
				long evidenceVersion = PlanningState.PendingEvidenceVersion;
				_executor.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
				MarkPlanRefreshConsumed(evidenceVersion);
				PlanningState.SetupPlanGenerated = _executor.HasPlanningSnapshot;
				if (!PlanningState.SetupPlanGenerated)
				{
					_status = "Waiting for floor planning evidence...";
					return;
				}
				RecordNativeIntuitionState("first-floor-plan-generated", force: true, nativeStateAvailable: true, nativeIntuitionActive: _nativeIntuitionActive);
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

			var inheritedRuntime = _floorRuntime;
			bool inheritedMessage =
				info.Reason is "LogMessage7272" or "LogMessage7273" or
					"LogMessage7272Rejected" or "LogMessage7273Rejected";
			if (inheritedMessage &&
			    info.EvidenceExpectationKind == IntuitionEvidenceExpectationKind.InheritedFloorResult &&
			    inheritedRuntime is { IsDisposed: false } &&
			    inheritedRuntime.Kind == FloorRuntimeKind.Normal &&
			    inheritedRuntime.InheritedIntuitionDecision == null &&
			    inheritedRuntime.InheritedIntuitionAttemptId == info.EvidenceAttemptId &&
			    inheritedRuntime.Floor == info.EvidenceTargetFloor)
			{
				inheritedRuntime.InheritedIntuitionEvidence = !info.EvidenceAccepted
					? InheritedIntuitionEvidenceKind.Rejected
					: info.Reason == "LogMessage7272"
						? InheritedIntuitionEvidenceKind.HoardPresent
						: InheritedIntuitionEvidenceKind.NoHoard;
			}

			RecordReplayEvent("chat-watchers-state", info);
			uint semanticMessageId = info.Reason switch
			{
				"LogMessage7272" or "LogMessage7272Rejected" => 7272,
				"LogMessage7273" or "LogMessage7273Rejected" => 7273,
				"LogMessage7274" or "LogMessage7274Rejected" => 7274,
				_ => 0
			};
			if (semanticMessageId != 0)
				_floorRuntime?.EvidenceSession?.ObserveSemanticMessage(semanticMessageId, info.EvidenceAccepted);

			switch (info.Reason)
			{
				case "GoldChestOvercapObserved":
					HandleGoldChestOvercapObserved(info.GoldChestOvercapSlotIndex);
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

		private void ResolveInheritedIntuition(FloorRuntime runtime)
		{
			if (runtime.Kind != FloorRuntimeKind.Normal ||
			    runtime.IsDisposed ||
			    runtime.InheritedIntuitionAttemptId <= 0 ||
			    runtime.InheritedIntuitionDecision.HasValue)
			{
				return;
			}

			int elapsedMilliseconds = (int)Math.Clamp(
				Environment.TickCount64 - runtime.InheritedIntuitionArmedAtMilliseconds,
				0L,
				int.MaxValue);
			var decision = InheritedIntuitionResolutionPlanner.Decide(
				runtime.InheritedIntuitionEvidence,
				elapsedMilliseconds,
				CurrentIntuitionResolutionWindowMilliseconds);
			if (!decision.Terminal)
				return;

			_chatWatchers?.CancelExpectedIntuitionResult(runtime.InheritedIntuitionAttemptId);
			runtime.InheritedIntuitionDecision = decision;
			runtime.EvidenceSession?.ObserveInheritedIntuitionResolution(
				decision.Source,
				decision.ElapsedMilliseconds,
				CurrentIntuitionResolutionWindowMilliseconds);

			if (decision.NoHoard)
			{
				_executor?.MarkInheritedNoHoardInferred();
				if (HandleNoHoardEvidenceInvalidated("inherited-no-hoard-inferred"))
					RequestPlanRefresh("inherited-no-hoard-inferred");
			}
			else if (decision.HoardPresent)
			{
				runtime.ObjectEvidence.Invalidate();
				RequestPlanRefresh("inherited-hoard-present");
			}
			else if (decision.IsError)
			{
				Service.Log.Error(
					$"[FloorPhase] Inherited Intuition protocol anomaly on floor {runtime.Floor}: {decision.Source}.");
			}

			RecordReplayEvent("inherited-intuition-resolved", new
			{
				floor = runtime.Floor,
				floorGeneration = runtime.Generation,
				dungeonId = runtime.DungeonId,
				attemptId = runtime.InheritedIntuitionAttemptId,
				source = decision.Source.ToString(),
				decision.HoardPresent,
				decision.NoHoard,
				decision.IsError,
				decision.ElapsedMilliseconds,
				windowMilliseconds = CurrentIntuitionResolutionWindowMilliseconds
			});
			EndHoardEvidenceWait($"inherited-intuition:{decision.Source}");
		}

		private unsafe bool ResolveCurrentFloorIntuitionTimeoutIfNeeded(InstanceContentDeepDungeon* dd)
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
				ResolutionWindowMilliseconds = CurrentIntuitionResolutionWindowMilliseconds
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
				windowMilliseconds = CurrentIntuitionResolutionWindowMilliseconds,
				reason = "authoritative-7272-or-7273-still-required"
			});
			return false;
		}

		private bool HandleNoHoardEvidenceInvalidated(string reason)
		{
			var currentPlanEntry = _executor?.CurrentPlanEntry;
			bool activeChestObjective =
				_floorRuntime?.ActiveExecution?.ObjectiveRecords.Any(
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

		private object BuildPlanReplayPayload(byte floor, string reason, int? roomIndex = null)
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

		private unsafe void ObserveCurrentRoom(InstanceContentDeepDungeon* dd)
		{
			var runtime = _floorRuntime;
			if (dd == null ||
			    runtime == null ||
			    runtime.IsDisposed ||
			    runtime.Floor != dd->Floor ||
			    runtime.DungeonId != dd->DeepDungeonId)
				return;

			int roomIndex = RoomGraph.GetLocalPlayerRoomIndex(dd);
			if (roomIndex < 0)
				return;

			runtime.EvidenceSession?.ObserveRoomVisit(roomIndex);
			if (roomIndex == runtime.LastObservedRoomIndex)
				return;

			runtime.LastObservedRoomIndex = roomIndex;
			if (runtime.ActiveRoomNavigationTarget == roomIndex)
			{
				runtime.ActiveRoomNavigationTarget = null;
			}

			RecordReplayEvent("room-entered", new
			{
				floor = dd->Floor,
				floorGeneration = runtime.Generation,
				roomIndex,
				phase = _phase.ToString(),
				status = _status
			});
		}

		private void RecordReplayEvent(string eventType, object data)
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

		private enum FloorRuntimeKind
		{
			Normal,
			Boss
		}

		private sealed class FloorPlanningState
		{
			public DateTime LastGeneralTickAt = DateTime.MinValue;
			public int LastKnownHoardCount;
			public bool RefreshRequested;
			public long PendingEvidenceVersion;
			public long ReconciledEvidenceVersion;
			public byte PendingEvidenceFloor = 255;
			public uint PendingEvidenceDungeonId;
			public string PendingEvidenceReason = string.Empty;
			public DateTime NextLateEvidencePollAt = DateTime.MinValue;
			public bool SetupPlanGenerated;
		}

		private sealed class FloorRuntime : IDisposable
		{
			private readonly Dictionary<RoomObjectiveKey, long> _objectiveIds = new();
			private long _nextObjectiveId;
			private ObjectiveArbiterSnapshot _objectiveInput;
			private long _objectiveEvidenceVersion;
			private long _objectiveOptionsVersion;
			private long _objectiveLedgerVersion;
			private bool _hasObjectiveInput;

			public FloorRuntime(
				long generation,
				uint dungeonId,
				byte floor,
				FloorRuntimeKind kind,
				DateTime readyAtUtc,
				NormalFloorGraphSnapshot? normalGraph,
				bool? nativeIntuitionActive,
				DetailedMapRunSnapshot detailedMap,
				RunFloorTelemetryTrace? runTelemetry)
			{
				Generation = generation;
				DungeonId = dungeonId;
				Floor = floor;
				Kind = kind;
				ReadyAtUtc = readyAtUtc;
				NormalGraph = normalGraph;
				ObjectiveLedger = new FloorObjectiveLedger(generation);
				Executor = kind == FloorRuntimeKind.Normal
					? new AutoPilotExecutor(GetRoomProgress, detailedMap)
					: null;
				NativeIntuitionActive = nativeIntuitionActive;
				ObjectEvidence = new FloorObjectEvidenceTracker();
				RunTelemetry = runTelemetry;
			}

			public long Generation { get; }
			public uint DungeonId { get; }
			public byte Floor { get; }
			public FloorRuntimeKind Kind { get; }
			public DateTime ReadyAtUtc { get; }
			public NormalFloorGraphSnapshot? NormalGraph { get; private set; }
			public AutoPilotExecutor? Executor { get; }
			public bool? NativeIntuitionActive { get; set; }
			public FloorObjectiveLedger ObjectiveLedger { get; }
			public ObjectiveExecution? ActiveExecution { get; private set; }
			public FloorPlanningState PlanningState { get; } = new();
			public PendingIntuitionState PendingIntuition { get; } = new();
			public bool BossNavigationResolved { get; set; }
			public FloorSearchState SearchState { get; } = new();
			public FloorObjectEvidenceTracker ObjectEvidence { get; }
			public RunFloorTelemetryTrace? RunTelemetry { get; }
			public RunFloorStateCumulativePublisher? RunFloorStatePublisher { get; set; }
			public int LastObservedRoomIndex { get; set; } = -1;
			public int? ActiveRoomNavigationTarget { get; set; }
			public BandedRevealExpectation? BandedRevealExpectation { get; set; }
			public FloorEvidenceSession? EvidenceSession { get; set; }
			public bool SightResearchDispatched { get; set; }
			public bool NaturalRevealInventoryBaselineEstablished { get; set; }
			public int NaturalPreviousSightStock { get; set; }
			public int NaturalPreviousMazerootStock { get; set; }
			public long NaturalPreviousSightLogSequence { get; set; }
			public long NaturalPreviousMazerootLogSequence { get; set; }
			public bool NaturalRevealDispatched { get; set; }
			public SightResearchRevealResource NaturalRevealResource { get; set; }
			public long NaturalSightLogSequenceAtDispatch { get; set; }
			public long NaturalMazerootLogSequenceAtDispatch { get; set; }
			public bool NaturalRevealConfirmed { get; set; }
			public long NaturalRevealConfirmationRefreshSequence { get; set; }
			public long NaturalRevealConfirmationFullScanCount { get; set; }
			public bool NaturalCandidateUniverseResolved { get; set; }
			public RawWorldPosition[] NaturalCandidateUniverse { get; set; } = [];
			public HashSet<ControlledTrapWitnessKey> NaturalObservedTrapWitnesses { get; } = [];
			public float NaturalMaximumTrapWitnessDistance { get; set; }
			public bool NaturalJointScanComplete { get; set; }
			public bool NaturalPoisonfruitAttempted { get; set; }
			public bool NaturalMazerootAttemptedOrAdopted { get; set; }
			public bool ControlledSightDispatched { get; set; }
			public long ControlledSightLogSequenceAtDispatch { get; set; }
			public long ControlledMazerootLogSequenceAtDispatch { get; set; }
			public ControlledPtSurveyItemAction ControlledCaptureItem { get; set; }
			public DateTime ControlledSightDispatchedAt { get; set; }
			public bool ControlledStrengthHandled { get; set; }
			public bool ControlledPoisonfruitDispatched { get; set; }
			public bool ControlledPendingPostCapturePoisonfruit { get; set; }
			public bool ControlledOpportunityCompleted { get; set; }
			public bool ControlledPositiveCapturePending { get; set; }
			public int ControlledHoardRoomIndex { get; set; } = -1;
			public Vector3 ControlledHoardPosition { get; set; }
			public bool ControlledHoardPositionResolved { get; set; }
			public bool ControlledSightConfirmed { get; set; }
			public long ControlledSightConfirmedAtMilliseconds { get; set; }
			public long ControlledSightConfirmationRefreshSequence { get; set; }
			public long ControlledSightConfirmationFullScanCount { get; set; }
			public bool ControlledCandidateUniverseResolved { get; set; }
			public RawWorldPosition[] ControlledCandidateUniverse { get; set; } = [];
			public bool ControlledHoardRoomTargetReached { get; set; }
			public long ControlledHoardRoomTargetRefreshSequence { get; set; }
			public long ControlledHoardRoomTargetFullScanCount { get; set; }
			public HashSet<ControlledTrapWitnessKey> ControlledObservedTrapWitnesses { get; } = [];
			public float ControlledMaximumTrapWitnessDistance { get; set; }
			public long ControlledIntuitionExpectationStartedAtMilliseconds { get; set; }
			public long ControlledIntuitionExpectationAttemptId { get; set; }
			public bool ControlledIntuitionRequiresCurrentUse { get; set; }
			public bool ControlledIntuitionResolved { get; set; }
			public bool ControlledIntuitionResolutionPending { get; set; }
			public ControlledPtIntuitionResolutionDecision? ControlledIntuitionDecision { get; set; }
			public long InheritedIntuitionArmedAtMilliseconds { get; set; }
			public long InheritedIntuitionAttemptId { get; set; }
			public InheritedIntuitionEvidenceKind InheritedIntuitionEvidence { get; set; }
			public InheritedIntuitionResolutionDecision? InheritedIntuitionDecision { get; set; }
			public bool ControlledPositiveMessagePendingIndicator { get; set; }
			public int ControlledIndicatorRoomCursor { get; set; }
			public bool ControlledDispatchBarrierActive { get; set; }
			public bool ControlledDispatchRelocationStarted { get; set; }
			public ObjectiveArbiterDecision ObjectiveDecision { get; private set; }
			public bool HasObjectiveDecision { get; private set; }
			public bool IsDisposed { get; private set; }

			public void ReplaceObjectiveExecution(FloorObjectiveKind objective, NavigationHelper navigation)
			{
				ActiveExecution?.Dispose();
				ActiveExecution = objective == FloorObjectiveKind.None
					? null
					: new ObjectiveExecution(objective, navigation);
			}

			private (bool HoardSearched, bool ChestsSearched, bool IntelVisited) GetRoomProgress(int roomIndex)
			{
				bool hoardSearched = false;
				bool chestsSearched = false;
				bool intelVisited = false;
				foreach (var pair in _objectiveIds)
				{
					if (pair.Key.RoomIndex != roomIndex || !ObjectiveLedger.TryGetObjective(pair.Value, out var objective))
						continue;

					switch (pair.Key.Category)
					{
						case RoomObjectiveCategory.Hoard:
							hoardSearched |= objective.Outcome == ObjectiveOutcomeKind.Succeeded;
							break;
						case RoomObjectiveCategory.Chests:
							chestsSearched |= objective.Outcome is ObjectiveOutcomeKind.Succeeded or ObjectiveOutcomeKind.Skipped;
							break;
						case RoomObjectiveCategory.Intel:
							intelVisited |= objective.Outcome == ObjectiveOutcomeKind.Succeeded;
							break;
					}
				}
				return (hoardSearched, chestsSearched, intelVisited);
			}

			public ObjectiveRecord GetOrCreateObjective(
				RoomObjectiveKey key,
				FloorObjectiveKind kind,
				bool required)
			{
				if (!_objectiveIds.TryGetValue(key, out long objectiveId))
				{
					objectiveId = ++_nextObjectiveId;
					_objectiveIds.Add(key, objectiveId);
					ObjectiveLedger.AddObjective(objectiveId, kind, required);
				}

				if (!ObjectiveLedger.TryGetObjective(objectiveId, out var objective))
					throw new InvalidOperationException($"Floor objective {objectiveId} is missing from generation {Generation} ledger.");
				return objective;
			}

			public bool SetObjectiveDecision(ObjectiveArbiterDecision decision)
			{
				if (IsDisposed ||
				    HasObjectiveDecision &&
				    ObjectiveDecision == decision)
					return false;

				ObjectiveDecision = decision;
				HasObjectiveDecision = true;
				return true;
			}

			public bool RefreshObjectiveDecision(
				ObjectiveArbiterSnapshot input,
				long evidenceVersion,
				long optionsVersion,
				long ledgerVersion,
				out ObjectiveArbiterDecision decision)
			{
				decision = ObjectiveDecision;
				if (IsDisposed ||
				    _hasObjectiveInput &&
				    _objectiveInput == input &&
				    _objectiveEvidenceVersion == evidenceVersion &&
				    _objectiveOptionsVersion == optionsVersion &&
				    _objectiveLedgerVersion == ledgerVersion)
				{
					return false;
				}

				_objectiveInput = input;
				_objectiveEvidenceVersion = evidenceVersion;
				_objectiveOptionsVersion = optionsVersion;
				_objectiveLedgerVersion = ledgerVersion;
				_hasObjectiveInput = true;
				decision = ObjectiveArbiter.Decide(input);
				return SetObjectiveDecision(decision);
			}

			public void ClearObjectiveDecision()
			{
				ObjectiveDecision = default;
				HasObjectiveDecision = false;
				_hasObjectiveInput = false;
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
				EvidenceSession = null;
				_objectiveIds.Clear();
				ActiveExecution?.Dispose();
				ActiveExecution = null;
				ObjectEvidence.Dispose();
				RunFloorStatePublisher = null;
				ClearObjectiveDecision();
			}
		}

		private sealed class PendingIntuitionState
		{
			private bool _hasPending;
			private byte _sourceFloor;
			private DateTime _lastUsedAtUtc = DateTime.MinValue;
			private long _attemptId;
			private bool _overdueRecorded;

			public void Reset()
			{
				_hasPending = false;
				_sourceFloor = 0;
				_lastUsedAtUtc = DateTime.MinValue;
				_attemptId = 0;
				_overdueRecorded = false;
			}

			public void MarkUsed(byte sourceFloor, DateTime usedAtUtc, long attemptId)
			{
				_hasPending = true;
				_sourceFloor = sourceFloor;
				_lastUsedAtUtc = usedAtUtc;
				_attemptId = attemptId;
				_overdueRecorded = false;
			}

			public void CancelAttempt(long attemptId)
			{
				if (!_hasPending || attemptId == 0 || attemptId != _attemptId)
					return;

				Reset();
			}

			public bool TryMarkResolved(long attemptId)
			{
				if (!_hasPending || attemptId == 0 || attemptId != _attemptId)
					return false;

				Reset();
				return true;
			}

			public bool TryMarkOverdueRecorded()
			{
				if (!_hasPending || _overdueRecorded)
					return false;

				_overdueRecorded = true;
				return true;
			}

			public bool TryGetCurrentFloorUseElapsedMilliseconds(byte floor, DateTime nowUtc, out int elapsedMilliseconds)
			{
				elapsedMilliseconds = 0;
				if (!_hasPending ||
				    _sourceFloor != floor ||
				    _lastUsedAtUtc == DateTime.MinValue)
				{
					return false;
				}

				elapsedMilliseconds = (int)Math.Max(0, (nowUtc - _lastUsedAtUtc).TotalMilliseconds);
				return true;
			}
		}

		private unsafe void UpdateBossFloor(InstanceContentDeepDungeon* dd)
		{
			var player = Service.LocalPlayer;
			if (player == null)
			{
				_status = "Boss floor - waiting for player";
				return;
			}

			if (Service.Condition[ConditionFlag.BetweenAreas] ||
			    Service.Condition[ConditionFlag.BetweenAreas51])
			{
				_status = "Boss floor - waiting for floor load to complete";
				_navHelper?.Cancel();
				_pt30DivineFavorFlashHelper?.Reset();
				return;
			}
			if (!RequireMovementPermission("boss objective", FloorObjectiveKind.DefeatBoss))
			{
				_pt30DivineFavorFlashHelper?.Reset();
				return;
			}

			_pt30DivineFavorFlashHelper?.Update(dd);

			if (Service.Condition[ConditionFlag.InCombat])
			{
				if (!BossNavigationResolved)
				{
					_navHelper?.Cancel();
					BossNavigationResolved = true;
					Service.Log.Info("[FloorPhase] Boss combat started -> canceling boss navigation");
				}

				_status = _pt30DivineFavorFlashHelper?.IsDivineFavorMovementActive == true
					? "Boss floor - PT30 Divine Favor movement override active"
					: "Boss floor - combat assist active";
				return;
			}

			var boss = Runtime.Helpers.CombatTargetingHelpers.PickNearestHostile(60f, out _);
			if (boss == null)
			{
				if (_pt30DivineFavorFlashHelper?.TryUpdateBossEngageMovement(dd, player.Position, out var pt30EngageStatus) == true)
				{
					_navHelper?.Cancel();
					_status = pt30EngageStatus;
					return;
				}

				_status = "Boss floor - waiting for boss target";
				return;
			}

			if (BossNavigationResolved)
			{
				var currentTarget = Service.TargetManager.Target as IBattleChara;
				if (currentTarget != null &&
				    currentTarget.GameObjectId == boss.GameObjectId &&
				    !currentTarget.IsDead &&
				    IsWithinBossEngageRadius(player.Position, boss.Position))
				{
					_status = "Boss floor - waiting for boss combat";
					return;
				}

				BossNavigationResolved = false;
				RecordReplayEvent("boss-navigation-reset", new
				{
					floor = dd->Floor,
					reason = "combat-ended-or-target-lost",
					bossId = boss.GameObjectId,
					currentTargetId = currentTarget?.GameObjectId ?? 0
				});
			}

			if (_pt30DivineFavorFlashHelper?.TryUpdateBossEngageMovement(dd, player.Position, out var pt30BossEngageStatus) == true)
			{
				_navHelper?.Cancel();
				_status = pt30BossEngageStatus;
				return;
			}

			var withinRange = IsWithinBossEngageRadius(player.Position, boss.Position);
			if (withinRange)
			{
				_navHelper?.Cancel();
				BossNavigationResolved = true;
				_status = "Boss floor - at boss";
				Service.Log.Info("[FloorPhase] Boss navigation complete -> in engage range");
				return;
			}

			var state = _navHelper!.Navigate(boss.Position, player.Position, BossNavigationArrivalTolerance, retryIntervalSeconds: 5.0);
			switch (state)
			{
				case NavigationState.Moving:
					_status = "Boss floor - navigating to boss";
					break;
				case NavigationState.Arrived:
					BossNavigationResolved = true;
					_status = "Boss floor - at boss";
					Service.Log.Info("[FloorPhase] Boss navigation complete -> arrived");
					break;
				case NavigationState.StuckRepathing:
					_status = $"Boss floor - repathing to boss ({_navHelper.StuckRetryCount}/3)";
					break;
				case NavigationState.StuckGiveUp:
					BossNavigationResolved = true;
					_status = "Boss floor - boss navigation failed";
					Service.Log.Warning("[FloorPhase] Boss navigation gave up");
					break;
				case NavigationState.Failed:
					BossNavigationResolved = true;
					_status = "Boss floor - boss navigation unavailable";
					Service.Log.Warning("[FloorPhase] Boss navigation failed to start");
					break;
			}
		}

		private static bool IsWithinBossEngageRadius(Vector3 playerPosition, Vector3 bossPosition)
		{
			var dx = playerPosition.X - bossPosition.X;
			var dz = playerPosition.Z - bossPosition.Z;
			return dx * dx + dz * dz <=
			       BossNavigationArrivalTolerance * BossNavigationArrivalTolerance;
		}

		private unsafe bool TryMarkPlayerDeathFatal(InstanceContentDeepDungeon* dd)
		{
			var player = Service.LocalPlayer;
			if (player == null || !player.IsDead)
				return false;

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
				LeaveMode = source.LeaveMode,
				LeaveAfterMinutes = source.LeaveAfterMinutes,
				RequireValidatedAbandonPrompt = source.RequireValidatedAbandonPrompt
			};
		}

		private unsafe NavDriveResult NavigateToRoom(InstanceContentDeepDungeon* dd, int targetRoom, global::Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
		{
			var runtime = _floorRuntime;
			if (runtime == null ||
			    runtime.IsDisposed ||
			    runtime.Floor != dd->Floor ||
			    runtime.DungeonId != dd->DeepDungeonId)
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
					if (runtime.ActiveRoomNavigationTarget != targetRoom)
					{
						runtime.ActiveRoomNavigationTarget = targetRoom;
						RecordReplayEvent("room-navigation-started", new
						{
							floor = dd->Floor,
							floorGeneration = runtime.Generation,
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
					if (runtime.ActiveRoomNavigationTarget != targetRoom)
					{
						runtime.ActiveRoomNavigationTarget = targetRoom;
						RecordReplayEvent("room-navigation-started", new
						{
							floor = dd->Floor,
							floorGeneration = runtime.Generation,
							fromRoom = playerRoom,
							targetRoom,
							mode = "Staging",
							stage = _navDriver.StageLabel
						});
					}
					_status = $"Staging: {_navDriver.StageLabel}";
					break;
				case NavDriveResult.Arrived:
					runtime.ActiveRoomNavigationTarget = null;
					break;
				case NavDriveResult.StuckRetrying:
					runtime.RunTelemetry?.ObserveNavigationIssue();
					_status = $"Repathing ({_navDriver.StuckRetryCount}/3)";
					break;
				case NavDriveResult.Failed:
					runtime.RunTelemetry?.ObserveNavigationIssue();
					runtime.ActiveRoomNavigationTarget = null;
					Service.Log.Warning($"[FloorPhase] Room {targetRoom} navigation failed");
					_navDriver.Cancel();
					break;
			}
			return result;
		}

		private void HandleDirectNavState(NavigationState state, string context)
		{
			switch (state)
			{
				case NavigationState.Arrived:
					_status = $"{context}: arrived";
					break;
				case NavigationState.StuckRepathing:
					_floorRuntime?.RunTelemetry?.ObserveNavigationIssue();
					_status = $"{context}: stuck ({_navHelper?.StuckRetryCount ?? 0}/3)";
					break;
				case NavigationState.StuckGiveUp:
					_floorRuntime?.RunTelemetry?.ObserveNavigationIssue();
					_status = $"{context}: failed";
					break;
				case NavigationState.Failed:
					_floorRuntime?.RunTelemetry?.ObserveNavigationIssue();
					_status = $"{context}: navigation unavailable";
					break;
			}
		}
	}
}
