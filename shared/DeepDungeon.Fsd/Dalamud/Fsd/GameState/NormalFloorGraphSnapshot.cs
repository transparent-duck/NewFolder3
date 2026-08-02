using System.Collections.Generic;

namespace DeepDungeon.Fsd.Dalamud.GameState
{
	internal sealed record NormalFloorGraphSnapshot(
		int HomeRoomIndex,
		int InitialPlayerRoomIndex,
		IReadOnlyList<int> ReachableRooms,
		int[,] RoomDistances);
}
