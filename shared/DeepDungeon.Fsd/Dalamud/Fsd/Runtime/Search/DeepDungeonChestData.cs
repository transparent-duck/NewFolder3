using DeepDungeon.Fsd.Dalamud.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Search
{
	internal static class DeepDungeonChestData
	{
		private const byte BronzeChestType = 1;
		private const byte SilverChestType = 2;
		private const byte GoldChestType = 3;

		public static unsafe bool IsRoomRevealed(InstanceContentDeepDungeon* dd, int roomIndex)
		{
			if (dd == null || roomIndex < 0 || roomIndex >= dd->MapData.Length)
				return false;

			return (dd->MapData[roomIndex] & InstanceContentDeepDungeon.RoomFlags.Revealed) != 0;
		}

		public static unsafe bool RoomHasEnabledChest(InstanceContentDeepDungeon* dd, int roomIndex, RunOptions config)
		{
			if (dd == null || config == null)
				return false;

			var chests = dd->Chests;
			for (int i = 0; i < chests.Length; i++)
			{
				var chest = chests[i];
				if (chest.RoomIndex != roomIndex)
					continue;

				if (IsEnabledChestType(chest.ChestType, config))
					return true;
			}

			return false;
		}

		public static unsafe bool RoomHasKnownChestEntry(
			InstanceContentDeepDungeon* dd,
			int roomIndex)
		{
			if (dd == null)
				return false;

			var chests = dd->Chests;
			for (int i = 0; i < chests.Length; i++)
			{
				var chest = chests[i];
				if (chest.RoomIndex == roomIndex && chest.ChestType != 0)
					return true;
			}

			return false;
		}

		public static bool IsEnabledChestType(byte chestType, RunOptions config)
		{
			if (config == null)
				return false;

			return chestType switch
			{
				BronzeChestType => config.OpenBronze,
				SilverChestType => config.OpenSilver,
				GoldChestType => config.OpenGold,
				_ => false
			};
		}
	}
}
