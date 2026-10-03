using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public static class PassageNavigationPolicy
{
    public static int? RoutingRoom(bool usedActor, int passageRoom, int playerRoom) =>
        passageRoom >= 0 && (!usedActor || playerRoom != passageRoom) ? passageRoom : null;

    // Passage models may float above the walking surface. Only use a close mesh point
    // under that same actor, never a different corridor/floor with a distant XZ position.
    public static bool TryProjectActor(Vector3 actor, Vector3? meshPoint, out Vector3 destination)
    {
        destination = actor;
        if (meshPoint is not { } point || !float.IsFinite(point.X) || !float.IsFinite(point.Y) ||
            !float.IsFinite(point.Z) || Math.Abs(point.Y - actor.Y) > 4f ||
            Vector2.DistanceSquared(new(actor.X, actor.Z), new(point.X, point.Z)) > 0.25f * 0.25f)
            return false;
        destination = point;
        return true;
    }
}
