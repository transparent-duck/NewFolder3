using System;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Map;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
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

        internal static unsafe bool TryResolveRequestedRoom(InstanceContentDeepDungeon* dd,
            int room, out Vector3 destination, out bool usedActor)
        {
            destination = default;
            usedActor = false;
            if (dd == null || (uint)room >= RoomGraph.MaxRooms ||
                !MapPos.TryGetRoomCenter(dd, room, out var center))
                return false;

            var rooms = new List<PassageSearchPoint>();
            var flags = dd->MapData;
            for (int index = 0; index < flags.Length; index++)
                if ((flags[index] & InstanceContentDeepDungeon.RoomFlags.Passage) != 0 &&
                    MapPos.TryGetRoomCenter(dd, index, out var point))
                    rooms.Add(new(index, point));
            var beacons = new List<PassageBeacon>();
            foreach (var actor in Service.GameObjects)
            {
                if (actor.ObjectKind != global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj ||
                    !IsPassageBase(actor.BaseId) || actor.Address == IntPtr.Zero)
                    continue;
                beacons.Add(new(actor.GameObjectId, actor.BaseId, ((EventObject*)actor.Address)->SharedTimelineState,
                    actor.Position));
            }

            var from = Service.LocalPlayer?.Position ?? center;
            var goal = PassageGoalPolicy.Select(beacons, rooms,
                position => Vector3.DistanceSquared(from, position), _ => true, requestedRoom: room);
            if (goal is not { } selected)
                return false;
            destination = selected.Position;
            usedActor = !selected.IsSearch;
            return true;
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
                if (!NativePassageAvailability.IsAvailable(actor.BaseId, actor.SharedTimelineState)) continue;
                float d2 = player == null ? 0f : Vector3.DistanceSquared(player.Position, actor.Position);
                if (found && d2 >= bestD2)
                    continue;

                dest = actor.Position;
                bestD2 = d2;
                found = true;
            }
            return found;
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

        internal static unsafe bool IsAvailablePassage(IGameObject obj)
        {
            if (!IsPassageBase(obj.BaseId)) return false;
            // The removed false beacon keeps its entity, position and targetability.
            // Read the event timeline; generic render flags do not change.
            return obj.BaseId != 2009507 ||
                obj.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj &&
                obj.Address != IntPtr.Zero && NativePassageAvailability.IsAvailable(
                    obj.BaseId, ((EventObject*)obj.Address)->SharedTimelineState);
        }
    }
}
