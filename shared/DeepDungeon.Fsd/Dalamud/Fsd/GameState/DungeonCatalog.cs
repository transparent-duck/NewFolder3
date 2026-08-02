using System.Collections.Generic;
using System.Linq;
using DeepDungeon.Fsd.Dalamud.Items;

namespace DeepDungeon.Fsd.Dalamud.GameState
{
	public static class DungeonCatalog
	{
		public static readonly DungeonData PalaceOfTheDead = new()
		{
			DungeonId = 1,
			Name = "Palace of the Dead",
			DutyDungeonId = 1,
			NpcDataId = 0,
			FloorsetNeedleByStartFloor = new Dictionary<int, string>(),
			RecoveryPotionItemId = Service.Item_SustainingPotion,
			RecoveryPotionName = "Sustaining Potion",
			PotsherdItemId = DeepDungeonItems.PotdPotsherd,
			HoardItemIds = DeepDungeonItems.PotdHoards,
			TerritoryFloorRanges = new Dictionary<uint, (int, int)>
			{
				{ 561, (1, 10) }, { 562, (11, 20) }, { 563, (21, 30) }, { 564, (31, 40) },
				{ 565, (41, 50) }, { 593, (51, 60) }, { 594, (61, 70) }, { 595, (71, 80) },
				{ 596, (81, 90) }, { 597, (91, 100) }, { 598, (101, 110) }, { 599, (111, 120) },
				{ 600, (121, 130) }, { 601, (131, 140) }, { 602, (141, 150) }, { 603, (151, 160) },
				{ 604, (161, 170) }, { 605, (171, 180) }, { 606, (181, 190) }, { 607, (191, 200) }
			}
		};

		public static readonly DungeonData HeavenOnHigh = new()
		{
			DungeonId = 2,
			Name = "Heaven-on-High",
			DutyDungeonId = 2,
			NpcDataId = 0,
			FloorsetNeedleByStartFloor = new Dictionary<int, string>(),
			RecoveryPotionItemId = Service.Item_EmpyreanPotion,
			RecoveryPotionName = "Empyrean Potion",
			PotsherdItemId = DeepDungeonItems.HohPotsherd,
			HoardItemIds = DeepDungeonItems.HohHoards,
			TerritoryFloorRanges = new Dictionary<uint, (int, int)>
			{
				{ 770, (21, 30) }, { 771, (31, 40) }, { 772, (41, 50) }, { 773, (51, 60) },
				{ 774, (61, 70) }, { 775, (71, 80) }, { 782, (81, 90) }, { 783, (91, 100) },
				{ 784, (101, 110) }, { 785, (111, 120) }
			}
		};

		public static readonly DungeonData EurekaOrthos = new()
		{
			DungeonId = 3,
			Name = "Eureka Orthos",
			DutyDungeonId = 3,
			NpcDataId = 0,
			FloorsetNeedleByStartFloor = new Dictionary<int, string>(),
			RecoveryPotionItemId = Service.Item_OrthosPotion,
			RecoveryPotionName = "Orthodox Recovery Potion",
			PotsherdItemId = DeepDungeonItems.EoPotsherd,
			HoardItemIds = DeepDungeonItems.EoHoards,
			TerritoryFloorRanges = new Dictionary<uint, (int, int)>
			{
				{ 1099, (1, 10) }, { 1100, (11, 20) }, { 1101, (21, 30) }, { 1102, (31, 40) },
				{ 1103, (41, 50) }, { 1104, (51, 60) }, { 1105, (61, 70) }, { 1106, (71, 80) },
				{ 1107, (81, 90) }, { 1108, (91, 100) }
			}
		};

		public static readonly DungeonData PilgrimsTraverse = new()
		{
			DungeonId = 4,
			Name = "Pilgrim's Traverse",
			DutyDungeonId = 4,
			NpcDataId = 1054942,
			FloorsetNeedleByStartFloor = new Dictionary<int, string>() { { 21, "21" }, { 31, "31" } },
			RecoveryPotionItemId = Service.Item_PilgrimsPotion,
			RecoveryPotionName = "Pilgrim's Potion",
			PotsherdItemId = DeepDungeonItems.PtPotsherd,
			HoardItemIds = DeepDungeonItems.PtHoards,
			TerritoryFloorRanges = new Dictionary<uint, (int, int)>
			{
				{ 1281, (1, 10) }, { 1282, (11, 20) }, { 1283, (21, 30) }, { 1284, (31, 40) },
				{ 1285, (41, 50) }, { 1286, (51, 60) }, { 1287, (61, 70) }, { 1288, (71, 80) },
				{ 1289, (81, 90) }, { 1290, (91, 100) }, { 1333, (1, 1) }
			}
		};

		public static readonly DungeonData[] All = { PalaceOfTheDead, HeavenOnHigh, EurekaOrthos, PilgrimsTraverse };

		private static Dictionary<uint, DungeonData>? _territoryLookup;

		public static bool SupportsNaturalPtStones(uint dungeonId) =>
			dungeonId == PilgrimsTraverse.DungeonId;

		public static bool TryGetByTerritoryId(uint territoryId, out DungeonData dungeon)
		{
			if (_territoryLookup == null)
			{
				_territoryLookup = new Dictionary<uint, DungeonData>();
				foreach (var d in All)
					foreach (var tid in d.TerritoryFloorRanges.Keys)
						_territoryLookup[tid] = d;
			}
			return _territoryLookup.TryGetValue(territoryId, out dungeon!);
		}

		public static bool TryGetByDungeonId(uint dungeonId, out DungeonData dungeon)
		{
			foreach (var d in All)
			{
				if (d.DungeonId == dungeonId)
				{
					dungeon = d;
					return true;
				}
			}
			dungeon = null!;
			return false;
		}
	}
}
