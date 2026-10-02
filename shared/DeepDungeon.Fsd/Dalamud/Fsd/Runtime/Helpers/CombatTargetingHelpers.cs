using System;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Helpers
{
    /// <summary>
    /// Shared targeting helpers for selecting hostile battle targets.
    /// Used by both manual banded farm and FSD combat-assist logic.
    /// </summary>
    internal static class CombatTargetingHelpers
    {
        public static IBattleChara? PickNearestHostile(float range, out bool withinRange)
        {
            float r2 = range * range;
            withinRange = false;

            var player = Service.LocalPlayer;
            if (player == null)
                return null;

            IBattleChara? best = null;
            float bestD2 = float.MaxValue;

            foreach (var obj in Service.GameObjects)
            {
                if (obj is IBattleNpc bnpc
                    && obj.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc
                    && (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)obj.SubKind == global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant
                    && bnpc.IsTargetable && !bnpc.IsDead)
                {
                    var dx = obj.Position.X - player.Position.X;
                    var dz = obj.Position.Z - player.Position.Z;
                    var d2 = dx * dx + dz * dz;
                    if (d2 < bestD2)
                    {
                        best = bnpc;
                        bestD2 = d2;
                    }
                }
            }

            if (best != null)
                withinRange = bestD2 <= r2;

            return best;
        }

        public static IBattleChara? PickNearestHostileAnyRange()
        {
            var player = Service.LocalPlayer;
            if (player == null)
                return null;

            IBattleChara? best = null;
            float bestD2 = float.MaxValue;

            foreach (var obj in Service.GameObjects)
            {
                if (obj is IBattleNpc bnpc
                    && obj.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc
                    && (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)obj.SubKind == global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant
                    && bnpc.IsTargetable && !bnpc.IsDead)
                {
                    var dx = obj.Position.X - player.Position.X;
                    var dz = obj.Position.Z - player.Position.Z;
                    var d2 = dx * dx + dz * dz;
                    if (d2 < bestD2)
                    {
                        best = bnpc;
                        bestD2 = d2;
                    }
                }
            }

            return best;
        }

        public static unsafe IBattleChara? PickHostileHighestHP(float range, bool onlyInRange, bool mustBeInCombat, Func<IBattleChara, bool>? predicate = null)
        {
            float r2 = range * range;
            var player = Service.LocalPlayer;
            if (player == null)
                return null;

            IBattleChara? best = null;
            long bestHp = -1;

            foreach (var obj in Service.GameObjects)
            {
                if (obj is IBattleNpc bnpc
                    && obj.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc
                    && (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)obj.SubKind == global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant
                    && bnpc.IsTargetable && !bnpc.IsDead)
                {
                    if (mustBeInCombat)
                    {
                        try
                        {
                            var ptr = (Character*)bnpc.Address;
                            if (ptr == null || !ptr->InCombat)
                                continue;
                        }
                        catch
                        {
                            continue;
                        }
                    }

                    if (onlyInRange)
                    {
                        var dx = obj.Position.X - player.Position.X;
                        var dz = obj.Position.Z - player.Position.Z;
                        if (dx * dx + dz * dz > r2)
                            continue;
                    }

                    if (predicate != null && !predicate(bnpc))
                        continue;

                    if (bnpc.CurrentHp > bestHp)
                    {
                        bestHp = bnpc.CurrentHp;
                        best = bnpc;
                    }
                }
            }

            return best;
        }

        // Optional BMR arena data is sampled by the caller, not queried inside the object scan.
        public static IBattleChara? PickBossTarget(Vector2 center, Func<IBattleChara, bool>? predicate)
        {
            var player = Service.LocalPlayer;
            if (player == null) return null;
            IBattleChara? best = null;
            BossTargetPriority bestPriority = default;
            foreach (var obj in Service.GameObjects)
            {
                if (obj is not IBattleNpc npc || !IsBossCandidate(npc, player.Position, predicate)) continue;
                var delta = new Vector2(npc.Position.X, npc.Position.Z) - center;
                var priority = new BossTargetPriority(npc.MaxHp, delta.LengthSquared(), npc.GameObjectId);
                if (best == null || priority.Precedes(bestPriority))
                {
                    best = npc;
                    bestPriority = priority;
                }
            }
            return best;
        }

        public static Vector2 BossTargetFallbackCenter(Func<IBattleChara, bool>? predicate)
        {
            var player = Service.LocalPlayer;
            if (player == null) return default;
            uint minMaxHp = uint.MaxValue;
            Vector2 sum = default;
            int count = 0;
            foreach (var obj in Service.GameObjects)
            {
                if (obj is not IBattleNpc npc || !IsBossCandidate(npc, player.Position, predicate)) continue;
                if (npc.MaxHp > minMaxHp) continue;
                if (npc.MaxHp < minMaxHp) { minMaxHp = npc.MaxHp; sum = default; count = 0; }
                sum += new Vector2(npc.Position.X, npc.Position.Z);
                count++;
            }
            return count > 0 ? sum / count : new Vector2(player.Position.X, player.Position.Z);
        }

        private static unsafe bool IsBossCandidate(IBattleNpc npc, Vector3 playerPosition, Func<IBattleChara, bool>? predicate)
        {
            if (!npc.IsTargetable || npc.IsDead || npc.CurrentHp <= 1 || npc.MaxHp == 0 ||
                (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)npc.SubKind !=
                    global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant ||
                predicate != null && !predicate(npc)) return false;
            var delta = new Vector2(npc.Position.X - playerPosition.X, npc.Position.Z - playerPosition.Z);
            // Select across the boss room so BMR can approach an add outside the current spell range.
            if (delta.LengthSquared() > 60 * 60) return false;
            var native = (Character*)npc.Address;
            return native != null && native->InCombat;
        }

		public static unsafe IBattleChara? PickAggroedHostile(float range, out bool withinRange, ulong preferredGameObjectId = 0)
		{
			withinRange = false;
			var selected = PickAggroedHostileCore(preferredGameObjectId, out var selectedDist);
			if (selected == null)
				return null;

			withinRange = selectedDist <= range * range;
			return selected;
		}

		private static unsafe IBattleChara? PickAggroedHostileCore(ulong preferredGameObjectId, out float selectedDist)
		{
			selectedDist = 0f;

			var player = Service.LocalPlayer;
			if (player == null)
				return null;

			var uiState = UIState.Instance();
			if (uiState == null)
				return null;

			var hater = &uiState->Hater;
			var count = Math.Clamp(hater->HaterCount, 0, 32);
			if (count == 0)
				return null;

			Span<ulong> aggroIds = stackalloc ulong[32];
			int aggroCount = 0;
			for (int i = 0; i < count && i < aggroIds.Length; i++)
			{
				var entityId = hater->Haters[i].EntityId;
				if (entityId == 0)
					continue;
				aggroIds[aggroCount++] = entityId;
			}

			if (aggroCount == 0)
				return null;

			IBattleChara? preferred = null;
			float preferredDist = 0f;
			IBattleChara? nearest = null;
			float nearestDist = float.MaxValue;

			foreach (var obj in Service.GameObjects)
			{
				if (obj is not IBattleChara bnpc
				    || obj.ObjectKind != global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc
				    || (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)obj.SubKind != global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant
				    || !bnpc.IsTargetable
				    || bnpc.IsDead)
				{
					continue;
				}

				bool matchesAggro = false;
				for (int i = 0; i < aggroCount; i++)
				{
					if (aggroIds[i] == bnpc.GameObjectId)
					{
						matchesAggro = true;
						break;
					}
				}

				if (!matchesAggro)
					continue;

				var dx = bnpc.Position.X - player.Position.X;
				var dz = bnpc.Position.Z - player.Position.Z;
				var distSq = dx * dx + dz * dz;

				if (preferredGameObjectId != 0 && bnpc.GameObjectId == preferredGameObjectId)
				{
					preferred = bnpc;
					preferredDist = distSq;
					break;
				}

				if (distSq < nearestDist)
				{
					nearest = bnpc;
					nearestDist = distSq;
				}
			}

			var selected = preferred ?? nearest;
			if (selected == null)
				return null;

			selectedDist = preferred != null ? preferredDist : nearestDist;
			return selected;
		}

		public static IBattleChara? GetBattleCharaByGameObjectId(ulong targetId)
		{
			if (targetId == 0)
				return null;

			foreach (var obj in Service.GameObjects)
			{
				if (obj is IBattleChara bnpc
				    && obj.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc
				    && (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)obj.SubKind == global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant
				    && !bnpc.IsDead
				    && bnpc.IsTargetable
				    && bnpc.GameObjectId == targetId)
				{
					return bnpc;
				}
			}

			return null;
		}
    }
}

