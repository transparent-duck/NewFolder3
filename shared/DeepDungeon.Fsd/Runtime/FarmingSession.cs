using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

internal sealed class FarmingSession(FarmingPlan plan, int ownedSlot = -1, bool resumeCurrentDuty = false)
{
    public FarmingPlan Plan { get; } = plan;
    public PreparedSaveBinding Source { get; private set; } = new();
    public bool BandedEnabled { get; private set; } = plan.BandedEnabled;
    public bool OpenGold { get; private set; } = plan.OpenGold;
    public bool OpenSilver { get; private set; } = plan.OpenSilver;
    public bool OpenBronze { get; private set; } = plan.OpenBronze;
    public int NextFloor { get; private set; } = plan.StartFloor;
    public int OwnedSlot { get; private set; } = ownedSlot;
    public bool ResumeCurrentDuty { get; } = resumeCurrentDuty;
    public int Failures { get; private set; }
    public int Discoveries { get; private set; }

    public bool TrySetTargets(in FarmingTargets targets)
    {
        if (!FarmingTargetPolicy.IsValid(Plan.Mode, targets)) return false;
        BandedEnabled = targets.Hoard;
        OpenGold = targets.Gold;
        OpenSilver = targets.Silver;
        OpenBronze = targets.Bronze;
        return true;
    }

    public PreparedSaveBinding PrepareSource()
    {
        Source.ConfigureForEntry(Plan.ReusesSave, Plan.SaveUse == SaveUse.Prepared ? null : NextFloor,
            OwnedSlot >= 0 ? OwnedSlot : null);
        return Source;
    }

    public bool BindCreatedSave(int slot)
    {
        OwnedSlot = slot;
        return slot >= 0;
    }

    public void ConfirmOwnedSaveDeleted()
    {
        OwnedSlot = -1;
        Source = new PreparedSaveBinding();
    }

    public void CompleteAttempt(in FarmingSessionDecision decision, int discoveries)
    {
        NextFloor = decision.NextFloor;
        Discoveries += discoveries;
        if (decision.RecoverFailure) Failures++;
    }
}

internal sealed class PreparedSaveBinding
{
    public SaveSlotSnapshot? Pinned { get; private set; }
    public bool Reusable { get; private set; }
    public int? ExpectedFloor { get; private set; }
    public int? RequiredSlot { get; private set; }
    public string Description => Pinned is { } value ? $"存檔 {value.Index + 1}，起點 {value.StartFloor} 層" : "唯一存檔待讀取";

    public PreparedSaveBinding(bool reusable = false, int? expectedFloor = null, int? requiredSlot = null) =>
        ConfigureForEntry(reusable, expectedFloor, requiredSlot);

    public void ConfigureForEntry(bool reusable, int? expectedFloor, int? requiredSlot)
    {
        Reusable = reusable;
        ExpectedFloor = expectedFloor;
        RequiredSlot = requiredSlot;
    }

    public bool Observe(IReadOnlyList<SaveSlotSnapshot> slots, out SaveSlotSnapshot source, out string error)
    {
        if (!PreparedSavePolicy.TryResolve(slots, Reusable ? Pinned : null, Reusable,
                ExpectedFloor, out source, out error, RequiredSlot)) return false;
        // Non-reusable progress is permitted to advance only to the explicitly expected floorset.
        if (Pinned.HasValue && source.Index != Pinned.Value.Index)
        {
            error = "來源存檔槽位已變更；已停止。";
            return false;
        }
        if (Pinned is { } pinned && (source.Identity != pinned.Identity ||
            ((!ExpectedFloor.HasValue || pinned.StartFloor == ExpectedFloor.Value) && source.Progress != pinned.Progress)))
        {
            error = "來源存檔內容已變更；已停止。";
            return false;
        }
        Pinned = source;
        return true;
    }
}
