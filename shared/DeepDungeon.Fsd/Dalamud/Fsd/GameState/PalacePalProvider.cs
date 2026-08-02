using System;
using System.Collections.Generic;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud.Map;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.GameState
{
	/// <summary>
	/// Provides PalacePal trap data for the current territory.
	/// Wraps the static PalacePalData methods.
	/// </summary>
	public sealed class PalacePalProvider
	{
		private const float RoomRadius = 30f;
		private const float RoomLayerTolerance = 8f;

		private byte _cachedFloor = 255;
		private ushort _cachedTerritory;
		private int _cachedLayout = -1;
		private readonly Dictionary<int, List<Vector3>> _trapsByRoom = new();

		public unsafe IReadOnlyList<Vector3> GetTrapPositionsForRoom(InstanceContentDeepDungeon* dd, int roomIndex)
		{
			EnsureRoomIndex(dd);
			return _trapsByRoom.TryGetValue(roomIndex, out var positions) ? positions : Array.Empty<Vector3>();
		}

		public unsafe IReadOnlyList<Vector3> GetCandidatePositionsForRoom(InstanceContentDeepDungeon* dd, int roomIndex)
		{
			EnsureRoomIndex(dd);
			return _trapsByRoom.TryGetValue(roomIndex, out var positions) ? positions : Array.Empty<Vector3>();
		}

		private unsafe void EnsureRoomIndex(InstanceContentDeepDungeon* dd)
		{
			if (dd == null)
				return;

			ushort territory = (ushort)Service.ClientState.TerritoryType;
			if (_cachedFloor == dd->Floor &&
			    _cachedTerritory == territory &&
			    _cachedLayout == dd->ActiveLayoutIndex)
				return;

			_cachedFloor = dd->Floor;
			_cachedTerritory = territory;
			_cachedLayout = dd->ActiveLayoutIndex;
			RebuildRoomIndex(dd);
		}

		private unsafe void RebuildRoomIndex(InstanceContentDeepDungeon* dd)
		{
			_trapsByRoom.Clear();

			IndexPositions(dd, PalacePalData.GetCandidatePositionsCurrentTerritory(), _trapsByRoom);
		}

		private static unsafe void IndexPositions(InstanceContentDeepDungeon* dd, IReadOnlyList<Vector3> positions, Dictionary<int, List<Vector3>> target)
		{
			for (int i = 0; i < positions.Count; i++)
			{
				int roomIndex = FindRoomContaining(dd, positions[i]);
				if (roomIndex < 0)
					continue;

				if (!target.TryGetValue(roomIndex, out var list))
				{
					list = new List<Vector3>();
					target[roomIndex] = list;
				}

				bool duplicate = false;
				for (int existingIndex = 0; existingIndex < list.Count; existingIndex++)
				{
					if (Vector3.DistanceSquared(list[existingIndex], positions[i]) <= 0.100001f * 0.100001f)
					{
						duplicate = true;
						break;
					}
				}

				if (!duplicate)
					list.Add(positions[i]);
			}
		}

		private static unsafe int FindRoomContaining(InstanceContentDeepDungeon* dd, Vector3 position)
		{
			for (int roomIndex = 0; roomIndex < 25; roomIndex++)
			{
				if (!MapPos.TryGetRoomCenter(dd, roomIndex, out var center))
				{
					continue;
				}

				float dx = position.X - center.X;
				float dz = position.Z - center.Z;
				float dy = position.Y - center.Y;
				if (MathF.Abs(dy) > RoomLayerTolerance)
					continue;

				if (dx * dx + dz * dz <= RoomRadius * RoomRadius)
					return roomIndex;
			}

			return -1;
		}
	}
}
