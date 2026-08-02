using System;
using System.Collections.Generic;

namespace DeepDungeon.Fsd.Core
{
    public static class RoomSearchBuilder
    {
        public static RoomSearchResult Build(in RoomSearchSnapshot snapshot)
        {
            var waypoints = new List<RoomSearchWaypointEntry>();

            if (ShouldBuildHoardWaypoints(snapshot))
            {
                AddHoardWaypoints(snapshot, waypoints);
            }

            if (snapshot.ShouldSearchChests && snapshot.VisibleChests != null)
            {
                for (int i = 0; i < snapshot.VisibleChests.Count; i++)
                {
                    waypoints.Add(new RoomSearchWaypointEntry
                    {
                        Type = snapshot.VisibleChests[i].Type,
                        Source = RoomSearchWaypointSource.VisibleChest,
                        SourceIndex = i
                    });
                }
            }

            return new RoomSearchResult
            {
                Waypoints = waypoints
            };
        }

        private static void AddHoardWaypoints(in RoomSearchSnapshot snapshot, List<RoomSearchWaypointEntry> waypoints)
        {
            if (snapshot.VisibleBandedChestInRoom)
            {
                waypoints.Add(new RoomSearchWaypointEntry
                {
                    Type = SearchObjectiveType.ChestBanded,
                    Source = RoomSearchWaypointSource.VisibleBandedChest,
                    SourceIndex = 0
                });
                return;
            }

            if (snapshot.CachedHoardIndicatorInRoom)
            {
                waypoints.Add(new RoomSearchWaypointEntry
                {
                    Type = SearchObjectiveType.Trap,
                    Source = RoomSearchWaypointSource.CachedHoardIndicator,
                    SourceIndex = 0
                });
                return;
            }

            if (snapshot.HasDetailedMapRoom)
            {
                for (int i = 0; i < snapshot.DetailedMapCandidateCount; i++)
                {
                    waypoints.Add(new RoomSearchWaypointEntry
                    {
                        Type = SearchObjectiveType.Trap,
                        Source = RoomSearchWaypointSource.DetailedMapCandidate,
                        SourceIndex = i
                    });
                }

                return;
            }

            for (int i = 0; i < snapshot.FallbackTrapCount; i++)
            {
                waypoints.Add(new RoomSearchWaypointEntry
                {
                    Type = SearchObjectiveType.Trap,
                    Source = RoomSearchWaypointSource.FallbackTrap,
                    SourceIndex = i
                });
            }
        }

        private static bool ShouldBuildHoardWaypoints(in RoomSearchSnapshot snapshot)
        {
            if (snapshot.HoardEvidenceState is HoardEvidenceState.IntuitionPending or HoardEvidenceState.IntuitionWaitingForIndicator)
            {
                return false;
            }

            return snapshot.ShouldProbeHoard || snapshot.ShouldSearchHoard;
        }
    }
}
