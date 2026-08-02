using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using DeepDungeon.Fsd.Dalamud.Map;

namespace DeepDungeon.Fsd.Dalamud.GameState
{
	/// <summary>
	/// Unified room graph: room indices, reachability, BFS, and distance cache.
	/// Merges the old DeepDungeonRooms + AutoPilot/Core/RoomGraph with a shared
	/// EnumerateNeighbors helper that eliminates the repeated 4-direction expansion.
	/// </summary>
	internal static class RoomGraph
	{
		public const int MaxRooms = 25;

		/// <summary>
		/// Yields valid connected neighbor indices for a room.
		/// All BFS/Floyd-Warshall methods delegate to this single implementation.
		/// </summary>
		public static unsafe void EnumerateNeighbors(
			InstanceContentDeepDungeon* dd, int roomIndex,
			Span<int> neighbors, out int count)
		{
			count = 0;
			var map = dd->MapData;
			int row = roomIndex / 5;
			int col = roomIndex % 5;
			var flags = map[roomIndex];

			if ((flags & InstanceContentDeepDungeon.RoomFlags.ConnectionN) != 0 && row > 0)
			{
				int n = roomIndex - 5;
				if ((map[n] & InstanceContentDeepDungeon.RoomFlags.ConnectionS) != 0)
					neighbors[count++] = n;
			}
			if ((flags & InstanceContentDeepDungeon.RoomFlags.ConnectionS) != 0 && row < 4)
			{
				int s = roomIndex + 5;
				if ((map[s] & InstanceContentDeepDungeon.RoomFlags.ConnectionN) != 0)
					neighbors[count++] = s;
			}
			if ((flags & InstanceContentDeepDungeon.RoomFlags.ConnectionW) != 0 && col > 0)
			{
				int w = roomIndex - 1;
				if ((map[w] & InstanceContentDeepDungeon.RoomFlags.ConnectionE) != 0)
					neighbors[count++] = w;
			}
			if ((flags & InstanceContentDeepDungeon.RoomFlags.ConnectionE) != 0 && col < 4)
			{
				int e = roomIndex + 1;
				if ((map[e] & InstanceContentDeepDungeon.RoomFlags.ConnectionW) != 0)
					neighbors[count++] = e;
			}
		}

		// ===== Room Index Lookups =====

		public static unsafe int GetHomeRoomIndex(InstanceContentDeepDungeon* dd)
		{
			if (dd == null) return -1;
			try
			{
				var map = dd->MapData;
				for (int i = 0; i < map.Length; i++)
					if ((map[i] & InstanceContentDeepDungeon.RoomFlags.Home) != 0)
						return i;
			}
			catch { }
			return -1;
		}

		public static unsafe int GetLocalPlayerRoomIndex(InstanceContentDeepDungeon* dd)
		{
			if (dd == null) return -1;
			try
			{
				var local = Service.LocalPlayer;
				if (local != null)
				{
					uint id = (uint)local.GameObjectId;
					var party = dd->Party;
					for (int i = 0; i < party.Length; i++)
						if (party[i].EntityId == id)
							return party[i].RoomIndex;
				}
			}
			catch { }
			return -1;
		}

		public static unsafe int GetPassageRoomIndex(InstanceContentDeepDungeon* dd)
		{
			if (dd == null) return -1;
			try
			{
				var map = dd->MapData;
				for (int i = 0; i < map.Length; i++)
					if ((map[i] & InstanceContentDeepDungeon.RoomFlags.Passage) != 0)
						return i;
			}
			catch { }
			return -1;
		}

		// ===== BFS =====

		public static unsafe List<int> BuildReachableRoomOrder(InstanceContentDeepDungeon* dd, int startRoom)
		{
			var order = new List<int>(MaxRooms);
			if (dd == null) return order;
			if (startRoom < 0 || startRoom >= MaxRooms) startRoom = 0;

			try
			{
				Span<bool> visited = stackalloc bool[MaxRooms];
				Span<int> nbuf = stackalloc int[4];
				var q = new Queue<int>();

				visited[startRoom] = true;
				q.Enqueue(startRoom);

				while (q.Count > 0)
				{
					var cur = q.Dequeue();
					order.Add(cur);

					EnumerateNeighbors(dd, cur, nbuf, out int nc);
					for (int i = 0; i < nc; i++)
					{
						int n = nbuf[i];
						if (!visited[n])
						{
							visited[n] = true;
							q.Enqueue(n);
						}
					}
				}
			}
			catch { }
			return order;
		}

