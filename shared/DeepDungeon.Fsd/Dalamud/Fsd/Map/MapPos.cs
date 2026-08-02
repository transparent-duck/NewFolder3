using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Map
{
    internal static class MapPos
    {
        private const int MaxRoomsPerFloor = 25;

        public static unsafe bool TryGetRoomCenter(InstanceContentDeepDungeon* dd, int roomIndex, out Vector3 center)
        {
            center = default;
            if (dd == null || roomIndex < 0 || roomIndex >= MaxRoomsPerFloor)
                return false;

            return MapPosGeneration.TryGetRuntimeRoomCenter(dd, roomIndex, out center);
        }
    }
}

