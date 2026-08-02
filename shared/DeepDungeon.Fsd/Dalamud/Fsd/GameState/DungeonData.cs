using System.Collections.Generic;

namespace DeepDungeon.Fsd.Dalamud.GameState
{
	public sealed class DungeonData
	{
		public uint DungeonId;
		public string Name = string.Empty;
		public uint AetheryteId;
		public uint NpcBaseId;
		public ushort TerritoryIdOutside;

		public uint DutyDungeonId;
		public uint NpcDataId;
		public Dictionary<int, string> FloorsetNeedleByStartFloor = new();

		/// <summary>
		/// Territory IDs that belong to this dungeon, mapped to (floorRangeStart, floorRangeEnd).
		/// </summary>
		public Dictionary<uint, (int startFloor, int endFloor)> TerritoryFloorRanges = new();

		/// <summary>
		/// Item used as the recovery potion in this dungeon.
		/// </summary>
		public uint RecoveryPotionItemId;
		public string RecoveryPotionName = string.Empty;

		/// <summary>
		/// Potsherd item ID for FSD end-mode tracking.
		/// </summary>
		public uint PotsherdItemId;

		/// <summary>
		/// Hoard item IDs for FSD end-mode tracking.
		/// </summary>
		public uint[] HoardItemIds = System.Array.Empty<uint>();
	}
}
