using System;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DeepDungeon.Fsd.Dalamud.GameState
{
	/// <summary>
	/// Helper to read inventory counts for potsherds and hoard items.
	/// </summary>
	internal static class DeepDungeonLootTracker
	{
		public unsafe static int GetItemCount(uint itemId)
		{
			return TryGetItemCount(itemId, out int count, out _) ? count : 0;
		}

		public unsafe static bool TryGetItemCount(uint itemId, out int count, out string error)
		{
			try
			{
				count = 0;
				error = string.Empty;
				if (itemId == 0)
				{
					error = "item id is zero";
					return false;
				}

				var im = InventoryManager.Instance();
				if (im == null)
				{
					error = "InventoryManager is unavailable";
					return false;
				}

				count = (int)im->GetInventoryItemCount(itemId);
				return true;
			}
			catch (Exception ex)
			{
				count = 0;
				error = ex.Message;
				return false;
			}
		}
	}
}

