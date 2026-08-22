using System;
using System.Collections.Generic;

namespace DeepDungeon.Fsd.Core
{
    public static class PlanGenerator
    {
        private const int HoardBasePriority = 100;
        private const int ChestBasePriority = 60;

        public static FloorPlanTrace GenerateTrace(in FloorPlanSnapshot snapshot)
        {
            var plan = new List<RoomPlanEntry>(snapshot.Rooms?.Count ?? 0);
            var hoardEvidenceState = ResolveHoardEvidenceState(snapshot);
            if (snapshot.Rooms == null || snapshot.Rooms.Count == 0 || snapshot.ReachableRooms == null || snapshot.ReachableRooms.Count == 0)
            {
                return new FloorPlanTrace
                {
                    Plan = plan,
                    Candidates = Array.Empty<FloorPlanCandidateTrace>(),
                    Selections = Array.Empty<FloorPlanSelectionTrace>(),
                    RejectionReason = "no reachable rooms"
                };
            }
            if (!ContainsRoom(snapshot.ReachableRooms, snapshot.PlayerRoomIndex))
            {
                return new FloorPlanTrace
                {
                    Plan = plan,
                    Candidates = Array.Empty<FloorPlanCandidateTrace>(),
                    Selections = Array.Empty<FloorPlanSelectionTrace>(),
                    RejectionReason = "player room is not reachable"
                };
            }

            var candidates = new List<RoomPlanEntry>(snapshot.Rooms.Count);
            var candidateTrace = new List<FloorPlanCandidateTrace>(snapshot.Rooms.Count);
            for (int i = 0; i < snapshot.Rooms.Count; i++)
            {
                var room = snapshot.Rooms[i];
                RoomPlanEntry entry;
                string reason;
                if (!ContainsRoom(snapshot.ReachableRooms, room.RoomIndex))
                {
                    entry = default;
                    reason = "unreachable";
                }
                else if (TryBuildPlanEntry(snapshot, room, hoardEvidenceState, out entry, out reason))
                {
                    candidates.Add(entry);
                }

                candidateTrace.Add(new FloorPlanCandidateTrace
                {
                    RoomIndex = room.RoomIndex,
                    Eligible = reason == "eligible" || reason == "searched-cached-hoard",
                    ShouldSearchHoard = entry.ShouldSearchHoard,
                    ShouldProbeHoard = entry.ShouldProbeHoard,
                    ShouldSearchChests = entry.ShouldSearchChests,
                    ShouldVisitForIntel = entry.ShouldVisitForIntel,
                    HoardEvidenceState = hoardEvidenceState,
                    BasePriority = GetBasePriority(entry),
                    Reason = reason
                });
            }

            int currentRoom = snapshot.PlayerRoomIndex;

            var selections = new List<FloorPlanSelectionTrace>(candidates.Count);
            while (candidates.Count > 0)
            {
                int bestIndex = 0;
                int bestDistance = int.MaxValue;
                int bestPassageDistance = int.MinValue;
                int bestBasePriority = int.MinValue;

                for (int i = 0; i < candidates.Count; i++)
                {
                    var candidate = candidates[i];
                    int distance = snapshot.RoomDistances[currentRoom, candidate.RoomIndex];
                    int passageDistance = snapshot.PassageRoomIndex >= 0
                        ? snapshot.RoomDistances[candidate.RoomIndex, snapshot.PassageRoomIndex]
                        : 0;
                    int basePriority = GetBasePriority(candidate);

                    if (distance < bestDistance ||
                        (distance == bestDistance && passageDistance > bestPassageDistance) ||
                        (distance == bestDistance && passageDistance == bestPassageDistance && basePriority > bestBasePriority))
                    {
                        bestIndex = i;
                        bestDistance = distance;
                        bestPassageDistance = passageDistance;
                        bestBasePriority = basePriority;
                    }
                }

                var next = candidates[bestIndex];
                selections.Add(new FloorPlanSelectionTrace
                {
                    Step = selections.Count,
                    FromRoomIndex = currentRoom,
                    SelectedRoomIndex = next.RoomIndex,
                    Distance = bestDistance,
                    PassageDistance = bestPassageDistance,
                    BasePriority = bestBasePriority
                });
                plan.Add(next);
                currentRoom = next.RoomIndex;
                candidates.RemoveAt(bestIndex);
            }

            return new FloorPlanTrace
            {
                Plan = plan,
                Candidates = candidateTrace,
                Selections = selections
            };
        }

        public static HoardEvidenceState ResolveHoardEvidenceState(in FloorPlanSnapshot snapshot)
        {
            if (!snapshot.BandedEnabled)
            {
                return HoardEvidenceState.Disabled;
            }

            if (snapshot.HoardOpenedThisFloor)
            {
                return HoardEvidenceState.AlreadyOpened;
            }

            if (snapshot.ChatSaysNoHoard || snapshot.InheritedNoHoardInferred)
            {
                return HoardEvidenceState.IntuitionNoHoard;
            }

            if (snapshot.CachedHoardIndicatorRoomIndex.HasValue)
            {
                return HoardEvidenceState.IntuitionDirect;
            }

            if (snapshot.ChatSaysHoard)
            {
                return HoardEvidenceState.IntuitionWaitingForIndicator;
            }

			if (snapshot.UsedIntuitionThisFloor)
			{
				return HoardEvidenceState.IntuitionPending;
			}

            if (snapshot.FloorsetHoardOpportunity == FloorsetHoardOpportunity.Maxed)
            {
                return HoardEvidenceState.FloorsetMaxed;
            }

            if (snapshot.FloorsetHoardOpportunity == FloorsetHoardOpportunity.ExcludedByDistribution)
            {
                return HoardEvidenceState.FloorsetDistributionExcluded;
            }

			if (snapshot.IntuitionActive)
			{
				return HoardEvidenceState.IntuitionActiveUnconfirmed;
			}

            return HoardEvidenceState.BlindSearch;
        }

