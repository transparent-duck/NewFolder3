using System.Collections.Generic;

namespace DeepDungeon.Fsd.Core
{
    public enum HoardEvidenceState
    {
        Disabled,
        AlreadyOpened,
        FloorsetMaxed,
        FloorsetDistributionExcluded,
        BlindSearch,
        IntuitionPending,
        IntuitionNoHoard,
        IntuitionWaitingForIndicator,
        IntuitionActiveUnconfirmed,
        IntuitionDirect
    }

    public readonly record struct FloorPlanSnapshot
    {
        public byte FloorNumber { get; init; }
        public int PlayerRoomIndex { get; init; }
        public int PassageRoomIndex { get; init; }
        public IReadOnlyList<int> ReachableRooms { get; init; }
        public int[,] RoomDistances { get; init; }
        public IReadOnlyList<PlanRoomData> Rooms { get; init; }
        public bool BandedEnabled { get; init; }
        public bool OpenChestsEnabled { get; init; }
        public bool HoardOpenedThisFloor { get; init; }
        public int FloorsetBandedCount { get; init; }
        public FloorsetHoardOpportunity FloorsetHoardOpportunity { get; init; }
        public int? CachedHoardIndicatorRoomIndex { get; init; }
        public bool IntuitionActive { get; init; }
        public bool ChatSaysHoard { get; init; }
        public bool ChatSaysNoHoard { get; init; }
        public bool InheritedNoHoardInferred { get; init; }
        public bool UsedIntuitionThisFloor { get; init; }
    }

    public readonly record struct PlanRoomData
    {
        public int RoomIndex { get; init; }
        public bool IsHome { get; init; }
        public bool IsSearched { get; init; }
        public bool IsHoardSearched { get; init; }
        public bool AreChestsSearched { get; init; }
        public bool IsIntelVisited { get; init; }
        public bool IsRevealed { get; init; }
        public bool HasKnownChestEntry { get; init; }
        public bool HasEnabledChest { get; init; }
    }

    public readonly record struct RoomPlanEntry
    {
        public RoomPlanEntry(int roomIndex, bool shouldSearchHoard, bool shouldSearchChests)
            : this(roomIndex, shouldSearchHoard, shouldSearchChests, false, HoardEvidenceState.Disabled)
        {
        }

        public RoomPlanEntry(
            int roomIndex,
            bool shouldProbeHoard,
            bool shouldSearchChests,
            bool shouldVisitForIntel,
            HoardEvidenceState hoardEvidenceState)
        {
            RoomIndex = roomIndex;
            ShouldProbeHoard = shouldProbeHoard;
            ShouldSearchChests = shouldSearchChests;
            ShouldVisitForIntel = shouldVisitForIntel;
            HoardEvidenceState = hoardEvidenceState;
        }

        public int RoomIndex { get; init; }
        public bool ShouldProbeHoard { get; init; }
        public bool ShouldSearchHoard => ShouldProbeHoard;
        public bool ShouldSearchChests { get; init; }
        public bool ShouldVisitForIntel { get; init; }
        public HoardEvidenceState HoardEvidenceState { get; init; }
    }
}
