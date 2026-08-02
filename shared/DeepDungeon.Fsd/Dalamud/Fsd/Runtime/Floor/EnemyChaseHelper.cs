using System;
using System.Collections.Generic;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using global::Dalamud.Game.ClientState.Objects.Types;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor
{
	internal sealed class EnemyChaseHelper
	{
		private ulong _currentTargetId;
		private Vector3 _legEndpoint;
		private bool _hasLegEndpoint;
		private int _lastTopologyPlayerRoomIndex = -1;
		private readonly Dictionary<ulong, DateTime> _deprioritizedTargets = new();

		public const float NavigationArrivalTolerance = 3f;
		public const float LiveTargetHoldRange = 3f;
		private static readonly TimeSpan DeprioritizeDuration = TimeSpan.FromSeconds(20);

		/// <summary>
		/// Returns a clearing target: aggroed enemy preferred, then a live sticky target,
		/// then the hostile in the topologically nearest room. A live sticky target is
		/// re-ranked when the player enters another room, but is replaced only by a
		/// hostile in a strictly closer room-hop tier.
		/// Target identity remains sticky while each navigation leg uses an immutable
		/// endpoint. The caller explicitly completes a leg before the live position is
		/// sampled for the next one.
		/// Returns null with an explicit acquisition failure when a new target cannot
		/// be ranked from the stable floor topology.
		/// </summary>
		public unsafe EnemyChaseTarget? GetClearingTarget(
			InstanceContentDeepDungeon* dd,
			NormalFloorGraphSnapshot? normalGraph,
			int playerRoomIndex,
			Vector3 playerPosition,
			out EnemyChaseAcquisitionFailure acquisitionFailure)
		{
			acquisitionFailure = EnemyChaseAcquisitionFailure.None;
			PruneDeprioritizedTargets();

			// 1. Prefer aggroed hostile (from hate list)
			var aggroed = PickAggroedHostileAnyRange(playerPosition);
			if (aggroed != null)
			{
				return TrackAndReturn(
					aggroed.GameObjectId,
					aggroed.Position,
					aggroed.HitboxRadius,
					EnemyChaseTargetReason.Aggro);
			}

			// 2. Persist existing clearing target if still alive
			if (_currentTargetId != 0 && !IsDeprioritized(_currentTargetId))
			{
				var existing = CombatTargetingHelpers.GetBattleCharaByGameObjectId(_currentTargetId);
				if (existing != null && existing.IsTargetable && !existing.IsDead)
				{
					if (playerRoomIndex != _lastTopologyPlayerRoomIndex &&
					    TryGetGraphHops(
						    dd,
						    normalGraph,
						    playerRoomIndex,
						    existing.Position,
						    out _,
						    out int currentTargetHops))
					{
						var revalidatedNearest = PickTopologyNearestHostileAnyRange(
							dd,
							normalGraph,
							playerRoomIndex,
							playerPosition,
							out int revalidatedTargetRoomIndex,
							out int revalidatedGraphHops,
							out _);
						if (revalidatedNearest != null)
						{
							_lastTopologyPlayerRoomIndex = playerRoomIndex;
							if (revalidatedNearest.GameObjectId != existing.GameObjectId &&
							    EnemyChaseTopologyRanking.ShouldReplaceSticky(
								    currentTargetHops,
								    revalidatedGraphHops))
							{
								_currentTargetId = revalidatedNearest.GameObjectId;
								_legEndpoint = revalidatedNearest.Position;
								_hasLegEndpoint = true;
								return new EnemyChaseTarget(
									_legEndpoint,
									revalidatedNearest.Position,
									revalidatedNearest.HitboxRadius,
									revalidatedNearest.GameObjectId,
									EnemyChaseTargetReason.CloserAfterRoomChange,
									playerRoomIndex,
									revalidatedTargetRoomIndex,
									revalidatedGraphHops);
							}
						}
					}

					return TrackAndReturn(
						existing.GameObjectId,
						existing.Position,
						existing.HitboxRadius,
						EnemyChaseTargetReason.Sticky);
				}

				_currentTargetId = 0;
				_legEndpoint = Vector3.Zero;
				_hasLegEndpoint = false;
				_lastTopologyPlayerRoomIndex = -1;
			}

			// 3. Acquire by room-graph hops, using XZ distance only within an equal-hop room tier.
			var nearest = PickTopologyNearestHostileAnyRange(
				dd,
				normalGraph,
				playerRoomIndex,
				playerPosition,
				out int targetRoomIndex,
				out int graphHops,
				out acquisitionFailure);
			if (nearest != null)
			{
				_currentTargetId = nearest.GameObjectId;
				_legEndpoint = nearest.Position;
				_hasLegEndpoint = true;
				_lastTopologyPlayerRoomIndex = playerRoomIndex;
				return new EnemyChaseTarget(
					_legEndpoint,
					nearest.Position,
					nearest.HitboxRadius,
					nearest.GameObjectId,
					EnemyChaseTargetReason.Nearest,
					playerRoomIndex,
					targetRoomIndex,
					graphHops);
			}

			return null;
		}

		/// <summary>
		/// Ends the immutable navigation leg without releasing the sticky target.
		/// The next GetClearingTarget call samples the target's current live position.
		/// </summary>
		public void CompleteCurrentLeg()
		{
			_hasLegEndpoint = false;
		}

		public void DeprioritizeCurrentTarget()
		{
			if (_currentTargetId == 0)
				return;

			_deprioritizedTargets[_currentTargetId] = DateTime.UtcNow + DeprioritizeDuration;
			_currentTargetId = 0;
			_legEndpoint = Vector3.Zero;
			_hasLegEndpoint = false;
			_lastTopologyPlayerRoomIndex = -1;
		}

		public void Reset()
		{
			_currentTargetId = 0;
			_legEndpoint = Vector3.Zero;
			_hasLegEndpoint = false;
			_lastTopologyPlayerRoomIndex = -1;
			_deprioritizedTargets.Clear();
		}

		private IBattleChara? PickAggroedHostileAnyRange(Vector3 playerPosition)
		{
			IBattleChara? selected = null;
			float selectedDistSq = float.MaxValue;

			foreach (var obj in Service.GameObjects)
			{
				if (obj is not IBattleChara bnpc ||
				    obj.ObjectKind != global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc ||
				    (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)obj.SubKind != global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant ||
				    !bnpc.IsTargetable ||
				    bnpc.IsDead)
				{
					continue;
				}

				if (!IsAggroedToPlayer(bnpc.GameObjectId))
					continue;

				if (IsDeprioritized(bnpc.GameObjectId))
					continue;

				if (_currentTargetId != 0 && bnpc.GameObjectId == _currentTargetId)
					return bnpc;

				var distSq = DistSqXZ(bnpc.Position, playerPosition);
				if (distSq < selectedDistSq)
				{
					selected = bnpc;
					selectedDistSq = distSq;
				}
			}

			return selected;
		}

		private unsafe IBattleChara? PickTopologyNearestHostileAnyRange(
			InstanceContentDeepDungeon* dd,
			NormalFloorGraphSnapshot? normalGraph,
			int playerRoomIndex,
			Vector3 playerPosition,
			out int targetRoomIndex,
			out int graphHops,
			out EnemyChaseAcquisitionFailure acquisitionFailure)
		{
			targetRoomIndex = -1;
			graphHops = -1;
			acquisitionFailure = EnemyChaseAcquisitionFailure.None;

			if (dd == null || normalGraph == null)
			{
				acquisitionFailure = EnemyChaseAcquisitionFailure.TopologyUnavailable;
				return null;
			}

			if (!ContainsRoom(normalGraph.ReachableRooms, playerRoomIndex))
			{
				acquisitionFailure = EnemyChaseAcquisitionFailure.PlayerRoomUnavailable;
				return null;
			}

			IBattleChara? selected = null;
			EnemyChaseCandidateRank selectedRank = default;
			bool foundUnboundHostile = false;
			bool foundUnreachableHostile = false;

			foreach (var obj in Service.GameObjects)
			{
				if (obj is not IBattleChara bnpc ||
				    obj.ObjectKind != global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc ||
				    (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)obj.SubKind != global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant ||
				    !bnpc.IsTargetable ||
				    bnpc.IsDead)
				{
					continue;
				}

				if (IsDeprioritized(bnpc.GameObjectId))
					continue;

				int candidateRoom = RoomGraph.GetRoomIndexForPosition(
					dd,
					bnpc.Position,
					normalGraph.ReachableRooms,
					-1);
				if (candidateRoom < 0)
				{
					foundUnboundHostile = true;
					continue;
				}

				int candidateHops = normalGraph.RoomDistances[playerRoomIndex, candidateRoom];
				if (candidateHops < 0 || candidateHops >= 999)
				{
					foundUnreachableHostile = true;
					continue;
				}

				var candidateRank = new EnemyChaseCandidateRank(
					candidateHops,
					DistSqXZ(bnpc.Position, playerPosition),
					bnpc.GameObjectId);
				if (selected == null || EnemyChaseTopologyRanking.IsBetter(candidateRank, selectedRank))
				{
					selected = bnpc;
					selectedRank = candidateRank;
					targetRoomIndex = candidateRoom;
					graphHops = candidateHops;
				}
			}

			if (selected == null)
			{
				if (foundUnboundHostile)
					acquisitionFailure = EnemyChaseAcquisitionFailure.TargetRoomUnavailable;
				else if (foundUnreachableHostile)
					acquisitionFailure = EnemyChaseAcquisitionFailure.TargetRoomUnreachable;
			}

			return selected;
		}

		private static unsafe bool TryGetGraphHops(
			InstanceContentDeepDungeon* dd,
			NormalFloorGraphSnapshot? normalGraph,
			int playerRoomIndex,
			Vector3 targetPosition,
			out int targetRoomIndex,
			out int graphHops)
		{
			targetRoomIndex = -1;
			graphHops = -1;
			if (dd == null ||
			    normalGraph == null ||
			    !ContainsRoom(normalGraph.ReachableRooms, playerRoomIndex))
			{
				return false;
			}

			targetRoomIndex = RoomGraph.GetRoomIndexForPosition(
				dd,
				targetPosition,
				normalGraph.ReachableRooms,
				-1);
			if (targetRoomIndex < 0)
				return false;

			graphHops = normalGraph.RoomDistances[playerRoomIndex, targetRoomIndex];
			return graphHops >= 0 && graphHops < 999;
		}

		private static bool ContainsRoom(IReadOnlyList<int> rooms, int roomIndex)
		{
			for (int i = 0; i < rooms.Count; i++)
			{
				if (rooms[i] == roomIndex)
					return true;
			}

			return false;
		}

		private static unsafe bool IsAggroedToPlayer(ulong targetId)
		{
			if (targetId == 0)
				return false;

			var uiState = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
			if (uiState == null)
				return false;

			var hater = &uiState->Hater;
			var count = Math.Clamp(hater->HaterCount, 0, 32);
			for (int i = 0; i < count; i++)
			{
				if (hater->Haters[i].EntityId == targetId)
					return true;
			}

			return false;
		}

		private bool IsDeprioritized(ulong targetId)
		{
			return _deprioritizedTargets.TryGetValue(targetId, out var expiresAt) &&
			       DateTime.UtcNow < expiresAt;
		}

		private void PruneDeprioritizedTargets()
		{
			if (_deprioritizedTargets.Count == 0)
				return;

			var now = DateTime.UtcNow;
			Span<ulong> expired = stackalloc ulong[Math.Min(_deprioritizedTargets.Count, 32)];
			int expiredCount = 0;
			foreach (var pair in _deprioritizedTargets)
			{
				if (pair.Value > now)
					continue;

				if (expiredCount >= expired.Length)
					break;

				expired[expiredCount++] = pair.Key;
			}

			for (int i = 0; i < expiredCount; i++)
				_deprioritizedTargets.Remove(expired[i]);
		}

		private EnemyChaseTarget TrackAndReturn(
			ulong id,
			Vector3 currentPos,
			float hitboxRadius,
			EnemyChaseTargetReason reason)
		{
			if (id != _currentTargetId)
			{
				_currentTargetId = id;
				_hasLegEndpoint = false;
				_lastTopologyPlayerRoomIndex = -1;
			}

			if (!_hasLegEndpoint)
			{
				_legEndpoint = currentPos;
				_hasLegEndpoint = true;
			}

			return new EnemyChaseTarget(_legEndpoint, currentPos, hitboxRadius, id, reason, -1, -1, -1);
		}

		private static float DistSqXZ(Vector3 a, Vector3 b)
		{
			float dx = a.X - b.X;
			float dz = a.Z - b.Z;
			return dx * dx + dz * dz;
		}
	}

	internal readonly record struct EnemyChaseCandidateRank(
		int GraphHops,
		float DistanceSquared,
		ulong GameObjectId);

	internal static class EnemyChaseTopologyRanking
	{
		public static bool ShouldReplaceSticky(int currentGraphHops, int candidateGraphHops)
		{
			return candidateGraphHops < currentGraphHops;
		}

		public static bool IsBetter(in EnemyChaseCandidateRank candidate, in EnemyChaseCandidateRank incumbent)
		{
			if (candidate.GraphHops != incumbent.GraphHops)
				return candidate.GraphHops < incumbent.GraphHops;

			int distanceComparison = candidate.DistanceSquared.CompareTo(incumbent.DistanceSquared);
			return distanceComparison != 0
				? distanceComparison < 0
				: candidate.GameObjectId < incumbent.GameObjectId;
		}
	}

	internal readonly struct EnemyChaseTarget
	{
		public EnemyChaseTarget(
			Vector3 position,
			Vector3 livePosition,
			float hitboxRadius,
			ulong gameObjectId,
			EnemyChaseTargetReason reason,
			int acquisitionPlayerRoomIndex,
			int acquisitionTargetRoomIndex,
			int acquisitionGraphHops)
		{
			Position = position;
			LivePosition = livePosition;
			HitboxRadius = hitboxRadius;
			GameObjectId = gameObjectId;
			Reason = reason;
			AcquisitionPlayerRoomIndex = acquisitionPlayerRoomIndex;
			AcquisitionTargetRoomIndex = acquisitionTargetRoomIndex;
			AcquisitionGraphHops = acquisitionGraphHops;
		}

		public Vector3 Position { get; }
		public Vector3 LivePosition { get; }
		public float HitboxRadius { get; }
		public ulong GameObjectId { get; }
		public EnemyChaseTargetReason Reason { get; }
		public int AcquisitionPlayerRoomIndex { get; }
		public int AcquisitionTargetRoomIndex { get; }
		public int AcquisitionGraphHops { get; }
	}

	internal enum EnemyChaseTargetReason
	{
		Aggro,
		Sticky,
		Nearest,
		CloserAfterRoomChange
	}

	internal enum EnemyChaseAcquisitionFailure
	{
		None,
		TopologyUnavailable,
		PlayerRoomUnavailable,
		TargetRoomUnavailable,
		TargetRoomUnreachable
	}
}
