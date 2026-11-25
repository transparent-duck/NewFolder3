using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public readonly record struct PassageBeacon(
    ulong ActorId, uint BaseId, ushort State, Vector3 Position);

public readonly record struct PassageSearchPoint(int Room, Vector3 Position, int Door = 0);

public readonly record struct PassageGoal(int Room, Vector3 Position, ulong ActorId = 0, int SearchDoor = 0)
{
    public bool IsSearch => ActorId == 0;
    public string Key => !IsSearch ? $"pass:{ActorId}" :
        SearchDoor == 0 ? $"pass:search:{Room}" : $"pass:search:{Room}:door:{SearchDoor}";
}

/// <summary>Plans through marked exit rooms and uses live beacons for exact traversal.</summary>
public static class PassageGoalPolicy
{
    public static PassageGoal? Select(
        IReadOnlyList<PassageBeacon> beacons, IReadOnlyList<PassageSearchPoint> searchPoints,
        Func<Vector3, float> distance, Func<PassageGoal, bool> eligible,
        ulong committedActor = 0, int? requestedRoom = null)
    {
        var rooms = searchPoints.Where(point => point.Door == 0).OrderBy(point => point.Room).ToArray();
        var actors = beacons.Where(beacon => float.IsFinite(beacon.Position.X) && float.IsFinite(beacon.Position.Z))
            .OrderBy(beacon => beacon.ActorId).ToArray();
        var roomActors = MatchRooms(rooms, actors);
        var actorByRoom = new Dictionary<int, PassageBeacon>(rooms.Length);
        for (int i = 0; i < rooms.Length; i++)
            if (roomActors[i] >= 0)
                actorByRoom.Add(rooms[i].Room, actors[roomActors[i]]);
        bool CanUse(PassageBeacon beacon, PassageGoal goal) =>
            NativePassageAvailability.IsAvailable(beacon.BaseId, beacon.State) && eligible(goal);

        for (int i = 0; committedActor != 0 && i < actors.Length; i++)
        {
            var beacon = actors[i];
            if (beacon.ActorId != committedActor || NativePassageAvailability.IsRemoved(beacon.BaseId, beacon.State))
                continue;
            int roomIndex = Array.IndexOf(roomActors, i);
            var goal = new PassageGoal(roomIndex < 0 ? -1 : rooms[roomIndex].Room, beacon.Position, beacon.ActorId);
            if (eligible(goal)) return goal;
        }

        PassageGoal? best = null;
        (bool Retired, bool Door, float Distance, int Room, int SearchDoor) bestRank = default;
        foreach (var point in searchPoints)
        {
            if (requestedRoom.HasValue && point.Room != requestedRoom.Value)
                continue;
            var goal = new PassageGoal(point.Room, point.Position, SearchDoor: point.Door);
            bool retired = false;
            if (actorByRoom.TryGetValue(point.Room, out var beacon))
            {
                var actorGoal = new PassageGoal(point.Room, beacon.Position, beacon.ActorId);
                retired = NativePassageAvailability.IsRemoved(beacon.BaseId, beacon.State) || !eligible(actorGoal);
                if (point.Door == 0 && CanUse(beacon, actorGoal)) goal = actorGoal;
            }
            if (!eligible(goal)) continue;
            float meters = distance(point.Position);
            var rank = (retired, point.Door != 0, meters, point.Room, point.Door);
            if (float.IsFinite(meters) && (best == null || rank.CompareTo(bestRank) < 0))
            {
                best = goal;
                bestRank = rank;
            }
        }
        if (best.HasValue || requestedRoom.HasValue) return best;

        // Live coordinates also describe a traversal target when no marked room
        // can be used (for example, a layout with an unmarked beacon).
        float bestDistance = float.PositiveInfinity;
        for (int i = 0; i < actors.Length; i++)
        {
            if (Array.IndexOf(roomActors, i) >= 0) continue;
            var beacon = actors[i];
            var goal = new PassageGoal(-1, beacon.Position, beacon.ActorId);
            float meters = distance(beacon.Position);
            if (meters < bestDistance && CanUse(beacon, goal))
            {
                best = goal;
                bestDistance = meters;
            }
        }
        return best;
    }

    // Minimum total horizontal distance, with distinct rows and columns.
    // Use the smaller set for rows so partial observations need no dummy rooms.
    private static int[] MatchRooms(PassageSearchPoint[] rooms, PassageBeacon[] actors)
    {
        var result = new int[rooms.Length];
        Array.Fill(result, -1);
        bool rowsAreActors = actors.Length <= rooms.Length;
        int rows = Math.Min(rooms.Length, actors.Length);
        int columns = Math.Max(rooms.Length, actors.Length);
        if (rows == 0) return result;

        var rowPotential = new double[rows + 1];
        var columnPotential = new double[columns + 1];
        var assignedRow = new int[columns + 1];
        var previousColumn = new int[columns + 1];
        var bestCost = new double[columns + 1];
        var visited = new bool[columns + 1];
        for (int row = 1; row <= rows; row++)
        {
            assignedRow[0] = row;
            Array.Fill(bestCost, double.PositiveInfinity);
            Array.Clear(visited);
            int current = 0;
            do
            {
                visited[current] = true;
                int activeRow = assignedRow[current];
                int next = 0;
                double delta = double.PositiveInfinity;
                for (int column = 1; column <= columns; column++)
                {
                    if (visited[column]) continue;
                    var room = rooms[(rowsAreActors ? column : activeRow) - 1];
                    var actor = actors[(rowsAreActors ? activeRow : column) - 1];
                    double dx = room.Position.X - actor.Position.X;
                    double dz = room.Position.Z - actor.Position.Z;
                    double cost = Math.Sqrt(dx * dx + dz * dz) - rowPotential[activeRow] - columnPotential[column];
                    if (cost < bestCost[column])
                    {
                        bestCost[column] = cost;
                        previousColumn[column] = current;
                    }
                    if (bestCost[column] < delta)
                    {
                        delta = bestCost[column];
                        next = column;
                    }
                }
                for (int column = 0; column <= columns; column++)
                {
                    if (visited[column])
                    {
                        rowPotential[assignedRow[column]] += delta;
                        columnPotential[column] -= delta;
                    }
                    else bestCost[column] -= delta;
                }
                current = next;
            } while (assignedRow[current] != 0);
            do
            {
                int previous = previousColumn[current];
                assignedRow[current] = assignedRow[previous];
                current = previous;
            } while (current != 0);
        }
        for (int column = 1; column <= columns; column++)
        {
            int row = assignedRow[column];
            if (row == 0) continue;
            int room = (rowsAreActors ? column : row) - 1;
            result[room] = (rowsAreActors ? row : column) - 1;
        }
        return result;
    }
}
