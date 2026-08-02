using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

internal sealed class ControlledPtSurveySession
{
    private static readonly byte[] ResearchFloorArray =
        Enumerable.Range(
                ControlledPtSurveyPolicy.FirstFloor,
                ControlledPtSurveyPolicy.LastResearchFloor - ControlledPtSurveyPolicy.FirstFloor + 1)
            .Select(floor => (byte)floor)
            .ToArray();

    public int? PinnedOccupiedSlotIndex { get; private set; }
    public IReadOnlyList<byte> ResearchFloors => ResearchFloorArray;
    public bool LeaveRequested { get; private set; }
    public bool AttemptSucceeded { get; private set; }
    public bool Fatal { get; private set; }
    public string FatalReason { get; private set; } = string.Empty;

    public void BeginAttempt()
    {
        LeaveRequested = false;
        AttemptSucceeded = false;
        Fatal = false;
        FatalReason = string.Empty;
    }

    public ControlledPtSaveSlotDecision ObserveAndPinSaveSlots(bool slot1Empty, bool slot2Empty)
    {
        var decision = ControlledPtSurveyPolicy.ValidateSaveSlots(slot1Empty, slot2Empty, PinnedOccupiedSlotIndex);
        if (decision.IsValid)
            PinnedOccupiedSlotIndex ??= decision.OccupiedSlotIndex;
        else
            Fail(decision.Error);
        return decision;
    }

    public void RequestSuccessfulLeave()
    {
        LeaveRequested = true;
    }

    public void MarkAttemptSucceeded()
    {
        AttemptSucceeded = true;
    }

    public void Fail(string reason)
    {
        Fatal = true;
        FatalReason = reason;
    }

}
