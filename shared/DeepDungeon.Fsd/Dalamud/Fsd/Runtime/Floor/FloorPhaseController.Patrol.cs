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

				bool engaged =
					Service.Condition[ConditionFlag.InCombat] ||
					target.Value.Reason == EnemyChaseTargetReason.Aggro;
				bool casting = Service.Condition[ConditionFlag.Casting];
				bool withinLiveTargetHoldRange = IsWithinLiveTargetHoldRange(target.Value, player.Position);
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
						ResetEngagedTargetProgress();
					}
				}
				else
				{
					ResetEngagedTargetProgress();
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

		private unsafe void SuppressStalledEngageTarget(InstanceContentDeepDungeon* dd, ulong targetId, string reason, IBattleChara current, Vector3 position)
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
				seconds = (int)(DateTime.UtcNow - _engagedTargetProgressAt).TotalSeconds,
				currentHp = current.CurrentHp,
				lastProgressHp = _engagedTargetProgressHp,
				x = position.X,
				y = position.Y,
				z = position.Z
			});
			ResetEngagedTargetProgress();
			_status = "Switching target after stalled engage";
		}

		private void ResetEngagedTargetProgress()
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
}
