using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

/// <summary>Tracks the floor's planning cadence and evidence acknowledgements.</summary>
public sealed class FloorPlanningSession
{
    private DateTime _lastGeneralTickAt = DateTime.MinValue;
    private DateTime _nextLateEvidencePollAt = DateTime.MinValue;
    public int LastKnownHoardCount { get; private set; }
    public bool RefreshRequested { get; private set; }
    public long PendingEvidenceVersion { get; private set; }
    public long ReconciledEvidenceVersion { get; private set; }
    public byte PendingEvidenceFloor { get; private set; } = 255;
    public uint PendingEvidenceDungeonId { get; private set; }
    public string PendingEvidenceReason { get; private set; } = string.Empty;
    public bool SetupPlanGenerated { get; private set; }

    public void ObserveHoardCount(int count) => LastKnownHoardCount = count;
    public void MarkSetupPlanGenerated(bool generated = true) => SetupPlanGenerated = generated;

    public void RequestRefresh(byte floor, uint dungeonId, string reason)
    {
        RefreshRequested = true;
        PendingEvidenceVersion++;
        PendingEvidenceFloor = floor;
        PendingEvidenceDungeonId = dungeonId;
        PendingEvidenceReason = reason;
    }

    public bool MatchesFloor(byte floor, uint dungeonId) =>
        PendingEvidenceFloor == floor && PendingEvidenceDungeonId == dungeonId;

    public void Acknowledge(long consumedVersion)
    {
        var acknowledgement = LateHoardEvidencePlanner.AcknowledgeVersion(
            PendingEvidenceVersion, ReconciledEvidenceVersion, consumedVersion);
        ReconciledEvidenceVersion = acknowledgement.ReconciledVersion;
        RefreshRequested = acknowledgement.RefreshPending;
    }

    public void DiscardPending()
    {
        ReconciledEvidenceVersion = PendingEvidenceVersion;
        RefreshRequested = false;
    }

    public bool TryBeginGeneralTick(DateTime now, TimeSpan interval)
    {
        if (now - _lastGeneralTickAt < interval) return false;
        _lastGeneralTickAt = now;
        return true;
    }

    public bool TryBeginLateEvidencePoll(DateTime now, TimeSpan interval)
    {
        if (now < _nextLateEvidencePollAt) return false;
        _nextLateEvidencePollAt = now.Add(interval);
        return true;
    }
}
