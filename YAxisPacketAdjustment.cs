using System.Numerics;

namespace NewFolder3;

internal static unsafe class YAxisPacketAdjustment
{
    // Packet coordinates use the layouts supplied by OmenTools. Do not alter
    // local actor coordinates, rotation, movement flags, or unrelated packets.
    public static bool TryAdjust(nint packet, bool instance, float delta, Vector3 playerPosition)
    {
        if (packet == 0 || !float.IsFinite(delta) || Math.Abs(delta) <= 0.0001f)
            return false;

        if (!instance)
        {
            var position = (Vector3*)((byte*)packet + 40);
            if (!IsPlausible(*position, playerPosition))
                return false;
            position->Y += Math.Clamp(delta, -15f, 15f);
            return true;
        }

        else
        {
            var position = (Vector3*)((byte*)packet + 44);
            var positionNew = (Vector3*)((byte*)packet + 56);
            if (!IsPlausible(*position, playerPosition))
                return false;
            bool adjustNew = IsPlausible(*positionNew, playerPosition);
            position->Y += Math.Clamp(delta, -15f, 15f);
            if (adjustNew)
                positionNew->Y = position->Y;
            return true;
        }
    }

    private static bool IsPlausible(Vector3 position, Vector3 playerPosition) =>
        float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z) &&
        Math.Abs(position.X) < 100000 && Math.Abs(position.Y) < 100000 && Math.Abs(position.Z) < 100000 &&
        Vector3.DistanceSquared(position, playerPosition) < 50f * 50f;
}
