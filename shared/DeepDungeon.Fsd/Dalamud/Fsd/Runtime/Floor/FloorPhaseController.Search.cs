using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.SubKinds;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor
{
	public sealed partial class FloorPhaseController
	{
		private enum SearchExecutionKind
		{
			PlannedRoom,
			BandedReentry
		}

		private enum RoomObjectiveCategory
		{
			Hoard,
			Chests,
			Intel
		}

		private readonly record struct RoomObjectiveKey(
			int RoomIndex,
			RoomObjectiveCategory Category,
			FloorObjectiveKind Kind);

		private readonly record struct ActiveObjectiveExecution(
			ObjectiveIdentity Identity,
			FloorObjectiveKind Kind,
			bool Required,
			RoomObjectiveCategory Category);

		private sealed class ObjectiveExecution : IDisposable
		{
			public ObjectiveExecution(FloorObjectiveKind objective, NavigationHelper navigation)
			{
				Objective = objective;
				TaskRunner = new WaypointTaskRunner(navigation);
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
			public ulong EngagedTargetProgressId;
			public uint EngagedTargetProgressHp;
			public DateTime EngagedTargetProgressAt = DateTime.MinValue;
			public bool ClearingEngageRecentering;
			public DateTime ClearingEngageRecenteringAt = DateTime.MinValue;
			public ulong PreEngageTargetProgressId;
			public uint PreEngageTargetProgressHp;
			public DateTime PreEngageTargetProgressAt = DateTime.MinValue;
			public bool ClearingPreEngageAirWallRecovery;
			public DateTime ClearingPreEngageAirWallRecoveryAt = DateTime.MinValue;
			public int ClearingPreEngageTargetRoom = -1;

			public void Dispose()
			{
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
			UsedImmediately,
			PendingRetry
		}

		private sealed class PostRoomPomanderRetry
		{
			public int FinishedRoomIndex;
			public DateTime ExpiresAt;
		}

		private readonly record struct BandedRevealExpectation(DateTime ExpiresAtUtc, long EvidenceSequence);

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
			get => _floorRuntime?.ActiveExecution?.ActiveWaypoint;
			set
			{
				if (_floorRuntime?.ActiveExecution != null)
				{
					if (_floorRuntime.ActiveExecution.ActiveWaypoint.HasValue &&
					    (!value.HasValue ||
					     _floorRuntime.ActiveExecution.ActiveWaypoint.Value != value.Value))
					{
						EndActiveWaypointTelemetry(RunWaypointTerminalOutcome.Aborted, "WaypointCleared");
					}
					_floorRuntime.ActiveExecution.ActiveWaypoint = value;
					if (!value.HasValue || !IsChestWaypoint(value.Value))
						_floorRuntime.ActiveExecution.ChestAttempt = null;
				}
				else if (value.HasValue)
					throw new InvalidOperationException("Cannot set a waypoint without an active objective execution.");
			}
		}
		private SearchExecutionKind _searchExecutionKind
		{
			get => _floorRuntime?.ActiveExecution?.SearchKind ?? SearchExecutionKind.PlannedRoom;
			set
			{
				if (_floorRuntime?.ActiveExecution != null)
					_floorRuntime.ActiveExecution.SearchKind = value;
				else if (value != SearchExecutionKind.PlannedRoom)
					throw new InvalidOperationException("Cannot start a non-default search execution without an active objective execution.");
			}
		}
		private FloorSearchState SearchState =>
			_floorRuntime?.SearchState ?? throw new InvalidOperationException("No active floor search state.");
		private ChestInteractionAttempt? ActiveChestAttempt => _floorRuntime?.ActiveExecution?.ChestAttempt;

		private const float TrapStandDurationSeconds = 3.0f;
		private const float IntelSettleDurationSeconds = 1.0f;
		private const float SilverWaitTimeoutSeconds = 30.0f;
		private const float ChestOpenTimeoutSeconds = 30.0f;
		private const float BandedChestOpenTimeoutSeconds = 120.0f;
		private const float PostRoomPomanderRetrySeconds = 3.0f;
		private const float BandedRevealExpectationSeconds = 3.0f;
		private const uint StrengthStatusId = 687;
		private const uint SteelStatusId = 1100;
		private const uint DeepDungeonCurseStatusId = 1087;

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
			if (_ctx?.Duty.PassageOpen != true || _executor == null)
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
				bool activeSearchRemoved = _floorRuntime?.ActiveExecution?.ObjectiveRecords.Count > 0 &&
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
				var normalGraph = _floorRuntime?.NormalGraph;
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
			long requestId = ++_nextRoomSearchRequestId;
			var objectEvidence = _floorRuntime!.ObjectEvidence.Current!;
			int observedRoomIndex = _floorRuntime.LastObservedRoomIndex;
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
			var runtime = _floorRuntime;
			if (runtime == null || runtime.IsDisposed)
			{
				_status = "Cannot start room objectives without an active floor runtime";
				Service.Log.Error($"[FloorPhase] {_status}");
				return false;
			}
			var activeExecution = runtime.ActiveExecution;
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
					runtime,
					activeExecution,
					roomIndex,
					RoomObjectiveCategory.Hoard,
					FloorObjectiveKind.OpenVisibleBandedChest,
					required: true);
			}

			if (entry?.ShouldProbeHoard == true &&
			    !TryBeginObjectiveExecution(
				    runtime,
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
				    runtime,
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
				    runtime,
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
			FloorRuntime runtime,
			ObjectiveExecution activeExecution,
			int roomIndex,
			RoomObjectiveCategory category,
			FloorObjectiveKind kind,
			bool required)
		{
			var objective = runtime.GetOrCreateObjective(new RoomObjectiveKey(roomIndex, category, kind), kind, required);
			if (objective.Outcome != ObjectiveOutcomeKind.Pending)
			{
				if (objective.Outcome != ObjectiveOutcomeKind.Preempted &&
				    (!required || !RoomObjectiveOutcomePlanner.IsRetryableFailure(objective.Outcome)))
				{
					RecordObjectiveExecutionRejected(objective.Identity, kind, category, "Start", ObjectiveRestartStatus.NotRestartable.ToString());
					return false;
				}

				var restart = runtime.ObjectiveLedger.Restart(objective.Identity);
				if (restart.Status != ObjectiveRestartStatus.Restarted)
				{
					RecordObjectiveExecutionRejected(objective.Identity, kind, category, "Restart", restart.Status.ToString());
					return false;
				}
				objective = restart.Objective;
			}

			activeExecution.ObjectiveRecords.Add(new ActiveObjectiveExecution(objective.Identity, kind, required, category));
			runtime.RunTelemetry?.ObserveObjectiveStart(
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
			var runtime = _floorRuntime;
			var activeExecution = runtime?.ActiveExecution;
			if (runtime == null || runtime.IsDisposed || activeExecution == null)
			{
				_status = activeExecution == null
					? "Rejected objective outcome without an active objective execution"
					: "Rejected objective outcome without its active floor runtime";
				RecordReplayEvent("objective-outcome-rejected-no-active-execution", new
				{
					floorGeneration = runtime?.Generation ?? 0,
					roomIndex,
					reason,
					status = _status
				});
				Service.Log.Error($"[FloorPhase] {_status}");
				return false;
			}
			var objectiveRecords = activeExecution.ObjectiveRecords;
			var searchState = runtime.SearchState;

			foreach (var execution in objectiveRecords)
			{
				var terminalOutcome = GetCategoryOutcome(execution.Category, outcome);
				var status = runtime.ObjectiveLedger.ValidateOutcome(execution.Identity, terminalOutcome, out _);
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
				var result = runtime.ObjectiveLedger.ApplyOutcome(execution.Identity, terminalOutcome);
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

		private bool PreemptActiveObjectiveExecutions(
			string reason,
			FloorObjectiveKind? replacementObjective = null)
		{
			if (_floorRuntime?.ActiveExecution?.ObjectiveRecords.Count is null or 0)
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
			var runtime = _floorRuntime;
			var previousObjective = runtime?.ActiveExecution?.Objective ?? FloorObjectiveKind.None;
			int roomIndex = _executor?.RoomContext?.RoomIndex ?? _executor?.CurrentTargetRoomIndex ?? -1;
			var executionKind = _searchExecutionKind;
			string reason = $"ObjectiveChanged:{replacementObjective}";
			if (!PreemptActiveObjectiveExecutions(reason, replacementObjective))
				return false;

			RecordReplayEvent("room-search-aborted", new
			{
				floor = runtime?.Floor ?? 0,
				floorGeneration = runtime?.Generation ?? 0,
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
			if (TryUpdateChestRecovery(dd, player))
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
					    _floorRuntime?.ObjectEvidence.Current is { } arrivalEvidence &&
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

		private unsafe bool TryUpdateChestRecovery(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
		{
			var attempt = ActiveChestAttempt;
			if (attempt == null || _activeWaypoint is not { } waypoint || !IsChestWaypoint(waypoint))
				return false;

			if (_ctx?.ChestInteraction.TryBeginRecovery(
					attempt,
					_floorRuntime?.ObjectEvidence.Current,
					out var reason) == true)
			{
				_floorRuntime?.RunTelemetry?.ObserveChestRecoveryStarted();
				RecordReplayEvent("chest-recovery-started", new
				{
					floor = dd->Floor,
					roomIndex = _executor?.RoomContext?.RoomIndex ?? -1,
					waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1,
					chestType = waypoint.Type.ToString(),
					entityId = attempt.EntityId,
					reason
				});
			}

			if (attempt.RecoveryPhase is not (ChestRecoveryPhase.Recentering or ChestRecoveryPhase.Returning))
				return false;

			int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
			if (playerRoom < 0 || !TryResolveRoomDestination(dd, playerRoom, out var roomCenter))
			{
				FinishChestRecovery(attempt, waypoint, false, "room-center-unavailable");
				return false;
			}

			var destination = attempt.RecoveryPhase == ChestRecoveryPhase.Recentering
				? roomCenter
				: waypoint.Position;
			var state = _navHelper!.Navigate(destination, player.Position, arrivalRadius: 1.5f);
			switch (state)
			{
				case NavigationState.Moving:
					_status = attempt.RecoveryPhase == ChestRecoveryPhase.Recentering
						? "Recentering after blocked chest interaction"
						: $"Returning to {DescribeChest(waypoint.Type)} after recentering";
					return true;
				case NavigationState.StuckRepathing:
					_status = $"Recovering blocked chest interaction ({_navHelper.StuckRetryCount}/3)";
					return true;
				case NavigationState.Arrived:
					if (attempt.RecoveryPhase == ChestRecoveryPhase.Recentering)
					{
						attempt.RecoveryPhase = ChestRecoveryPhase.Returning;
						_status = $"Reapproaching {DescribeChest(waypoint.Type)} after recentering";
						return true;
					}
					FinishChestRecovery(attempt, waypoint, true, "returned-to-chest");
					return false;
				case NavigationState.StuckGiveUp:
					FinishChestRecovery(attempt, waypoint, false, "navigation-stuck");
					return false;
				case NavigationState.Failed:
					FinishChestRecovery(attempt, waypoint, false, "navigation-failed");
					return false;
				default:
					return true;
			}
		}

		private void FinishChestRecovery(
			ChestInteractionAttempt attempt,
			RoomWaypoint waypoint,
			bool succeeded,
			string reason)
		{
			attempt.RecoveryPhase = ChestRecoveryPhase.Exhausted;
			attempt.ConsecutiveInteractionRejects = 0;
			attempt.NextInteractAt = DateTime.MinValue;
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

			if (ShouldUseGoldChestOvercapPomander(slotIndex))
			{
				if (CanAttemptPomanderUse() &&
				    TryUsePomander(slotIndex, dd, $"gold overcap relief ({DescribePomanderSlot(slotIndex)})"))
				{
					Service.Log.Info($"[FloorPhase] Used {DescribePomanderSlot(slotIndex)} to resolve capped gold chest");
					chestAttempt.PendingGoldOvercapSlotIndex = null;
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

		private void HandleGoldChestOvercapObserved(uint? slotIndex)
		{
			var runtime = _floorRuntime;
			var attempt = ActiveChestAttempt;
			if (!slotIndex.HasValue ||
			    runtime == null ||
			    runtime.IsDisposed ||
			    _activeWaypoint?.Type != RoomObjectiveType.ChestGold ||
			    attempt == null ||
			    attempt.EntityId == 0)
			{
				RecordReplayEvent("gold-chest-overcap-rejected", new
				{
					floor = runtime?.Floor ?? 0,
					floorGeneration = runtime?.Generation ?? 0,
					slotIndex,
					reason = "no-active-gold-interaction-attempt"
				});
				return;
			}

			attempt.PendingGoldOvercapSlotIndex = slotIndex.Value;
			RecordReplayEvent("gold-chest-overcap-correlated", new
			{
				floor = runtime.Floor,
				floorGeneration = runtime.Generation,
				roomIndex = _executor?.RoomContext?.RoomIndex ?? -1,
				waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1,
				entityId = attempt.EntityId,
				slotIndex = slotIndex.Value
			});
		}

		private void ConfigureTaskForWaypoint(RoomWaypoint waypoint)
		{
			_activeWaypoint = waypoint;
			if (IsChestWaypoint(waypoint))
				_floorRuntime!.ActiveExecution!.ChestAttempt = new ChestInteractionAttempt(waypoint);
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
					_chatWatchers?.ExpectHoardCofferFound(_floorRuntime?.Floor ?? 0);
					_taskRunner!.Configure(
						waypoint.Position, 1.5f,
						preCondition: null, preTimeoutSeconds: 0,
						postCondition: _ => IsChestAccepted(waypoint),
						postTimeoutSeconds: BandedChestOpenTimeoutSeconds);
					break;

				case RoomObjectiveType.ChestSilver:
					_taskRunner!.Configure(
						waypoint.Position, 1.5f,
						preCondition: () => _ctx?.ChestInteraction.CanStart(ActiveChestAttempt, waypoint) == true,
						preTimeoutSeconds: SilverWaitTimeoutSeconds,
						postCondition: _ => IsChestAccepted(waypoint),
						postTimeoutSeconds: ChestOpenTimeoutSeconds);
					break;

				default:
					_taskRunner!.Configure(
						waypoint.Position, 1.5f,
						preCondition: null, preTimeoutSeconds: 0,
						postCondition: _ => IsChestAccepted(waypoint),
						postTimeoutSeconds: ChestOpenTimeoutSeconds);
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
				_floorRuntime?.ObjectEvidence.Invalidate();
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

			var normalGraph = _floorRuntime?.NormalGraph;
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
			    _floorRuntime?.ObjectiveLedger.TryGetObjective(mandatoryExecution.Value.Identity.ObjectiveId, out var mandatoryObjective) == true)
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
			var objectiveRecords = _floorRuntime?.ActiveExecution?.ObjectiveRecords;
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
					    _floorRuntime != null &&
					    _executor?.HasAuthoritativeHoardResolution != true)
					{
						if (!_floorRuntime.ObjectiveLedger.ResetFailureCount(SearchState.MandatoryObjectiveBlockedIdentity.Value))
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
			_floorRuntime!.ActiveExecution!.Objective = FloorObjectiveKind.OpenVisibleBandedChest;
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

			var normalGraph = _floorRuntime?.NormalGraph;
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
					_floorRuntime?.ObjectEvidence.Current,
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
			if (!BandedChestLocator.TryFindHoardIndicatorMatch(_floorRuntime!.ObjectEvidence.Current!, out var indicator))
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
					_floorRuntime?.ObjectEvidence.Invalidate();
				}
			}
			else
			{
				var snapshot = _ctx?.ChestInteraction.Observe(ActiveChestAttempt, _floorRuntime?.ObjectEvidence.Current) ?? default;
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
			var runtime = _floorRuntime;
			if (runtime == null || runtime.IsDisposed)
				return;

			var expectation = new BandedRevealExpectation(
				DateTime.UtcNow.AddSeconds(BandedRevealExpectationSeconds),
				runtime.ObjectEvidence.Current?.RefreshSequence ?? 0);
			runtime.BandedRevealExpectation = expectation;
			RecordReplayEvent("banded-reveal-expectation-started", new
			{
				floor = runtime.Floor,
				floorGeneration = runtime.Generation,
				sourceRoomIndex,
				evidenceSequence = expectation.EvidenceSequence,
				durationSeconds = BandedRevealExpectationSeconds,
				reason = "hoard-room-search-finished"
			});
		}

		private bool IsBandedRevealExpectationPending()
		{
			var runtime = _floorRuntime;
			if (runtime == null || runtime.IsDisposed || runtime.BandedRevealExpectation is not { } expectation)
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
			    runtime.ObjectEvidence.Current is { Available: true } evidence &&
			    evidence.RefreshSequence > expectation.EvidenceSequence)
			{
				ClearBandedRevealExpectation("expired");
				return false;
			}

			return true;
		}

		private void ClearBandedRevealExpectation(string reason)
		{
			var runtime = _floorRuntime;
			if (runtime == null || runtime.BandedRevealExpectation is not { } expectation)
				return;

			RecordReplayEvent("banded-reveal-expectation-ended", new
			{
				floor = runtime.Floor,
				floorGeneration = runtime.Generation,
				evidenceSequence = runtime.ObjectEvidence.Current?.RefreshSequence ?? 0,
				startedAtEvidenceSequence = expectation.EvidenceSequence,
				reason
			});
			runtime.BandedRevealExpectation = null;
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
				var normalGraph = _floorRuntime?.NormalGraph;
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
			    TryUseNaturalMazeroot(dd, "S1 Sight fallback with 敏慧"))
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
			       _pomanderManager.GetStoneCount(2) > 0;
		}

		private unsafe void TryUseGeneralAutoPomander(InstanceContentDeepDungeon* dd)
		{
			if (_ctx?.ControlledPtSurvey != null)
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
				SightUseBlocked = IsSightUseBlocked(),
				IntuitionUsable = pomandersUsableThisFloor && _pomanderManager.IsUsable(FloorInitPlanner.IntuitionPomanderSlotIndex),
				SightUsable = pomandersUsableThisFloor && _pomanderManager.IsUsable(FloorInitPlanner.SightPomanderSlotIndex),
				AffluenceUsable = pomandersUsableThisFloor && _pomanderManager.IsUsable(FloorInitPlanner.AffluencePomanderSlotIndex),
				StrengthUsable = pomandersUsableThisFloor && _pomanderManager.IsUsable(FloorInitPlanner.StrengthPomanderSlotIndex),
				SteelUsable = pomandersUsableThisFloor && _pomanderManager.IsUsable(FloorInitPlanner.SteelPomanderSlotIndex),
				PurityUsable = pomandersUsableThisFloor && _pomanderManager.IsUsable(FloorInitPlanner.PurityPomanderSlotIndex),
				SerenityUsable = pomandersUsableThisFloor && _pomanderManager.IsUsable(FloorInitPlanner.SerenityPomanderSlotIndex),
				RaisingUsable = pomandersUsableThisFloor && _pomanderManager.IsUsable(FloorInitPlanner.RaisingPomanderSlotIndex),
				AffluenceActive = _pomanderManager.IsActive(FloorInitPlanner.AffluencePomanderSlotIndex),
				RaisingActive = _pomanderManager.IsActive(FloorInitPlanner.RaisingPomanderSlotIndex),
				HasStrengthStatus = HasLocalPlayerStatus(StrengthStatusId),
				HasSteelStatus = HasLocalPlayerStatus(SteelStatusId),
				HasCurseStatus = HasLocalPlayerStatus(DeepDungeonCurseStatusId),
				HasHarmfulFloorEffect = hasHarmfulFloorEffect
			};
		}

		private GeneralAutoPomanderSnapshot BuildGeneralAutoPomanderSnapshot(bool allowStatusOverlap, bool hasHarmfulFloorEffect = false)
		{
			return new GeneralAutoPomanderSnapshot
			{
				CanAttemptPomanderUse = CanAttemptPomanderUse(),
				AffluenceUsable = _pomanderManager.IsUsable(FloorInitPlanner.AffluencePomanderSlotIndex),
				StrengthUsable = _pomanderManager.IsUsable(FloorInitPlanner.StrengthPomanderSlotIndex),
				SteelUsable = _pomanderManager.IsUsable(FloorInitPlanner.SteelPomanderSlotIndex),
				PurityUsable = _pomanderManager.IsUsable(FloorInitPlanner.PurityPomanderSlotIndex),
				SerenityUsable = _pomanderManager.IsUsable(FloorInitPlanner.SerenityPomanderSlotIndex),
				RaisingUsable = _pomanderManager.IsUsable(FloorInitPlanner.RaisingPomanderSlotIndex),
				AffluenceActive = _pomanderManager.IsActive(FloorInitPlanner.AffluencePomanderSlotIndex),
				RaisingActive = _pomanderManager.IsActive(FloorInitPlanner.RaisingPomanderSlotIndex),
				HasStrengthStatus = HasLocalPlayerStatus(StrengthStatusId),
				HasSteelStatus = HasLocalPlayerStatus(SteelStatusId),
				HasCurseStatus = HasLocalPlayerStatus(DeepDungeonCurseStatusId),
				HasHarmfulFloorEffect = hasHarmfulFloorEffect,
				AllowStatusOverlap = allowStatusOverlap
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
					return TryUsePomander(decision.SlotIndex!.Value, dd, decision.Reason!)
						? RoomFinishPomanderOutcome.UsedImmediately
						: RoomFinishPomanderOutcome.PendingRetry;
				default:
					return RoomFinishPomanderOutcome.NotNeeded;
			}
		}

		private bool ShouldUseGoldChestOvercapPomander(uint slotIndex)
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
				SightUseBlocked = IsSightUseBlocked(),
				IntuitionUsable = pomandersUsableThisFloor && _pomanderManager.IsUsable(FloorInitPlanner.IntuitionPomanderSlotIndex),
				SightUsable = pomandersUsableThisFloor && _pomanderManager.IsUsable(FloorInitPlanner.SightPomanderSlotIndex),
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

		private static bool HasLocalPlayerStatus(uint statusId)
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

		private bool CanAttemptPomanderUse()
		{
			if (Service.Condition[ConditionFlag.InCombat] ||
			    Service.Condition[ConditionFlag.Casting] ||
			    Service.Condition[ConditionFlag.BetweenAreas] ||
			    Service.Condition[ConditionFlag.BetweenAreas51])
			{
				return false;
			}

			if (DateTime.UtcNow < _nextPomanderUseAt)
				return false;

			return true;
		}

		private unsafe bool TryUsePomander(uint slotIndex, InstanceContentDeepDungeon* dd, string reason)
		{
			if (!DeepDungeonFloorItemUsePolicy.CanUsePomanders(
				    dd->DeepDungeonBanId) ||
			    !_pomanderManager.IsUsable(slotIndex))
				return false;

			long sightLogSequenceBeforeDispatch =
				_chatWatchers?.SightLogSequence ?? 0;
			long mazerootLogSequenceBeforeDispatch =
				_chatWatchers?.MazerootLogSequence ?? 0;
			if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
				RecordNativeIntuitionState($"before-intuition-use:{reason}", force: true);

			long intuitionAttemptId = 0;
			long intuitionExpectedAtMilliseconds = 0;
			DateTime intuitionExpectedAtUtc = DateTime.MinValue;
			if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
			{
				intuitionExpectedAtMilliseconds = Environment.TickCount64;
				intuitionExpectedAtUtc = DateTime.UtcNow;
				intuitionAttemptId = _chatWatchers?.ExpectIntuitionResult(dd->Floor) ?? 0;
				PendingIntuition.MarkUsed(dd->Floor, intuitionExpectedAtUtc, intuitionAttemptId);
			}

			if (_pomanderManager.Use(slotIndex))
			{
				_pomanderDispatchedThisUpdate = true;
				if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
				{
					_chatWatchers?.MarkIntuitionUsedThisFloor();
					if (_ctx?.ControlledPtSurvey != null && _floorRuntime != null)
					{
						_floorRuntime.ControlledIntuitionExpectationStartedAtMilliseconds = intuitionExpectedAtMilliseconds;
						_floorRuntime.ControlledIntuitionExpectationAttemptId = intuitionAttemptId;
						_floorRuntime.ControlledIntuitionResolved = false;
						_floorRuntime.ControlledIntuitionDecision = null;
					}
				}
				else if (slotIndex == FloorInitPlanner.SightPomanderSlotIndex)
				{
					_chatWatchers?.MarkSightAttemptedThisFloor();
					_floorRuntime?.ObjectEvidence.Invalidate();
					RegisterNaturalRevealDispatch(
						SightResearchRevealResource.Sight,
						sightLogSequenceBeforeDispatch,
						mazerootLogSequenceBeforeDispatch);
				}

				_nextPomanderUseAt = DateTime.UtcNow.AddSeconds(3);
				Service.Log.Info($"[FloorPhase] Used pomander slot {slotIndex} ({reason}) on floor {dd->Floor}");
				RecordReplayEvent("pomander-used", new
				{
					floor = dd->Floor,
					slotIndex,
					reason,
					intuitionAttemptId = slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex
						? intuitionAttemptId
						: 0
				});
				if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
					RecordNativeIntuitionState($"after-intuition-use:{reason}:success", force: true);
				return true;
			}

			if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
			{
				PendingIntuition.CancelAttempt(intuitionAttemptId);
				_chatWatchers?.CancelExpectedIntuitionResult(intuitionAttemptId);
				RecordNativeIntuitionState($"after-intuition-use:{reason}:failed", force: true);
			}
			_nextPomanderUseAt = DateTime.UtcNow.AddSeconds(1);
			return false;
		}

		private unsafe void ObserveHoardCount(InstanceContentDeepDungeon* dd, string checkpoint)
		{
			int current = dd->HoardCount;
			int previous = PlanningState.LastKnownHoardCount;
			_executor!.ObserveHoardCount(current);
			PlanningState.LastKnownHoardCount = current;
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
				case RoomFinishPomanderOutcome.UsedImmediately:
					Service.Log.Info("[FloorPhase] Consumed post-room S2 pomander while continuing main flow");
					ClearPostRoomPomanderRetry();
					return true;
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

			var normalGraph = _floorRuntime?.NormalGraph;
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

		private bool ShouldRunGeneralTick()
		{
			var now = DateTime.UtcNow;
			if ((now - PlanningState.LastGeneralTickAt) < GeneralTickInterval)
				return false;

			PlanningState.LastGeneralTickAt = now;
			return true;
		}
	}
}
