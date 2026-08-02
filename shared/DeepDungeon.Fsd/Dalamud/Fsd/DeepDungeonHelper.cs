using System;
using DeepDungeon.Fsd.Dalamud.GameState;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud
{
    /// <summary>
    /// Deep dungeon helper class for basic floor and dungeon detection.
    /// Hosts canonical boss-floor / passage-open rules shared between manual and FSD flows.
    /// Territory data is sourced from <see cref="DungeonCatalog"/>.
    /// </summary>
    public static class DeepDungeonHelper
    {
        public static bool IsInDeepDungeon()
        {
            return DungeonCatalog.TryGetByTerritoryId(Service.ClientState.TerritoryType, out _);
        }

        public static bool TryGetRecoveryPotionForCurrentDungeon(out uint itemId, out string potionName)
        {
            if (DungeonCatalog.TryGetByTerritoryId(Service.ClientState.TerritoryType, out var dungeon)
                && dungeon.RecoveryPotionItemId != 0)
            {
                itemId = dungeon.RecoveryPotionItemId;
                potionName = dungeon.RecoveryPotionName;
                return true;
            }

            itemId = 0;
            potionName = string.Empty;
            return false;
        }

        public static bool IsBossFloor(uint dungeonId, byte floor)
        {
            switch (dungeonId)
            {
                case 1:
                    return (floor % 10 == 0) && floor != 200;
                case 2:
                    return (floor % 10 == 0) && floor != 100;
                case 3:
                case 4:
                    return (floor % 10 == 0 && floor != 100) || floor == 99;
                default:
                    return (floor % 10 == 0) && floor != 100 && floor != 200;
            }
        }

        public static unsafe bool IsPassageOpen(InstanceContentDeepDungeon* dd)
        {
            try
            {
                return dd != null && dd->PassageProgress >= 11;
            }
            catch
            {
                return false;
            }
        }

		public static bool TryGetFloorRangeForCurrentTerritory(out int startFloor, out int endFloor)
		{
			startFloor = 0;
			endFloor = 0;
			uint territoryId = Service.ClientState.TerritoryType;

			if (DungeonCatalog.TryGetByTerritoryId(territoryId, out var dungeon)
			    && dungeon.TerritoryFloorRanges.TryGetValue(territoryId, out var range))
			{
				startFloor = range.startFloor;
				endFloor = range.endFloor;
				return true;
			}
			return false;
		}
    }
}
