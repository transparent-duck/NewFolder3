using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Coord;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed record FloorWorkDebugSnapshot(
    FloorPhase Phase,
    string TaskPhase,
    string Status,
    int HoardCount,
    HoardEvidenceState HoardEvidenceState,
    Vector3? CachedHoardIndicatorPos,
    IReadOnlyList<int> RoomPath,
    HashSet<int> CompletedRooms,
    IReadOnlyList<ObjectiveSite> Work)
{
    public int CurrentRoomIdx => 0;
    public int? CurrentTargetRoom => Work.Count > 0 && Work[0].RoomIndex >= 0 ? Work[0].RoomIndex : null;
}

internal sealed partial class FloorExplorationController
{
    private readonly List<Vector3> _sightProjection = new();
    internal bool HoardAcquired => _ctx.Knowledge.Floor?.HoardAcquired == true;

    internal IReadOnlyList<Vector3> ObservedSightTrapPositions
    {
        get
        {
            if (_ctx.Knowledge.Floor is not { } floor)
                return Array.Empty<Vector3>();
            while (_sightProjection.Count < floor.ObservedSightPositions.Count)
            {
                var point = floor.ObservedSightPositions[_sightProjection.Count];
                _sightProjection.Add(new(point.X, point.Y, point.Z));
            }
            return _sightProjection;
        }
    }

    internal FloorWorkDebugSnapshot GetDebugSnapshot()
    {
        var floor = _ctx.Knowledge.Floor;
        var work = new List<ObjectiveSite>();
        if (_siteDriver?.Head is { } head)
            work.Add(head);
        if (_siteDriver is { } driver)
            foreach (var item in driver.RouteWork)
                if (work.Count == 0 || item.Site.Key != work[0].Key)
                    work.Add(item.Site);
        var rooms = new List<int>();
        foreach (var site in work)
            if (site.RoomIndex >= 0 && (rooms.Count == 0 || rooms[^1] != site.RoomIndex))
                rooms.Add(site.RoomIndex);
        var visited = new HashSet<int>();
        if (_coverage != null && NormalGraph != null)
            foreach (var room in NormalGraph.ReachableRooms)
                if (_coverage.IsRoomCovered(room))
                    visited.Add(room);
        return new(_phase, _coordPipeline?.CurrentObjective.ToString() ?? "None", _status,
            floor?.LatestHoardCount ?? 0, floor?.EvidenceState ?? HoardEvidenceState.Disabled,
            floor?.ExactHoardPosition, rooms, visited, work);
    }
}
