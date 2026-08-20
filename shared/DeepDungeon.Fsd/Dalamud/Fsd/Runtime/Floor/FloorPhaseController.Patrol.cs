using System;
using System.Collections.Generic;
using System.Numerics;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.SubKinds;
using global::Dalamud.Game.ClientState.Objects.Types;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Map;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor
{
	public sealed partial class FloorPhaseController
	{
		private ObjectiveExecution PatrolExecution =>
			_floorRuntime?.ActiveExecution ?? throw new InvalidOperationException("No active patrol objective execution.");
		private List<int> _patrolRooms => PatrolExecution.PatrolRooms;
		private ref int _patrolIndex => ref PatrolExecution.PatrolIndex;
		private ref int _currentPatrolRoom => ref PatrolExecution.CurrentPatrolRoom;
		private ref ulong _engagedTargetProgressId => ref PatrolExecution.EngagedTargetProgressId;
		private ref uint _engagedTargetProgressHp => ref PatrolExecution.EngagedTargetProgressHp;
		private ref DateTime _engagedTargetProgressAt => ref PatrolExecution.EngagedTargetProgressAt;
		private ref bool _clearingEngageRecentering => ref PatrolExecution.ClearingEngageRecentering;
		private ref DateTime _clearingEngageRecenteringAt => ref PatrolExecution.ClearingEngageRecenteringAt;
		private ref ulong _preEngageTargetProgressId => ref PatrolExecution.PreEngageTargetProgressId;
		private ref uint _preEngageTargetProgressHp => ref PatrolExecution.PreEngageTargetProgressHp;
		private ref DateTime _preEngageTargetProgressAt => ref PatrolExecution.PreEngageTargetProgressAt;
		private ref bool _clearingPreEngageAirWallRecovery => ref PatrolExecution.ClearingPreEngageAirWallRecovery;
		private ref DateTime _clearingPreEngageAirWallRecoveryAt => ref PatrolExecution.ClearingPreEngageAirWallRecoveryAt;
		private ref int _clearingPreEngageTargetRoom => ref PatrolExecution.ClearingPreEngageTargetRoom;

		private static readonly TimeSpan ClearingPreEngageRecoveryLimit = TimeSpan.FromSeconds(10);

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

			if (TryUpdateClearingEngageRecentering(dd, player))
				return;

			if (TryUpdateClearingPreEngageAirWallRecovery(dd, player))
				return;

			var target = _chaseHelper.GetClearingTarget(
				dd,
				_floorRuntime?.NormalGraph,
				_floorRuntime?.LastObservedRoomIndex ?? -1,
				player.Position,
				out var acquisitionFailure);
			if (target != null)
			{
				_lastChaseAcquisitionFailureKey = string.Empty;
				RecordChaseTargetEvent("clearing-target-selected", target.Value);
				_ctx.SetPreferredAggroTarget(target.Value.GameObjectId, target.Value.Position);

				var currentTarget = CombatTargetingHelpers.GetBattleCharaByGameObjectId(target.Value.GameObjectId);
				bool targetSpecificAggro =
					target.Value.Reason == EnemyChaseTargetReason.Aggro ||
					EnemyChaseHelper.IsAggroedToPlayer(target.Value.GameObjectId);
				bool casting = Service.Condition[ConditionFlag.Casting];
				bool withinLiveTargetHoldRange = IsWithinLiveTargetHoldRange(target.Value, player.Position);
				bool preEngageRecovery =
					TrackPreEngageTarget(
						target.Value,
						currentTarget,
						targetSpecificAggro,
						attackAttemptWindow: casting || withinLiveTargetHoldRange,
						dd);
				if (preEngageRecovery)
				{
					_chaseHelper.CompleteCurrentLeg();
					_navHelper?.Cancel();
					if (TryUpdateClearingPreEngageAirWallRecovery(dd, player))
						return;
				}

				bool engaged = targetSpecificAggro;
				if (engaged || casting || withinLiveTargetHoldRange)
				{
					_chaseHelper.CompleteCurrentLeg();
					_status = engaged
						? "Hostile engaged"
						: casting
							? "Holding position while casting"
							: "Hostile within attack hold range";
					if (_navHelper!.HasActiveTarget)
						_navHelper.Cancel();
					if (engaged)
					{
						if (TryRecoverStalledEngage(dd, target.Value, player))
							return;
					}
					else
					{
						ResetEngagedProgressOnly();
					}
				}
				else
				{
					ResetEngagedProgressOnly();
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

				if (TryResolveRoomDestination(dd, targetRoom, out var dest))
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

		private void ResetPatrolPlan()
		{
			var execution = _floorRuntime?.ActiveExecution;
			if (execution == null)
				return;
			execution.PatrolRooms.Clear();
			execution.PatrolIndex = 0;
			execution.CurrentPatrolRoom = -1;
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
			_lastChaseTargetEventAt = now;
			RecordReplayEvent(eventType, new
			{
				phase = _phase.ToString(),
				targetId = target.GameObjectId,
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
				playerRoom
			});
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

		private unsafe bool TryRecoverStalledEngage(InstanceContentDeepDungeon* dd, EnemyChaseTarget target, IPlayerCharacter player)
		{
			var current = CombatTargetingHelpers.GetBattleCharaByGameObjectId(target.GameObjectId);
			if (current == null)
			{
				ResetEngagedTargetProgress();
				return false;
			}

			var now = DateTime.UtcNow;
			if (_engagedTargetProgressId != target.GameObjectId)
			{
				_engagedTargetProgressId = target.GameObjectId;
				_engagedTargetProgressHp = current.CurrentHp;
				_engagedTargetProgressAt = now;
				return false;
			}

			if (current.CurrentHp < _engagedTargetProgressHp)
			{
				_engagedTargetProgressHp = current.CurrentHp;
				_engagedTargetProgressAt = now;
				_clearingEngageRecentering = false;
				_clearingEngageRecenteringAt = DateTime.MinValue;
				return false;
			}

			if (now - _engagedTargetProgressAt < ClearingEngageNoProgressLimit)
				return false;

			if (!_clearingEngageRecentering)
				_floorRuntime?.RunTelemetry?.ObserveStalledEngageRecoveryStarted();

			int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
			if (playerRoom < 0 || !TryResolveRoomDestination(dd, playerRoom, out _))
			{
				SuppressStalledEngageTarget(dd, target.GameObjectId, "room-center-unavailable", current, target.Position);
				return true;
			}

			if (!_clearingEngageRecentering)
			{
				_clearingEngageRecentering = true;
				_clearingEngageRecenteringAt = now;
				RecordReplayEvent("clearing-engage-recenter-started", new
				{
					floor = dd->Floor,
					targetId = target.GameObjectId,
					reason = "engage-no-hp-progress",
					seconds = (int)(now - _engagedTargetProgressAt).TotalSeconds,
					playerRoom,
					currentHp = current.CurrentHp,
					lastProgressHp = _engagedTargetProgressHp,
					x = target.Position.X,
					y = target.Position.Y,
					z = target.Position.Z
				});
			}

			return TryUpdateClearingEngageRecentering(dd, player);
		}

		private unsafe bool TrackPreEngageTarget(
			EnemyChaseTarget target,
			IBattleChara? current,
			bool targetSpecificAggro,
			bool attackAttemptWindow,
			InstanceContentDeepDungeon* dd)
		{
			DateTime now = DateTime.UtcNow;
			var decision = EnemyChaseRecoveryPolicy.Decide(
				targetAvailable: current != null,
				targetDead: current == null || current.IsDead || current.CurrentHp == 0,
				targetSpecificAggro: targetSpecificAggro,
				targetHpDecreased:
					current != null &&
					_preEngageTargetProgressId == target.GameObjectId &&
					current.CurrentHp < _preEngageTargetProgressHp,
				attackAttemptWindow: attackAttemptWindow,
				noProgress: _preEngageTargetProgressId == target.GameObjectId
					? now - _preEngageTargetProgressAt
					: TimeSpan.Zero,
				recoveryActive: _clearingPreEngageAirWallRecovery);

			if (_preEngageTargetProgressId != target.GameObjectId)
			{
				_preEngageTargetProgressId = target.GameObjectId;
				_preEngageTargetProgressHp = current?.CurrentHp ?? 0;
				_preEngageTargetProgressAt = now;
				_clearingPreEngageAirWallRecovery = false;
				_clearingPreEngageAirWallRecoveryAt = DateTime.MinValue;
				_clearingPreEngageTargetRoom = ResolveTargetRoomIndex(dd, target);
				return false;
			}

			if (decision == EnemyChaseRecoveryDecision.TargetProgress)
			{
				ResetPreEngageProgressOnly();
				return false;
			}

			// A target can remain selected while the player is still traversing the
			// room graph.  Only the normal attack-attempt window is evidence that the
			// player is being held by an attack-blocking wall; a long chase alone must
			// never start room-interior recovery.
			if (!attackAttemptWindow)
			{
				_preEngageTargetProgressAt = now;
				_clearingPreEngageAirWallRecovery = false;
				_clearingPreEngageAirWallRecoveryAt = DateTime.MinValue;
				_clearingPreEngageTargetRoom = ResolveTargetRoomIndex(dd, target);
				return false;
			}

			if (decision != EnemyChaseRecoveryDecision.Start)
				return decision == EnemyChaseRecoveryDecision.Continue;

			_clearingPreEngageAirWallRecovery = true;
			_clearingPreEngageAirWallRecoveryAt = now;
			_clearingPreEngageTargetRoom = ResolveTargetRoomIndex(dd, target);
			RecordReplayEvent("clearing-pre-engage-recovery-started", new
			{
				floor = dd->Floor,
				targetId = target.GameObjectId,
				reason = "no-target-specific-progress",
				seconds = (int)(now - _preEngageTargetProgressAt).TotalSeconds,
				targetRoom = _clearingPreEngageTargetRoom,
				attackAttemptWindow,
				x = target.LivePosition.X,
				y = target.LivePosition.Y,
				z = target.LivePosition.Z
			});
			return true;
		}

		private unsafe bool TryUpdateClearingPreEngageAirWallRecovery(
			InstanceContentDeepDungeon* dd,
			IPlayerCharacter player)
		{
			if (!_clearingPreEngageAirWallRecovery)
				return false;

			ulong targetId = _preEngageTargetProgressId;
			if (targetId == 0)
			{
				ResetPreEngageProgressOnly();
				return false;
			}

			var current = CombatTargetingHelpers.GetBattleCharaByGameObjectId(targetId);
			bool targetSpecificHpProgress =
				current != null && current.CurrentHp < _preEngageTargetProgressHp;
			if (current == null || current.IsDead || current.CurrentHp == 0 ||
				EnemyChaseHelper.IsAggroedToPlayer(targetId) ||
				targetSpecificHpProgress)
			{
				RecordReplayEvent("clearing-pre-engage-recovery-completed", new
				{
					floor = dd->Floor,
					targetId,
					reason = current == null || current.IsDead || current.CurrentHp == 0
						? "target-disappeared"
						: EnemyChaseHelper.IsAggroedToPlayer(targetId)
							? "target-aggroed"
							: "target-hp-progress",
					currentHp = current?.CurrentHp ?? 0,
					lastProgressHp = _preEngageTargetProgressHp
				});
				ResetPreEngageProgressOnly();
				return false;
			}

			var now = DateTime.UtcNow;
			if (now - _clearingPreEngageAirWallRecoveryAt >= ClearingPreEngageRecoveryLimit)
			{
				SuppressStalledEngageTarget(
					dd,
					targetId,
					"pre-engage-room-interior-no-progress",
					current,
					current.Position,
					_preEngageTargetProgressAt,
					_preEngageTargetProgressHp);
				return true;
			}

			if (_clearingPreEngageTargetRoom < 0 ||
				!TryResolveRoomDestination(dd, _clearingPreEngageTargetRoom, out var destination))
			{
				SuppressStalledEngageTarget(
					dd,
					targetId,
					"pre-engage-room-center-unavailable",
					current,
					current.Position,
					_preEngageTargetProgressAt,
					_preEngageTargetProgressHp);
				return true;
			}

			var state = _navHelper!.Navigate(destination, player.Position, arrivalRadius: 1.5f);
			switch (state)
			{
				case NavigationState.Moving:
					_status = "Crossing room interior to engage hostile";
					return true;
				case NavigationState.Arrived:
					_status = "Room interior reached; retrying hostile engage";
					return true;
				case NavigationState.StuckRepathing:
					_status = $"Crossing room interior to engage hostile ({_navHelper.StuckRetryCount}/3)";
					return true;
				case NavigationState.StuckGiveUp:
					SuppressStalledEngageTarget(
						dd,
						targetId,
						"pre-engage-room-center-navigation-stuck",
						current,
						current.Position,
						_preEngageTargetProgressAt,
						_preEngageTargetProgressHp);
					return true;
				case NavigationState.Failed:
					SuppressStalledEngageTarget(
						dd,
						targetId,
						"pre-engage-room-center-navigation-failed",
						current,
						current.Position,
						_preEngageTargetProgressAt,
						_preEngageTargetProgressHp);
					return true;
				default:
					return true;
			}
		}

		private unsafe int ResolveTargetRoomIndex(
			InstanceContentDeepDungeon* dd,
			EnemyChaseTarget target)
		{
			if (target.AcquisitionTargetRoomIndex >= 0)
				return target.AcquisitionTargetRoomIndex;

			IReadOnlyList<int>? rooms = _floorRuntime?.NormalGraph?.ReachableRooms;
			return rooms == null
				? -1
				: RoomGraph.GetRoomIndexForPosition(dd, target.LivePosition, rooms, -1);
		}

		private unsafe bool TryUpdateClearingEngageRecentering(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
		{
			if (!_clearingEngageRecentering)
				return false;

			var targetId = _engagedTargetProgressId;
			if (targetId == 0)
			{
				ResetEngagedTargetProgress();
				return false;
			}

			var current = CombatTargetingHelpers.GetBattleCharaByGameObjectId(targetId);
			if (current == null)
			{
				ResetEngagedTargetProgress();
				return false;
			}

			if (current.CurrentHp < _engagedTargetProgressHp)
			{
				RecordReplayEvent("clearing-engage-recenter-completed", new
				{
					floor = dd->Floor,
					targetId,
					reason = "hp-progress-resumed",
					currentHp = current.CurrentHp,
					lastProgressHp = _engagedTargetProgressHp
				});
				ResetEngagedTargetProgress();
				return false;
			}

			var now = DateTime.UtcNow;
			if (now - _clearingEngageRecenteringAt >= ClearingEngageRecenterFallbackLimit)
			{
				SuppressStalledEngageTarget(dd, targetId, "room-center-recovery-no-progress", current, current.Position);
				return true;
			}

			int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
			if (playerRoom < 0 || !TryResolveRoomDestination(dd, playerRoom, out var dest))
			{
				SuppressStalledEngageTarget(dd, targetId, "room-center-unavailable", current, current.Position);
				return true;
			}

			var state = _navHelper!.Navigate(dest, player.Position, arrivalRadius: 1.5f);
			switch (state)
			{
				case NavigationState.Moving:
					_status = "Recentering in room after stalled engage";
					return true;
				case NavigationState.Arrived:
					RecordReplayEvent("clearing-engage-recenter-completed", new
					{
						floor = dd->Floor,
						targetId,
						reason = "arrived-room-center",
						playerRoom,
						currentHp = current.CurrentHp,
						lastProgressHp = _engagedTargetProgressHp,
						x = dest.X,
						y = dest.Y,
						z = dest.Z
					});
					_status = "Recentered, retrying hostile";
					ResetEngagedTargetProgress();
					return false;
				case NavigationState.StuckRepathing:
					_status = $"Recentering in room after stalled engage ({_navHelper.StuckRetryCount}/3)";
					return true;
				case NavigationState.StuckGiveUp:
					SuppressStalledEngageTarget(dd, targetId, "room-center-navigation-stuck", current, current.Position);
					return true;
				case NavigationState.Failed:
					SuppressStalledEngageTarget(dd, targetId, "room-center-navigation-failed", current, current.Position);
					return true;
				default:
					return true;
			}
		}

		private unsafe void SuppressStalledEngageTarget(
			InstanceContentDeepDungeon* dd,
			ulong targetId,
			string reason,
			IBattleChara current,
			Vector3 position,
			DateTime? progressAt = null,
			uint? progressHp = null)
		{
			_chaseHelper.DeprioritizeCurrentTarget();
			_ctx?.SuppressCombatTarget(targetId, CombatTargetSuppressionDuration);
			_ctx?.ClearPreferredAggroTarget();
			_navHelper?.Cancel();
			RecordReplayEvent("clearing-target-deprioritized", new
			{
				floor = dd->Floor,
				targetId,
				reason,
				seconds = (int)(DateTime.UtcNow - (progressAt ?? _engagedTargetProgressAt)).TotalSeconds,
				currentHp = current.CurrentHp,
				lastProgressHp = progressHp ?? _engagedTargetProgressHp,
				x = position.X,
				y = position.Y,
				z = position.Z
			});
			ResetEngagedTargetProgress();
			_status = "Switching target after stalled engage";
		}

		private void ResetEngagedTargetProgress()
		{
			ResetEngagedProgressOnly();
			ResetPreEngageProgressOnly();
		}

		private void ResetEngagedProgressOnly()
		{
			var execution = _floorRuntime?.ActiveExecution;
			if (execution == null)
				return;
			execution.EngagedTargetProgressId = 0;
			execution.EngagedTargetProgressHp = 0;
			execution.EngagedTargetProgressAt = DateTime.MinValue;
			execution.ClearingEngageRecentering = false;
			execution.ClearingEngageRecenteringAt = DateTime.MinValue;
		}

		private void ResetPreEngageProgressOnly()
		{
			var execution = _floorRuntime?.ActiveExecution;
			if (execution == null)
				return;

			execution.PreEngageTargetProgressId = 0;
			execution.PreEngageTargetProgressHp = 0;
			execution.PreEngageTargetProgressAt = DateTime.MinValue;
			execution.ClearingPreEngageAirWallRecovery = false;
			execution.ClearingPreEngageAirWallRecoveryAt = DateTime.MinValue;
			execution.ClearingPreEngageTargetRoom = -1;
		}

		private unsafe bool TryResolveRoomDestination(InstanceContentDeepDungeon* dd, int roomIndex, out Vector3 dest)
		{
			dest = Vector3.Zero;
			if (roomIndex < 0 || dd == null)
				return false;

			if (MapPos.TryGetRoomCenter(dd, roomIndex, out dest))
			{
				return true;
			}

			return false;
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
		private static readonly TimeSpan NoProgressLimit = TimeSpan.FromSeconds(15);

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
			return noProgress >= NoProgressLimit
				? EnemyChaseRecoveryDecision.Start
				: EnemyChaseRecoveryDecision.None;
		}
	}
}
