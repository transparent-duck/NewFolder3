using System.Collections.Generic;

namespace DeepDungeon.Fsd.Core
{
    public readonly record struct FloorPlanTrace
    {
        public IReadOnlyList<RoomPlanEntry> Plan { get; init; }
        public IReadOnlyList<FloorPlanCandidateTrace> Candidates { get; init; }
        public IReadOnlyList<FloorPlanSelectionTrace> Selections { get; init; }
        public string? RejectionReason { get; init; }
    }

    public readonly record struct FloorPlanCandidateTrace
    {
        public int RoomIndex { get; init; }
        public bool Eligible { get; init; }
        public bool ShouldSearchHoard { get; init; }
        public bool ShouldProbeHoard { get; init; }
        public bool ShouldSearchChests { get; init; }
        public bool ShouldVisitForIntel { get; init; }
        public HoardEvidenceState HoardEvidenceState { get; init; }
        public int BasePriority { get; init; }
        public string Reason { get; init; }
    }

    public readonly record struct FloorPlanSelectionTrace
    {
        public int Step { get; init; }
        public int FromRoomIndex { get; init; }
        public int SelectedRoomIndex { get; init; }
        public int Distance { get; init; }
        public int PassageDistance { get; init; }
        public int BasePriority { get; init; }
    }
}
