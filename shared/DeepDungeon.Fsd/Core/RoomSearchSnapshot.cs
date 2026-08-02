using System.Collections.Generic;

namespace DeepDungeon.Fsd.Core
{
    public readonly record struct RoomSearchSnapshot
    {
        public int RoomIndex { get; init; }
        public bool ShouldSearchHoard { get; init; }
        public bool ShouldProbeHoard { get; init; }
        public bool ShouldSearchChests { get; init; }
        public bool ShouldVisitForIntel { get; init; }
        public HoardEvidenceState HoardEvidenceState { get; init; }
        public bool VisibleBandedChestInRoom { get; init; }
        public bool CachedHoardIndicatorInRoom { get; init; }
        public bool HasDetailedMapRoom { get; init; }
        public int DetailedMapCandidateCount { get; init; }
        public int FallbackTrapCount { get; init; }
        public IReadOnlyList<SnapshotChestEntry> VisibleChests { get; init; }
        public IReadOnlyList<SnapshotTrapIndicatorEntry> VisibleTrapIndicators { get; init; }
    }

    public readonly record struct SnapshotChestEntry
    {
        public SearchObjectiveType Type { get; init; }
    }

    public readonly record struct SnapshotTrapIndicatorEntry
    {
        public uint BaseId { get; init; }
        public bool IsInsideRoom { get; init; }
        public float X { get; init; }
        public float Y { get; init; }
        public float Z { get; init; }
        public int? MatchedSlotIndex { get; init; }
        public float? MatchedDistance { get; init; }
        public string MatchMethod { get; init; }
    }

    public enum SearchObjectiveType
    {
        Trap,
        ChestBronze,
        ChestSilver,
        ChestGold,
        ChestBanded
    }

    public enum RoomSearchWaypointSource
    {
        VisibleBandedChest,
        CachedHoardIndicator,
        DetailedMapCandidate,
        FallbackTrap,
        VisibleChest
    }
}