		public static unsafe bool IsRoomReachable(InstanceContentDeepDungeon* dd, int fromRoom, int toRoom)
		{
			if (dd == null) return false;
			if (fromRoom == toRoom) return true;
			if (fromRoom < 0 || fromRoom >= MaxRooms || toRoom < 0 || toRoom >= MaxRooms) return false;

			try
			{
				Span<bool> visited = stackalloc bool[MaxRooms];
				Span<int> nbuf = stackalloc int[4];
				var q = new Queue<int>();

				visited[fromRoom] = true;
				q.Enqueue(fromRoom);

				while (q.Count > 0)
				{
					var cur = q.Dequeue();
					if (cur == toRoom) return true;

					EnumerateNeighbors(dd, cur, nbuf, out int nc);
					for (int i = 0; i < nc; i++)
					{
						int n = nbuf[i];
						if (!visited[n])
						{
							visited[n] = true;
							q.Enqueue(n);
						}
					}
				}
			}
			catch { }
			return false;
		}

		public static unsafe bool TryBuildRoomRoute(InstanceContentDeepDungeon* dd, int startRoom, int targetRoom, List<int> route)
		{
			route.Clear();
			if (dd == null) return false;
			if (startRoom < 0 || startRoom >= MaxRooms || targetRoom < 0 || targetRoom >= MaxRooms) return false;
			if (startRoom == targetRoom)
			{
				route.Add(startRoom);
				return true;
			}

			try
			{
				Span<int> parent = stackalloc int[MaxRooms];
				Span<int> nbuf = stackalloc int[4];
				for (int i = 0; i < MaxRooms; i++) parent[i] = -1;

				var q = new Queue<int>();
				parent[startRoom] = startRoom;
				q.Enqueue(startRoom);

				while (q.Count > 0)
				{
					var cur = q.Dequeue();
					if (cur == targetRoom) break;

					EnumerateNeighbors(dd, cur, nbuf, out int nc);
					for (int i = 0; i < nc; i++)
					{
						int n = nbuf[i];
						if (parent[n] == -1)
						{
							parent[n] = cur;
							q.Enqueue(n);
						}
					}
				}

				if (parent[targetRoom] == -1) return false;

				var stack = new Stack<int>();
				int walk = targetRoom;
				while (walk != startRoom)
				{
					stack.Push(walk);
					walk = parent[walk];
					if (walk == -1) break;
				}
				if (walk != startRoom) return false;

				stack.Push(startRoom);
				while (stack.Count > 0) route.Add(stack.Pop());
				return true;
			}
			catch { return false; }
		}

		// ===== Distance Cache (Floyd-Warshall) =====

		public static unsafe int[,] BuildDistanceCache(InstanceContentDeepDungeon* dd, List<int> reachableRooms)
		{
			var distances = new int[MaxRooms, MaxRooms];
			if (dd == null || reachableRooms.Count == 0) return distances;

			for (int i = 0; i < MaxRooms; i++)
				for (int j = 0; j < MaxRooms; j++)
					distances[i, j] = (i == j) ? 0 : 999;

			Span<int> nbuf = stackalloc int[4];
			foreach (var room in reachableRooms)
			{
				EnumerateNeighbors(dd, room, nbuf, out int nc);
				for (int i = 0; i < nc; i++)
				{
					int n = nbuf[i];
					distances[room, n] = 1;
					distances[n, room] = 1;
				}
			}

			foreach (var k in reachableRooms)
				foreach (var i in reachableRooms)
					foreach (var j in reachableRooms)
					{
						int throughK = distances[i, k] + distances[k, j];
						if (throughK < distances[i, j])
							distances[i, j] = throughK;
					}

			return distances;
		}

		// ===== Position-based Room Detection =====

		public static unsafe int GetRoomIndexForPosition(InstanceContentDeepDungeon* dd, Vector3 position, IReadOnlyList<int> reachableRooms, int fallbackRoom)
		{
			if (dd == null || reachableRooms == null || reachableRooms.Count == 0) return fallbackRoom;

			int bestRoom = -1;
			float bestDist2 = float.MaxValue;

			foreach (var room in reachableRooms)
			{
				if (MapPos.TryGetRoomCenter(dd, room, out var roomCenter))
				{
					var dx = position.X - roomCenter.X;
					var dz = position.Z - roomCenter.Z;
					var d2 = dx * dx + dz * dz;

					if (d2 < bestDist2)
					{
						bestDist2 = d2;
						bestRoom = room;
					}
				}
			}

			return bestRoom >= 0 ? bestRoom : fallbackRoom;
		}
	}
}
