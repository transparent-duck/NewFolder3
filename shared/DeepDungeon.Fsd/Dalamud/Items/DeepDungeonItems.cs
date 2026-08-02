using System.Collections.Generic;

namespace DeepDungeon.Fsd.Dalamud.Items
{
	/// <summary>
	/// Named constants and helpers for Deep Dungeon-related items (potsherds and hoards).
	/// Centralizes item IDs so they are not scattered as magic numbers.
	/// </summary>
	public static class DeepDungeonItems
	{
		// Potsherds (one per deep dungeon)
		public const uint PotdPotsherd = 15422;   // Gelmorran Potsherd
		public const uint HohPotsherd = 23164;    // Empyrean Potsherd
		public const uint EoPotsherd = 38941;     // Orthos Aetherpool Fragment
		public const uint PtPotsherd = 46186;     // Illumed Aetherpool Glass

		// Hoard items by dungeon
		public static readonly uint[] PotdHoards = { 16170, 16171, 16172, 16173 };
		public static readonly uint[] HohHoards = { 23223, 23224, 23225 };
		public static readonly uint[] EoHoards = { 38945, 38946, 38947 };
		public static readonly uint[] PtHoards = { 47104, 47105, 47106 }; // 47742 is weekly reward; intentionally omitted

		/// <summary>
		/// Optionally pre-register all Deep Dungeon-related items in the ItemManager cache.
		/// </summary>
		public static void PreRegisterAll()
		{
			ItemManager.Initialize();

			IEnumerable<uint> allIds()
			{
				yield return PotdPotsherd;
				yield return HohPotsherd;
				yield return EoPotsherd;
				yield return PtPotsherd;
				foreach (var id in PotdHoards) yield return id;
				foreach (var id in HohHoards) yield return id;
				foreach (var id in EoHoards) yield return id;
				foreach (var id in PtHoards) yield return id;
			}

			foreach (var id in allIds())
			{
				try { ItemManager.GetOrRegister(id); } catch { }
			}
		}
	}
}



