using System.Numerics;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Search
{
	/// <summary>
	/// Shared helpers for locating banded hoard coffers in the current instance.
	/// Returns world positions so callers in both manual and auto flows can path to them.
	/// </summary>
	internal static class BandedChestLocator
	{
		internal readonly record struct HoardIndicatorMatch(
			Vector3 Position,
			uint BaseId,
			ulong GameObjectId,
			uint EntityId,
			ushort ObjectIndex,
			string Name,
			string ObjectKind,
			byte SubKind,
			bool IsTargetable,
			bool IsBandedChest,
			string Address);

		internal const uint HoardIndicatorBaseId = 2007542;

		/// <summary>
		/// Finds the nearest banded coffer to the local player, if any.
		/// </summary>
		public static bool TryFindNearestToPlayer(FloorObjectEvidenceSnapshot evidence, out Vector3? position)
		{
			position = null;
			if (!evidence.Available)
				return false;

			var me = Service.LocalPlayer;
			float bestD2 = float.MaxValue;
			for (int i = 0; i < evidence.Chests.Count; i++)
			{
				var chest = evidence.Chests[i];
				if (chest.Kind != FloorChestKind.Banded || !chest.Object.IsTargetable)
					continue;
				if (me == null)
				{
					position = chest.Object.Position;
					return true;
				}
				float d2 = DistanceSquaredXZ(chest.Object.Position, me.Position);
				if (d2 < bestD2)
				{
					position = chest.Object.Position;
					bestD2 = d2;
				}
			}
			return true;
		}

		/// <summary>
		/// Finds the nearest banded coffer within the given radius of a point, if any.
		/// </summary>
		public static bool TryFindNearestAround(FloorObjectEvidenceSnapshot evidence, Vector3 around, float radius, out Vector3? position)
		{
			position = null;
			if (!evidence.Available)
				return false;

			float r2 = radius * radius;
			float bestD2 = float.MaxValue;
			for (int i = 0; i < evidence.Chests.Count; i++)
			{
				var chest = evidence.Chests[i];
				if (chest.Kind != FloorChestKind.Banded || !chest.Object.IsTargetable)
					continue;
				float d2 = DistanceSquaredXZ(chest.Object.Position, around);
				if (d2 <= r2 && d2 < bestD2)
				{
					position = chest.Object.Position;
					bestD2 = d2;
				}
			}
			return true;
		}

		public static bool TryFindHoardIndicatorMatch(FloorObjectEvidenceSnapshot evidence, out HoardIndicatorMatch? indicator)
		{
			indicator = null;
			if (!evidence.Available)
				return false;

			var me = Service.LocalPlayer;
			float bestD2 = float.MaxValue;
			for (int i = 0; i < evidence.HoardIndicators.Count; i++)
			{
				var item = evidence.HoardIndicators[i];
				var obj = item.Object;
				var match = new HoardIndicatorMatch(
					obj.Position,
					obj.BaseId,
					obj.GameObjectId,
					obj.EntityId,
					obj.ObjectIndex,
					item.Name,
					item.ObjectKind,
					item.SubKind,
					obj.IsTargetable,
					false,
					item.Address);
				if (me == null)
				{
					indicator = match;
					return true;
				}
				float d2 = DistanceSquaredXZ(obj.Position, me.Position);
				if (d2 < bestD2)
				{
					indicator = match;
					bestD2 = d2;
				}
			}
			return true;
		}

		private static float DistanceSquaredXZ(Vector3 left, Vector3 right)
		{
			float dx = left.X - right.X;
			float dz = left.Z - right.Z;
			return dx * dx + dz * dz;
		}
	}
}

