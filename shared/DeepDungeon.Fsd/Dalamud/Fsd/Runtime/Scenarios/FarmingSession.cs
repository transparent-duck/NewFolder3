using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

internal sealed class FarmingSession(FarmingPlan plan)
{
    public FarmingPlan Plan { get; } = plan;
    public PreparedSaveBinding Source { get; set; } = new();
    public bool BandedEnabled { get; set; } = plan.BandedEnabled;
    public bool OpenGold { get; set; } = plan.OpenGold;
    public bool OpenSilver { get; set; } = plan.OpenSilver;
    public bool OpenBronze { get; set; } = plan.OpenBronze;
    public int NextFloor { get; set; } = plan.StartFloor;
    public int OwnedSlot { get; set; } = -1;
    public int Failures { get; set; }
    public int Discoveries { get; set; }
}

internal sealed class PreparedSaveBinding
{
    public SaveSlotSnapshot? Pinned { get; private set; }
    public bool Reusable { get; set; }
    public int? ExpectedFloor { get; set; }
    public int? RequiredSlot { get; set; }
    public string Description => Pinned is { } value ? $"存檔 {value.Index + 1}，起點 {value.StartFloor} 層" : "唯一存檔待讀取";

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
