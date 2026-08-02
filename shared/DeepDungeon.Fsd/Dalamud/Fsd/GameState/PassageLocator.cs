using System;
using System.Numerics;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;

namespace DeepDungeon.Fsd.Dalamud.GameState
{
    /// <summary>
    /// Shared helpers for locating the passage (exit pylon/cairn) in the current deep-dungeon floor.
    /// Provides canonical actor and room-center resolution used by manual and FSD flows.
    /// </summary>
    internal static class PassageLocator
    {
        private static readonly uint[] PassageBaseIds =
        {
            0x1EA094, // CairnPalace (POTD)
            0x1EA9A3, // BeaconHoH   (HOH)
            0x1EB867, // PylonEO     (EO)
            0x1EBE24  // PylonPT     (PT)
        };

        /// <summary>
        /// Tries to find the nearest passage actor (pylon/cairn) to the local player.
        /// </summary>
        public static unsafe bool TryGetPassageActorPosition(InstanceContentDeepDungeon* dd, out Vector3 dest)
        {
            dest = default;

            try
            {
                var player = Service.LocalPlayer;
                IGameObject? best = null;
                float bestD2 = float.MaxValue;

                foreach (var obj in Service.GameObjects)
                {
                    if (obj == null)
                        continue;

                    if (!IsPassageBase(obj.BaseId))
                        continue;

                    float d2;
                    if (player != null)
                    {
                        var dx = obj.Position.X - player.Position.X;
                        var dz = obj.Position.Z - player.Position.Z;
                        d2 = dx * dx + dz * dz;
                    }
                    else
                    {
                        d2 = 0f;
                    }

                    if (best == null || d2 < bestD2)
                    {
                        best = obj;
                        bestD2 = d2;
                    }
                }

                if (best != null)
                {
                    dest = best.Position;
                    return true;
                }
            }
            catch
            {
                // ignore
            }

            return false;
        }

        internal static bool TryGetPassageActorPosition(FloorObjectEvidenceSnapshot evidence, out Vector3 dest)
        {
            dest = default;
            if (!evidence.Available)
                return false;

            var player = Service.LocalPlayer;
            float bestD2 = float.MaxValue;
            bool found = false;
            for (int i = 0; i < evidence.PassageActors.Count; i++)
            {
                var actor = evidence.PassageActors[i];
                float d2 = player == null ? 0f : Vector3.DistanceSquared(player.Position, actor.Position);
                if (found && d2 >= bestD2)
                    continue;

                dest = actor.Position;
                bestD2 = d2;
                found = true;
            }
            return found;
        }

        /// <summary>
        /// Tries to resolve a reasonable passage destination using room center if the actor is not yet loaded.
        /// Returns true and a world position if successful.
        /// </summary>
        public static unsafe bool TryGetPassageRoomCenter(InstanceContentDeepDungeon* dd, out int passageRoomIndex, out Vector3 dest)
        {
            dest = default;
            passageRoomIndex = -1;

            if (dd == null)
                return false;

            try
            {
                passageRoomIndex = RoomGraph.GetPassageRoomIndex(dd);
                if (passageRoomIndex < 0)
                    return false;

                if (!TryGetRoomCenter(dd, passageRoomIndex, out dest))
                    return false;

                return true;
            }
            catch
            {
                // ignore
            }

            return false;
        }

        /// <summary>
        /// Resolves the best passage destination, preferring the actual actor position when available
        /// and falling back to the room center otherwise.
        /// </summary>
        public static unsafe bool TryResolvePassageDestination(InstanceContentDeepDungeon* dd, out Vector3 dest, out bool usedActorPosition, out int passageRoomIndex)
        {
            dest = default;
            usedActorPosition = false;
            passageRoomIndex = -1;

            if (dd == null)
                return false;

            // Prefer exact actor position
            if (TryGetPassageActorPosition(dd, out dest))
            {
                usedActorPosition = true;
                passageRoomIndex = RoomGraph.GetPassageRoomIndex(dd);
                return true;
            }

            // Fallback to room center
            if (TryGetPassageRoomCenter(dd, out passageRoomIndex, out dest))
            {
                usedActorPosition = false;
                return true;
            }

            return false;
        }

        internal static unsafe bool TryResolvePassageDestination(
            InstanceContentDeepDungeon* dd,
            FloorObjectEvidenceSnapshot evidence,
            out Vector3 dest,
            out bool usedActorPosition,
            out int passageRoomIndex)
        {
            dest = default;
            usedActorPosition = false;
            passageRoomIndex = -1;
            if (dd == null || !evidence.Available)
                return false;

            if (TryGetPassageActorPosition(evidence, out dest))
            {
                usedActorPosition = true;
                passageRoomIndex = RoomGraph.GetPassageRoomIndex(dd);
                return true;
            }

            return TryGetPassageRoomCenter(dd, out passageRoomIndex, out dest);
        }

        internal static bool IsPassageBase(uint baseId)
        {
            for (int i = 0; i < PassageBaseIds.Length; i++)
            {
                if (baseId == PassageBaseIds[i])
                    return true;
            }
            return false;
        }

        private static unsafe bool TryGetRoomCenter(InstanceContentDeepDungeon* dd, int roomIndex, out Vector3 center)
        {
            center = default;

            try
            {
                return DeepDungeon.Fsd.Dalamud.Map.MapPos.TryGetRoomCenter(dd, roomIndex, out center);
            }
            catch
            {
                // ignore
            }

            return false;
        }
    }
}

