using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public readonly record struct PassageBeacon(
    ulong ActorId, uint BaseId, ushort State, int Room, Vector3 Position);

public readonly record struct PassageSearchPoint(int Room, Vector3 Position, int Door = 0);

public readonly record struct PassageGoal(int Room, Vector3 Position, ulong ActorId = 0, int SearchDoor = 0)
{
    public bool IsSearch => ActorId == 0;
    public string Key => !IsSearch ? $"pass:{ActorId}" :
        SearchDoor == 0 ? $"pass:search:{Room}" : $"pass:search:{Room}:door:{SearchDoor}";
}

/// <summary>Selects one terminal for the route, including a room to observe when no beacon can be used.</summary>
public static class PassageGoalPolicy
{
    public static PassageGoal? Select(
        IEnumerable<PassageBeacon> beacons, IEnumerable<PassageSearchPoint> searchPoints,
        Func<Vector3, float> distance, Func<PassageGoal, bool> eligible,
        ulong committedActor = 0, int? requestedRoom = null)
    {
        PassageGoal? best = null;
        float bestDistance = float.PositiveInfinity;
        foreach (var beacon in beacons)
        {
            if (requestedRoom.HasValue && beacon.Room != requestedRoom.Value)
                continue;
            var goal = new PassageGoal(beacon.Room, beacon.Position, beacon.ActorId);
            bool committed = beacon.ActorId != 0 && beacon.ActorId == committedActor;
            if ((!NativePassageAvailability.IsAvailable(beacon.BaseId, beacon.State) &&
                !(committed && !NativePassageAvailability.IsRemoved(beacon.BaseId, beacon.State))) ||
                !eligible(goal))
                continue;
            if (committed)
                return goal;
            float meters = distance(goal.Position);
            if (meters < bestDistance || meters == bestDistance && best is { } previous && goal.ActorId < previous.ActorId)
            {
                best = goal;
                bestDistance = meters;
            }
        }

        if (best.HasValue)
            return best;

        // A removed actor is evidence about that actor only. Prefer to scan
        // another area first, while keeping this area eligible for a scan.
        var roomsWithRemovedActors = new HashSet<int>();
        foreach (var beacon in beacons)
            if (NativePassageAvailability.IsRemoved(beacon.BaseId, beacon.State))
                roomsWithRemovedActors.Add(beacon.Room);
        bool bestContainsRemovedActor = true;
        foreach (var point in searchPoints)
        {
            if (requestedRoom.HasValue && point.Room != requestedRoom.Value)
                continue;
            var goal = new PassageGoal(point.Room, point.Position, SearchDoor: point.Door);
            if (!eligible(goal))
                continue;
            float meters = distance(goal.Position);
            bool containsRemovedActor = roomsWithRemovedActors.Contains(goal.Room);
            bool sameSearchStage = best == null || (best.Value.SearchDoor == 0) == (point.Door == 0);
            bool preferred = sameSearchStage ? bestContainsRemovedActor && !containsRemovedActor : point.Door == 0;
            bool samePriority = sameSearchStage && bestContainsRemovedActor == containsRemovedActor;
            if (float.IsFinite(meters) && (best == null || preferred ||
                samePriority && (meters < bestDistance || meters == bestDistance && goal.Room < best.Value.Room)))
            {
                best = goal;
                bestDistance = meters;
                bestContainsRemovedActor = containsRemovedActor;
            }
        }
        return best;
    }
}
