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
		private EnemyChaseTarget? _lastTarget;
		public Func<IBattleChara, bool>? CanSelectNewTarget;
		public Func<ulong, bool>? IsTargetBlocked;
		private readonly HashSet<uint> _reportedAvoidedNames = new();
		private int _lastTopologyPlayerRoomIndex = -1;

		/// <summary>
		/// Returns a clearing target: aggroed enemy preferred, then a live sticky target,
		/// then the hostile in the topologically nearest room. A live sticky target is
		/// re-ranked when the player enters another room, but is replaced only by a
		/// hostile in a strictly closer room-hop tier.
		/// Returns null with an explicit acquisition failure when a new target cannot
		/// be ranked from the stable floor topology.
		/// </summary>
		public unsafe EnemyChaseTarget? GetClearingTarget(
			RunContext? context,
			InstanceContentDeepDungeon* dd,
			NormalFloorGraphSnapshot? normalGraph,
			int playerRoomIndex,
			Vector3 playerPosition,
            out EnemyChaseAcquisitionFailure acquisitionFailure,
            bool allowNewTargets = true,
            Func<IBattleChara, bool>? canEngage = null)
		{
			acquisitionFailure = EnemyChaseAcquisitionFailure.None;

			// 1. Prefer aggroed hostile (from hate list)
			var aggroed = CombatTargetingHelpers.PickExcludedAggroTarget(context, canEngage ?? (static _ => true))
				?? PickAggroedHostileAnyRange(playerPosition, canEngage);
            if (aggroed == null && canEngage != null)
                aggroed = CombatTargetingHelpers.PickExcludedAggroTarget(context, static _ => true)
                    ?? PickAggroedHostileAnyRange(playerPosition, null);
			if (aggroed != null)
			{
				return TrackAndReturn(aggroed);
			}

			if (!allowNewTargets)
			{
                acquisitionFailure = EnemyChaseAcquisitionFailure.NoObservedHostile;
				_currentTargetId = 0;
				_lastTarget = null;
				return null;
			}

			// 2. Persist existing clearing target if still alive
			if (_currentTargetId != 0 && !IsDeprioritized(_currentTargetId))
			{
				var existing = CombatTargetingHelpers.GetBattleCharaByGameObjectId(_currentTargetId);
				if (existing != null && existing.IsTargetable && !existing.IsDead && IsNewTargetAllowed(existing))
				{
					if (playerRoomIndex != _lastTopologyPlayerRoomIndex &&
					    TryGetGraphHops(
						    dd,
						    normalGraph,
						    playerRoomIndex,
						    existing.Position,
						    out int currentTargetHops))
					{
						var revalidatedNearest = PickTopologyNearestHostileAnyRange(
							dd,
							normalGraph,
							playerRoomIndex,
							playerPosition,
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
								return TrackAndReturn(revalidatedNearest, playerRoomIndex);
							}
						}
					}

					return TrackAndReturn(existing);
				}

				_currentTargetId = 0;
				_lastTarget = null;
				_lastTopologyPlayerRoomIndex = -1;
			}

			// 3. Acquire by room-graph hops, using XZ distance only within an equal-hop room tier.
			var nearest = PickTopologyNearestHostileAnyRange(
				dd,
				normalGraph,
				playerRoomIndex,
				playerPosition,
				out _,
				out acquisitionFailure);
			if (nearest == null) return null;
			return TrackAndReturn(nearest, playerRoomIndex);
		}

		/// <summary>The sticky target as last RETURNED to a caller —
		/// read-only observation for diagnostics/previews; never acquires
		/// or re-ranks anything.</summary>
		public EnemyChaseTarget? LastTarget =>
			_currentTargetId != 0 ? _lastTarget : null;

		public void ReleaseTarget(ulong targetId)
		{
			if (targetId != _currentTargetId)
				return;
			_currentTargetId = 0;
			_lastTarget = null;
			_lastTopologyPlayerRoomIndex = -1;
		}

		public void Reset()
		{
			_currentTargetId = 0;
			_lastTarget = null;
			_lastTopologyPlayerRoomIndex = -1;
			_reportedAvoidedNames.Clear();
		}

		private IBattleChara? PickAggroedHostileAnyRange(Vector3 playerPosition,
            Func<IBattleChara, bool>? canEngage)
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

				if (!IsAggroedToPlayer(bnpc.GameObjectId) || canEngage?.Invoke(bnpc) == false)
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

        private bool IsNewTargetAllowed(IBattleChara target)
        {
            if (CanSelectNewTarget?.Invoke(target) != false) return true;
            if (_reportedAvoidedNames.Add(target.NameId))
                Service.Log.Info($"[MobPull] avoiding new target={target.GameObjectId} nameId={target.NameId} baseId={target.BaseId}");
            return false;
        }

		private unsafe IBattleChara? PickTopologyNearestHostileAnyRange(
			InstanceContentDeepDungeon* dd,
			NormalFloorGraphSnapshot? normalGraph,
			int playerRoomIndex,
			Vector3 playerPosition,
			out int graphHops,
			out EnemyChaseAcquisitionFailure acquisitionFailure)
		{
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
            bool foundExcludedHostile = false;

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

				if (!IsNewTargetAllowed(bnpc))
                {
                    foundExcludedHostile = true;
                    continue;
                }

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
					graphHops = candidateHops;
				}
			}

			if (selected == null)
			{
				if (foundUnboundHostile)
					acquisitionFailure = EnemyChaseAcquisitionFailure.TargetRoomUnavailable;
				else if (foundUnreachableHostile)
					acquisitionFailure = EnemyChaseAcquisitionFailure.TargetRoomUnreachable;
                else
                    acquisitionFailure = foundExcludedHostile ? EnemyChaseAcquisitionFailure.Excluded : EnemyChaseAcquisitionFailure.NoObservedHostile;
			}

			return selected;
		}

		private static unsafe bool TryGetGraphHops(
			InstanceContentDeepDungeon* dd,
			NormalFloorGraphSnapshot? normalGraph,
			int playerRoomIndex,
			Vector3 targetPosition,
			out int graphHops)
		{
			graphHops = -1;
			if (dd == null ||
			    normalGraph == null ||
			    !ContainsRoom(normalGraph.ReachableRooms, playerRoomIndex))
			{
				return false;
			}

			int targetRoomIndex = RoomGraph.GetRoomIndexForPosition(
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

		public static bool IsAggroedToPlayer(ulong targetId) => ReadAggro(targetId) == true;

		internal static unsafe bool? ReadAggro(ulong targetId)
		{
			if (targetId == 0)
				return null;

			var uiState = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
			if (uiState == null)
				return null;

			var hater = &uiState->Hater;
			var count = Math.Clamp(hater->HaterCount, 0, 32);
			for (int i = 0; i < count; i++)
			{
				if (hater->Haters[i].EntityId == targetId)
					return true;
			}

			return false;
		}

		private bool IsDeprioritized(ulong targetId) => IsTargetBlocked?.Invoke(targetId) == true;

		private EnemyChaseTarget TrackAndReturn(IBattleChara target, int? playerRoom = null)
		{
			if (target.GameObjectId != _currentTargetId)
			{
				_currentTargetId = target.GameObjectId;
				_lastTopologyPlayerRoomIndex = playerRoom ?? -1;
			}

			var selected = new EnemyChaseTarget(target.GameObjectId, target.Position);
			_lastTarget = selected;
			return selected;
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

	internal readonly record struct EnemyChaseTarget(ulong GameObjectId, Vector3 LivePosition);

	internal enum EnemyChaseAcquisitionFailure
	{
		None,
		TopologyUnavailable,
		PlayerRoomUnavailable,
		TargetRoomUnavailable,
		TargetRoomUnreachable,
        Excluded,
        NoObservedHostile,
        AttackableWithoutRoomIndex
	}
}