        public static bool IsMandatoryHoardWorkTerminal(in FloorPlanSnapshot snapshot)
        {
            var state = ResolveHoardEvidenceState(snapshot);
            if (state is HoardEvidenceState.Disabled or
                HoardEvidenceState.AlreadyOpened or
                HoardEvidenceState.FloorsetMaxed or
                HoardEvidenceState.FloorsetDistributionExcluded or
                HoardEvidenceState.IntuitionNoHoard)
            {
                return true;
            }

            if (snapshot.Rooms == null ||
                snapshot.ReachableRooms == null ||
                snapshot.ReachableRooms.Count == 0 ||
                !ContainsRoom(snapshot.ReachableRooms, snapshot.PlayerRoomIndex))
                return false;

            for (int i = 0; i < snapshot.Rooms.Count; i++)
            {
                var room = snapshot.Rooms[i];
                if (!ContainsRoom(snapshot.ReachableRooms, room.RoomIndex))
                    continue;
                if (state == HoardEvidenceState.IntuitionActiveUnconfirmed &&
                    ShouldVisitForIntel(room, state, room.IsIntelVisited || room.IsSearched))
                {
                    return false;
                }

                if (state == HoardEvidenceState.BlindSearch &&
                    ShouldProbeHoard(snapshot, room, state, room.IsHoardSearched))
                {
                    return false;
                }
            }

            return state is HoardEvidenceState.IntuitionActiveUnconfirmed or HoardEvidenceState.BlindSearch;
        }

        private static bool TryBuildPlanEntry(
            in FloorPlanSnapshot snapshot,
            in PlanRoomData room,
            HoardEvidenceState hoardEvidenceState,
            out RoomPlanEntry entry,
            out string reason)
        {
            entry = default;

            bool hoardSearched = room.IsHoardSearched;
            bool chestsSearched = room.AreChestsSearched || room.IsSearched;
            bool intelVisited = room.IsIntelVisited || room.IsSearched;

            if (room.IsSearched)
            {
                if (hoardEvidenceState == HoardEvidenceState.IntuitionDirect &&
                    !snapshot.HoardOpenedThisFloor &&
                    snapshot.CachedHoardIndicatorRoomIndex.HasValue &&
                    snapshot.CachedHoardIndicatorRoomIndex.Value == room.RoomIndex &&
                    !hoardSearched)
                {
                    entry = new RoomPlanEntry(
                        room.RoomIndex,
                        shouldProbeHoard: true,
                        shouldSearchChests: false,
                        shouldVisitForIntel: false,
                        hoardEvidenceState);
                    reason = "searched-cached-hoard";
                    return true;
                }

                reason = "searched";
                return false;
            }

            bool shouldProbeHoard = ShouldProbeHoard(snapshot, room, hoardEvidenceState, hoardSearched);
            bool shouldSearchChests = ShouldSearchChests(snapshot, room, chestsSearched);
            bool shouldVisitForIntel = ShouldVisitForIntel(room, hoardEvidenceState, intelVisited);
            if (!shouldProbeHoard && !shouldSearchChests && !shouldVisitForIntel)
            {
                reason = hoardEvidenceState switch
                {
                    HoardEvidenceState.IntuitionPending => "intuition-pending",
                    HoardEvidenceState.BlindSearch when room.BlindHoardProbeSuppressed =>
                        "blind-hoard-unavailable",
                    _ => "no-objectives"
                };
                return false;
            }

            entry = new RoomPlanEntry(
                room.RoomIndex,
                shouldProbeHoard,
                shouldSearchChests,
                shouldVisitForIntel,
                hoardEvidenceState);
            reason = "eligible";
            return true;
        }

        private static bool ShouldProbeHoard(in FloorPlanSnapshot snapshot, in PlanRoomData room, HoardEvidenceState hoardEvidenceState, bool hoardSearched)
        {
            if (room.IsHome)
            {
                return false;
            }

            return hoardEvidenceState switch
            {
                HoardEvidenceState.BlindSearch =>
                    !hoardSearched && !room.BlindHoardProbeSuppressed,
                HoardEvidenceState.IntuitionDirect => snapshot.CachedHoardIndicatorRoomIndex == room.RoomIndex && !hoardSearched,
                _ => false
            };
        }

        private static bool ShouldVisitForIntel(in PlanRoomData room, HoardEvidenceState hoardEvidenceState, bool intelVisited)
        {
            return (hoardEvidenceState is HoardEvidenceState.IntuitionWaitingForIndicator or HoardEvidenceState.IntuitionActiveUnconfirmed) &&
                !room.IsHome &&
                !intelVisited;
        }

        private static bool ShouldSearchChests(in FloorPlanSnapshot snapshot, in PlanRoomData room, bool chestsSearched)
        {
            if (!snapshot.OpenChestsEnabled)
            {
                return false;
            }

            if (chestsSearched)
            {
                return false;
            }

            if (room.IsRevealed)
            {
                return room.HasEnabledChest;
            }

            if (room.HasKnownChestEntry)
            {
                return room.HasEnabledChest;
            }

            return true;
        }

        private static int GetBasePriority(RoomPlanEntry entry)
        {
            int priority = 0;
            if (entry.ShouldSearchHoard)
            {
                priority += HoardBasePriority;
            }

            if (entry.ShouldSearchChests)
            {
                priority += ChestBasePriority;
            }

            return priority;
        }

        private static bool ContainsRoom(IReadOnlyList<int> rooms, int roomIndex)
        {
            for (int i = 0; i < rooms.Count; i++)
            {
                if (rooms[i] == roomIndex)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
