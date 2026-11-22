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
        private TerminalRoomController? _terminalRooms;
		private readonly IFloorEvidenceObserver? _floorEvidenceObserver;
		private readonly IRunTelemetryObserver? _runTelemetryObserver;
        private readonly Func<IBossMovementStrategy>? _spinBossMovementFactory;
		private readonly NativeDeepDungeonLogMessageSource _logMessageSource;
		private NavigationHelper? _navHelper;
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
		private string _status = "Idle";
		private bool _wasTransitioning;
		private byte _lastGraphPendingFloor = 255;
		private uint _lastGraphPendingDungeonId;
		private NativeIntuitionState? _lastNativeIntuitionState;
		private bool _nativeIntuitionActive;
		private bool _nativeIntuitionSampleAvailable;
		private bool? _lastNativeIntuitionReadAvailable;
		private DateTime _nextNativeIntuitionPollAt = DateTime.MinValue;
		private DateTime _lastObjectEvidenceTelemetryAt = DateTime.MinValue;
		private DateTime _lastObjectEvidenceUnavailableAt = DateTime.MinValue;
		private static readonly TimeSpan NativeIntuitionPollInterval = TimeSpan.FromMilliseconds(250);
		private readonly record struct NativeIntuitionState(bool IsActive, int Count, bool IsUsable);
		public FloorPhase CurrentPhase => _phase;
        internal Vector3? CameraChestPosition => _floorRuntime?.CameraChestPosition;
        internal bool UsesEncounterCoordination =>
            _floorRuntime is { IsDisposed: false, Boss.UsesSpinStrategy: false, UsesEncounterCoordination: true };
        internal bool BossRequestsNorthCamera => _floorRuntime?.Boss?.RequestsNorthCamera == true;
        internal object? BossMovementSnapshot => _floorRuntime is { Kind: FloorRuntimeKind.Boss, IsDisposed: false } runtime
            ? new { strategy = runtime.Boss.MovementStrategyName, running = runtime.Boss.MovementStarted,
                northCamera = runtime.Boss.RequestsNorthCamera, combatReady = runtime.Boss.AllowsCombatAssist }
            : null;
        public void SuspendBossMovement() => _floorRuntime?.Boss?.SuspendMovement();

        internal void SuspendForMissingDutyObservation()
        {
            _observingInheritance = false;
            _nativeIntuitionSampleAvailable = false;
            _nextNativeIntuitionPollAt = DateTime.MinValue;
            _floorRuntime?.Survey.CancelInheritedObservation();
            CancelActiveMovement();
        }
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
			IRunTelemetryObserver? runTelemetryObserver = null,
            Func<IBossMovementStrategy>? spinBossMovementFactory = null)
		{
			_logMessageSource = logMessageSource ?? throw new ArgumentNullException(nameof(logMessageSource));
			_floorEvidenceObserver = floorEvidenceObserver;
			_runTelemetryObserver = runTelemetryObserver;
            _spinBossMovementFactory = spinBossMovementFactory;
		}

        private (uint Dungeon, byte Floor) _intuitionScope;
        private bool _observingInheritance;
        private bool? _earlyIntuitionResult;
        private long _intuitionScopeStartedAt;
        private bool _intuitionScopeDuringTransition;

		public void Initialize(RunContext context)
		{
			Dispose();
			_ctx = context;
            _intuitionScope = default;
            _earlyIntuitionResult = null;
            _intuitionScopeDuringTransition = false;
            _observingInheritance = false;
			context.RecordDiagnostic = RecordReplayEvent;
			_chaseHelper.CanSelectNewTarget = context.CanSelectNewCombatTarget;
			_navHelper = new NavigationHelper(_ctx.Navigator);
            _terminalRooms = new TerminalRoomController(context, _navHelper, status => _status = status,
                RecordReplayEvent, DestroyFloorRuntime, ResolvePassageWalkingPosition);
			DisposeChatWatchers();
			_chatWatchers = new ChatWatchers(_logMessageSource);
			_chatWatchers.StateChanged += OnChatWatchersStateChanged;
			CloseRunRecording("controller-reinitialized");
			_chaseHelper.Reset();
			_phase = FloorPhase.FloorSetup;
			_wasTransitioning = false;
			_lastGraphPendingFloor = 255;
			_lastGraphPendingDungeonId = 0;
			_lastNativeIntuitionState = null;
			_nativeIntuitionActive = false;
			_nativeIntuitionSampleAvailable = false;
			_lastNativeIntuitionReadAvailable = null;
			_nextNativeIntuitionPollAt = DateTime.MinValue;
			_lastObjectEvidenceTelemetryAt = DateTime.MinValue;
			_lastObjectEvidenceUnavailableAt = DateTime.MinValue;
			_floorRuntime?.ResetPermissionBlocks();
			_floorRuntime?.ResetEngagedTargetProgress();
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
			if (_ctx == null || _terminalRooms == null)
				return;

			if (!_ctx.Duty.IsInDuty)
			{
				DestroyFloorRuntime(0, "outside-duty");
                _intuitionScope = default;
                _nativeIntuitionSampleAvailable = false;
				return;
			}

			var efw = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.Instance();
			if (efw == null)
			{
                SuspendForMissingDutyObservation();
				_floorRuntime?.Objectives.ClearObjectiveDecision();
				return;
			}
			var dd = efw->GetInstanceContentDeepDungeon();
			if (dd == null)
			{
                SuspendForMissingDutyObservation();
				_floorRuntime?.Objectives.ClearObjectiveDecision();
				return;
			}

			try
			{
				bool isTransitioning = _ctx.Duty.IsTransitioning || dd->Floor == 0;
				ObserveDutyTransitionState(dd->Floor, isTransitioning);
				if (TryMarkPlayerDeathFatal(dd))
				{
					if (_ctx.FarmingPlan == null)
						DestroyFloorRuntime(dd->Floor, "player-death");
					return;
				}

				if (isTransitioning)
				{
                    if (!_intuitionScopeDuringTransition) _observingInheritance = false;
					DestroyFloorRuntime(dd->Floor, "transitioning");
					_status = "Waiting for map to load...";
					_navHelper?.Cancel();
					return;
				}

                // The native floor identifies evidence before the player position
                // is stable enough for movement.
                ObserveIntuitionScope(dd);
                _intuitionScopeDuringTransition = false;
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
                if (_terminalRooms.TryUpdatePt99ResultTransfer(dd)) return;
				bool nativeIntuitionActive = false;
				bool nativeStateAvailable = !requiresIntuitionState ||
				                            TryGetNativeIntuitionState(out nativeIntuitionActive);
                if (!nativeStateAvailable)
                    _observingInheritance = false;

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
                if (!_observingInheritance)
                    activeRuntime.Survey.CancelInheritedObservation();
				activeRuntime.Survey.ResolveInheritedIntuition();
                if (activeRuntime.Survey.InheritedIntuitionDecision.HasValue) _observingInheritance = false;
				activeRuntime.ObserveFloorRuntimeNativeIntuitionEdge();

				// Knowledge ingest: once per outer update, after shared
				// native/evidence/inherited resolution and BEFORE policy,
				// harvest and objective decisions — never inside the
				// coord decision paths themselves.
				if (activeRuntime.UsesSiteCoordination)
					activeRuntime.IngestKnowledge(dd);

				// Observation -> work decision -> completion check -> exec:
				// the site scheduler commits THIS update's selected work
				// before harvest/objective decisions and the pipeline tick.
				if (activeRuntime.UsesSiteCoordination &&
				    _phase == FloorPhase.FloorActive)
					activeRuntime.PrepareNormalDecision(dd);

                if (_ctx.StatusIsError) return;
                if (activeRuntime.TryFinishHarvest(dd)) return;
                if (activeRuntime.TryUseHohMagicite(dd))
                {
                    CancelActiveMovement();
                    return;
                }
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
                        if (activeRuntime.Boss.MovementFailure is { } movementFailure && _ctx != null)
                        {
                            _ctx.StatusLine = movementFailure;
                            _ctx.StatusIsError = true;
                        }
						break;
					case FloorPhase.Done:
						break;
				}

				activeRuntime.RefreshObjectiveDecision(dd);
			}
			catch (Exception ex)
			{
                SuspendBossMovement();
				_floorRuntime?.Objectives.ClearObjectiveDecision();
				Service.Log.Error($"[FloorPhase] Update error: {ex.Message}\n{ex.StackTrace}");
			}
		}

		public void Dispose()
		{
			DestroyFloorRuntime(0, "controller-disposed");
            _terminalRooms = null;
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
						ControlledSurvey: false));
			}
			catch (Exception ex)
			{
				Service.Log.Error($"[RunTelemetry] Host run-recording observer failed: {ex}");
			}
		}

		public void CancelActiveMovement()
		{
            SuspendBossMovement();
			_floorRuntime?.CancelCoordMovement();
			bool navigationActive = _navHelper?.HasActiveTarget == true;
			if (navigationActive)
				_navHelper?.Cancel();
		}

		public bool TickCombatChannel(out string status)
		{
			status = string.Empty;
			var context = _ctx;
			if (_floorRuntime is { Kind: FloorRuntimeKind.Boss } bossRuntime && !bossRuntime.Boss.AllowsCombatAssist)
			{
				context?.CombatAssist.Suspend();
				status = _status;
				return false;
			}
			if (context == null || context.FarmingPlan?.ReusesSave == true || !AllowsCombatChannel)
				return false;

			context.CombatAssist.Tick(
				context,
				context.Duty.IsBossFloor,
				context.Duty.PassageOpen,
				out status);
			return true;
		}

        public Vector3? CachedHoardIndicatorPos => _ctx?.Knowledge.Floor?.ExactHoardPosition;

        public IReadOnlyList<Vector3> ObservedSightTrapPositions =>
            _floorRuntime?.ObservedSightTrapPositions ?? Array.Empty<Vector3>();

        internal FloorWorkDebugSnapshot? GetDebugSnapshot() =>
            _floorRuntime?.GetDebugSnapshot();

		private unsafe bool RefreshFloorObjectEvidence(InstanceContentDeepDungeon* dd, FloorExplorationController runtime)
		{
			var refresh = runtime.ObjectEvidence.RefreshIfDue(runtime.DungeonId);
			if (refresh.Attempted)
			{
				runtime.ObserveCurrentRoom(dd);
				runtime.Survey.ObserveFloorEvidence(dd);
				runtime.PublishAuthoritativeRunFloorStateIfChanged(dd);
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
					passageActorCount = snapshot?.PassageActors.Count ?? 0,
					passageActors = refresh.MaterialChanged ? snapshot?.PassageActors.Select(actor => new
					{
						actor.EntityId, actor.GameObjectId, actor.BaseId, actor.IsTargetable, actor.ObjectKind, actor.SharedTimelineState,
						position = new { x = actor.Position.X, y = actor.Position.Y, z = actor.Position.Z }
					}).ToArray() : null
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

		private unsafe bool TryBuildFloorRuntime(InstanceContentDeepDungeon* dd)
		{
			var readyAtUtc = DateTime.UtcNow;
			bool isBossFloor = DeepDungeonHelper.IsBossFloor(dd->DeepDungeonId, dd->Floor);
			NormalFloorGraphSnapshot? normalGraph = null;
			var runtimeKind = isBossFloor ? FloorRuntimeKind.Boss : FloorRuntimeKind.Normal;
			if (FloorMapKey.For(dd) is { } key && FloorMap.Get(key, dd->Floor) == null)
			{
				_status = FloorMap.Error;
                if (FloorMap.Failed && _ctx != null)
                {
                    _ctx.StatusLine = _status;
                    _ctx.StatusIsError = true;
                }
				_navHelper?.Cancel();
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
				_runTelemetryObserver == null
					? null
					: new RunFloorTelemetryTrace(
						readyAtUtc,
						Service.LocalPlayer?.ClassJob.RowId ?? 0,
						dd->DeepDungeonId,
						((dd->Floor - 1) / 10) * 10 + 1,
						dd->Floor,
						floorGeneration,
						false,
						!isBossFloor),
                new FloorItemExecutor(_ctx!, _pomanderManager, _chatWatchers, () => _phase,
                    (checkpoint, force) => RecordNativeIntuitionState(checkpoint, force), RecordReplayEvent),
                _ctx!, _chaseHelper, _pomanderManager, _chatWatchers,
                _runTelemetryObserver, _passageDestination, () => _phase, value => _phase = value,
                () => _status, value => _status = value, () => _nativeIntuitionActive,
                CancelActiveMovement, RecordReplayEvent,
                (checkpoint, force) => RecordNativeIntuitionState(checkpoint, force),
                SnapshotRunOptions, RecordNativeIntuitionState);
            _floorRuntime.AttachBoss(new FloorBossController(dd->Floor, _ctx!, _navHelper!,
                _pomanderManager, _floorRuntime.Items,
                (operation, objective) => _floorRuntime.RequireMovementPermission(operation, objective),
                value => _status = value, RecordReplayEvent, RecordBossCombatSnapshot,
                _floorRuntime, _spinBossMovementFactory,
                () => _floorRuntime.CoordEscapeActive));
            _floorRuntime.AttachSurvey(new FloorSurveyController(
                _floorRuntime.Generation, _floorRuntime.DungeonId, _floorRuntime.Floor, !isBossFloor,
                _floorRuntime.ReadyAtUtc, normalGraph, _floorRuntime.ObjectEvidence, _floorRuntime.Items,
                _ctx!, _pomanderManager, _chatWatchers,
                () => _phase, () => _nativeIntuitionActive, value => _status = value,
                RecordReplayEvent, _floorRuntime.RequestPlanRefresh));
            _floorRuntime.Survey.InitializeNaturalInventory();
			if (runtimeKind == FloorRuntimeKind.Normal)
			{
				try
				{
					var roomBindings = FloorEvidenceSession.BuildRoomBindings(dd, normalGraph!.ReachableRooms);
					_floorRuntime.Survey.OpenEvidenceSession(new FloorEvidenceSession(
						FsdEngineIdentity.InformationalVersion,
						dd->DeepDungeonId,
						dd->Floor,
						Service.ClientState.TerritoryType,
						dd->ActiveLayoutIndex,
						FloorEvidenceAcquisitionMode.NaturalGameplay,
						roomBindings));
				}
				catch (Exception ex)
				{
					Service.Log.Error($"[FloorEvidenceJournal] Failed to open floor session for dungeon={dd->DeepDungeonId}, floor={dd->Floor}: {ex}");
				}
			}
			_lastObjectEvidenceTelemetryAt = DateTime.MinValue;
			_lastObjectEvidenceUnavailableAt = DateTime.MinValue;
			_phase = isBossFloor ? FloorPhase.BossFloor : FloorPhase.FloorSetup;
			_chaseHelper.Reset();
			_floorRuntime?.ResetPermissionBlocks();
			if (_ctx != null) _ctx.ClearingTargetId = null;
			_lastGraphPendingFloor = 255;
			_lastGraphPendingDungeonId = 0;
			_floorRuntime!.ResetEngagedTargetProgress();
			_chatWatchers?.BeginReadyFloor();
            if (!isBossFloor && _observingInheritance)
                _floorRuntime.Survey.BeginInheritedIntuition(_intuitionScopeStartedAt, _earlyIntuitionResult == true);
            if (_earlyIntuitionResult is { } hasHoard)
                _ctx!.Knowledge.Floor?.ObserveIntuitionResult(hasHoard);
			_status = isBossFloor ? "Boss floor" : $"Floor {_floorRuntime.Floor} - initializing";
			RecordNativeIntuitionState("stable-floor-session-created", force: true, nativeStateAvailable: isBossFloor || _nativeIntuitionSampleAvailable, nativeIntuitionActive: _nativeIntuitionActive);
			Service.Log.Info($"[FloorPhase] Floor changed to {_floorRuntime.Floor} (generation {_floorRuntime.Generation})");
			RecordReplayEvent("floor-changed", new
			{
				floor = _floorRuntime.Floor,
				floorGeneration = _floorRuntime.Generation,
				sessionKind = runtimeKind.ToString(),
				dungeon = dd->DeepDungeonId,
				territory = Service.ClientState.TerritoryType,
				layout = FloorMapKey.For(dd)?.Layout,
				activeLayoutIndex = dd->ActiveLayoutIndex,
				layoutInitializationType = dd->LayoutInitializationType,
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
				var roomFlags = new int[RoomGraph.MaxRooms];
				var mapData = dd->MapData;
				for (int i = 0; i < mapData.Length && i < roomFlags.Length; i++)
					roomFlags[i] = (int)mapData[i];
				RecordReplayEvent("normal-floor-graph-built", new
				{
					floor = _floorRuntime.Floor,
					floorGeneration = _floorRuntime.Generation,
					dungeonId = dd->DeepDungeonId,
					homeRoomIndex = normalGraph.HomeRoomIndex,
					initialPlayerRoomIndex = normalGraph.InitialPlayerRoomIndex,
					reachableRoomCount = normalGraph.ReachableRooms.Count,
					roomFlags,
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
			CancelActiveMovement();
			ObserveFloorTerminalTelemetry(runtime, reason);
			ObserveFloorTelemetryBoundary(runtime, reason);
			if (runtime.ItemUse.Pending is { } pendingFloorItemUse)
				runtime.Items.CancelPendingIntuitionAttempt(pendingFloorItemUse);
			runtime.Dispose();
			_floorRuntime = null;
			_phase = FloorPhase.FloorSetup;
			_chaseHelper.Reset();
			_floorRuntime?.ResetPermissionBlocks();
			if (_ctx != null) _ctx.ClearingTargetId = null;
			_lastGraphPendingFloor = 255;
			_lastGraphPendingDungeonId = 0;

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

        private unsafe void ObserveIntuitionScope(InstanceContentDeepDungeon* dd)
        {
            var scope = (dd->DeepDungeonId, dd->Floor);
            if (_intuitionScope == scope) return;

            bool carried = _intuitionScope.Dungeon == scope.Item1 &&
                _intuitionScope.Floor + 1 == scope.Item2 && _nativeIntuitionSampleAvailable && _nativeIntuitionActive;
            _intuitionScope = scope;
            _earlyIntuitionResult = null;
            _intuitionScopeStartedAt = Environment.TickCount64;
            _nativeIntuitionSampleAvailable = _pomanderManager.TryIsActive(
                FloorInitPlanner.IntuitionPomanderSlotIndex, out _nativeIntuitionActive);
            _observingInheritance = carried && _nativeIntuitionSampleAvailable && _nativeIntuitionActive;
            _nextNativeIntuitionPollAt = DateTime.MinValue;
        }

        private unsafe bool ObserveScopedIntuitionMessage(ChatWatchers.StateChangedInfo info)
        {
            var efw = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.Instance();
            var dd = efw == null ? null : efw->GetInstanceContentDeepDungeon();
            if (_ctx == null || dd == null || dd->Floor == 0 || dd->DeepDungeonId == 0)
                return false;
            var scope = (dd->DeepDungeonId, dd->Floor);
            bool changingAreas = _ctx.Duty.IsTransitioning || DutyState.IsClientTransitioning;
            if (changingAreas && (_floorRuntime is { } last && last.DungeonId == scope.Item1 &&
                    last.Floor == scope.Item2 ||
                _intuitionScope == scope && !_intuitionScopeDuringTransition))
                return false;

            if (_intuitionScope != scope)
                _intuitionScopeDuringTransition = changingAreas;
            ObserveIntuitionScope(dd);
            bool positive = info.Reason == "LogMessage7272";
            if (info.EvidenceTargetFloor != 0 && info.EvidenceTargetFloor != dd->Floor)
            {
                // A receipt for the prior floor cannot establish presence here.
                // A positive message at this boundary invalidates an absence inference.
                if (positive) _observingInheritance = false;
                return false;
            }
            var runtimeInScope = _floorRuntime is { IsDisposed: false } current &&
                current.Floor == dd->Floor && current.DungeonId == dd->DeepDungeonId ? current : null;
            bool inherited = _observingInheritance && !info.EvidenceAccepted;
            if (inherited)
            {
                if (runtimeInScope != null)
                    runtimeInScope.Survey.ObserveInheritedMessage(positive);
                // An unexpected negative during loading cannot later count as silence.
                else if (!positive)
                    _observingInheritance = false;
            }
            bool floorEvidence = positive || !inherited;
            if (floorEvidence)
            {
                _earlyIntuitionResult = positive || _earlyIntuitionResult == true;
                if (runtimeInScope != null && _ctx.Knowledge.Floor?.ObserveIntuitionResult(positive) == true)
                {
                    runtimeInScope.ObjectEvidence.Invalidate();
                    RequestPlanRefresh(info.Reason);
                }
            }
            runtimeInScope?.Survey.EvidenceSession?.ObserveSemanticMessage(positive ? 7272u : 7273u, info.EvidenceAccepted);
            if (runtimeInScope != null &&
                _chatWatchers?.CompleteExpectedIntuitionResult(info.EvidenceAttemptId) == true)
            {
                runtimeInScope.PendingIntuition.TryMarkResolved(info.EvidenceAttemptId);
                if (runtimeInScope.ItemUse.Pending is { } pending &&
                    pending.IntuitionAttemptId == info.EvidenceAttemptId)
                    pending.ObserveAuthoritativeConfirmation();
            }
            RecordReplayEvent("intuition-message-scope", new { floor = dd->Floor, dungeonId = dd->DeepDungeonId,
                info.Reason, floorEvidence, inherited,
                bufferedBeforeRuntime = runtimeInScope == null });
            return true;
        }

		private void OnChatWatchersStateChanged(ChatWatchers.StateChangedInfo info)
		{
            if (info.Reason is "LogMessage7272" or "LogMessage7273")
            {
                if (!ObserveScopedIntuitionMessage(info))
                {
                    RecordReplayEvent("intuition-message-outside-stable-floor", info);
                    return;
                }
            }
			if (info.Reason is "LogMessage7256" or "LogMessage11251Mazeroot")
				_floorRuntime?.ObjectEvidence.Invalidate();

			RecordReplayEvent("chat-watchers-state", info);
			if (info.Reason == "LogMessage7274Observed")
				_floorRuntime?.Survey.EvidenceSession?.ObserveSemanticMessage(7274, accepted: false);

			switch (info.Reason)
			{
				case "GoldChestOvercapObserved":
					_floorRuntime?.HandleGoldChestOvercapObserved(info.GoldChestOvercapSlotIndex);
					break;
				case "SilverChestOvercapObserved":
					_floorRuntime?.HandleSilverChestOvercapObserved(info.SilverChestOvercapRowId);
					break;
                case "MagiciteUseObserved":
                    if (info.UsedMagiciteTypeId is { } typeId) _floorRuntime?.ObserveMagiciteUse(typeId);
                    break;
                case "FalsePassageObserved":
                    _floorRuntime?.ObserveFalsePassage();
                    break;
			}

			if (info.Reason is "LogMessage7272" or "LogMessage7273")
				RecordNativeIntuitionState($"chat-{info.Reason}", force: true);
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
				chatSaysHoard = _ctx?.Knowledge.Floor?.ChatSaysHoard ?? false,
				chatSaysNoHoard = _ctx?.Knowledge.Floor?.ChatSaysNoHoard ?? false,
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

        private bool _waitingForRevival;
		private unsafe bool TryMarkPlayerDeathFatal(InstanceContentDeepDungeon* dd)
		{
			var player = Service.LocalPlayer;
			if (player == null || !player.IsDead)
            {
                if (player != null && _waitingForRevival)
                {
                    _waitingForRevival = false;
                    RecordReplayEvent("player-revived", new { floor = dd->Floor, hp = player.CurrentHp });
                }
                return false;
            }

            if (_ctx?.FarmingPlan != null)
            {
                if (!_waitingForRevival)
                {
                    _waitingForRevival = true;
                    RecordReplayEvent("player-dead-waiting-duty-outcome", new
                    {
                        floor = dd->Floor, hp = player.CurrentHp,
                        raisingActive = dd->Items[(int)FloorInitPlanner.RaisingPomanderSlotIndex].IsActive
                    });
                }
                _status = "Waiting for revival or duty outcome";
                SuspendBossMovement();
                _floorRuntime?.CancelCoordMovement();
                _navHelper?.Cancel();
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

        private void RequestPlanRefresh(string reason) => _floorRuntime?.RequestPlanRefresh(reason);
	}
}
