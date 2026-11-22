using DeepDungeon.Fsd.Core;
using System.Numerics;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry
{
	internal static class NpcInteractionGuard
	{
		public static readonly NavigationRange InteractionRange = new(5.5f, NavigationDistanceMetric.Spatial);
        public static float MaxInteractDistance => InteractionRange.Radius;

		public static unsafe bool TryInteract(uint baseId, string statusPrefix, out string status)
		{
			status = $"{statusPrefix}: locating NPC";
			var npc = FindByBaseId(baseId);
			if (npc == null)
			{
				status = $"{statusPrefix}: NPC not found, waiting for load...";
				return false;
			}

			var player = Service.LocalPlayer;
			if (player == null)
			{
				status = $"{statusPrefix}: waiting for player";
				return false;
			}

			var distance = InteractionRange.Distance(player.Position, npc.Position);
			if (!InteractionRange.Contains(player.Position, npc.Position))
			{
				status = $"{statusPrefix}: NPC too far ({distance:F1}m); move near entry NPC";
				return false;
			}

			var ts = TargetSystem.Instance();
			if (ts == null)
			{
				status = $"{statusPrefix}: target system unavailable";
				return false;
			}

			if (ts->Target == null || ts->Target->BaseId != baseId)
			{
				ts->Target = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address;
			}

			ts->InteractWithObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address);
			status = $"{statusPrefix}: interacted with NPC";
			return true;
		}

		internal static IGameObject? FindByBaseId(uint baseId)
		{
			foreach (var obj in Service.GameObjects)
			{
				if (obj != null && obj.BaseId == baseId)
					return obj;
			}

			return null;
		}
	}
}
