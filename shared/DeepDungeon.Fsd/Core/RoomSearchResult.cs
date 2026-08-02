using System.Collections.Generic;

namespace DeepDungeon.Fsd.Core
{
    public readonly record struct RoomSearchResult
    {
        public IReadOnlyList<RoomSearchWaypointEntry> Waypoints { get; init; }
    }

    public readonly record struct RoomSearchWaypointEntry
    {
        public SearchObjectiveType Type { get; init; }
        public RoomSearchWaypointSource Source { get; init; }
        public int SourceIndex { get; init; }
    }
}
