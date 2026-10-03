namespace DeepDungeon.Fsd.Core;

public enum FarmingMode { Hoard, Aetherpool, HoardDiscovery, DeepProgression }
public enum SaveUse { Create, Prepared }

/// <summary>Validated, immutable intent shared by UI, API and the runtime.</summary>
public sealed record FarmingPlan(
    FarmingMode Mode, SaveUse SaveUse, int StartFloor, int TargetCycles, bool Infinite,
    bool BandedEnabled, bool OpenGold, bool OpenSilver, bool OpenBronze)
{
    public string DisplayName => Mode switch
    {
        FarmingMode.Hoard => "挖寶藏",
        FarmingMode.Aetherpool => "強化／陶片",
        FarmingMode.HoardDiscovery => "寶藏發現成就",
        _ => "死靈術士(beta)"
    };
    // Optional run-only diagnostic boundary; normal progression still climbs to 100.
    public int? StopAfterFloor { get; init; }
    // Bridge-only checkpoint tests; UI challenge runs still start/restart at floor one.
    public bool UsesDiagnosticCheckpoints { get; init; }
    public bool ReusesSave => Mode is FarmingMode.Aetherpool or FarmingMode.HoardDiscovery;
    public bool ShowsDetailedMap => Mode == FarmingMode.Hoard && BandedEnabled;
    public string CycleUnit => Mode == FarmingMode.DeepProgression ? "登頂次數" : Mode == FarmingMode.Hoard ? "挖寶輪數" : "採集輪數";

    public static bool TryCreate(FarmingMode mode, SaveUse saveUse, int startFloor,
        int cycles, bool infinite, bool hoard, bool gold, bool silver, bool bronze,
        out FarmingPlan? plan, out string error)
    {
        plan = null;
        error = !Enum.IsDefined(mode) || !Enum.IsDefined(saveUse) ? "未知玩法或存檔用途。"
            : cycles < 1 ? "循環次數必須至少為 1。"
            : mode == FarmingMode.Hoard && saveUse != SaveUse.Create
                ? "固定層段模式只使用空槽位新建存檔，不接受既有存檔。"
            : mode is FarmingMode.Aetherpool or FarmingMode.HoardDiscovery && saveUse != SaveUse.Prepared
                ? "此玩法需要唯一的準備存檔。"
            : mode == FarmingMode.Aetherpool && !silver && !bronze
                ? "強化／陶片玩法至少選擇銀箱或銅箱。"
            : !FarmingTargetPolicy.IsValid(mode, new(hoard, gold, silver, bronze))
                ? "此模式的寶藏／寶箱選項不相容。"
            : mode == FarmingMode.DeepProgression && (saveUse != SaveUse.Create || startFloor != 1)
                ? "深層攻略必須從第一層新建存檔。"
            : mode == FarmingMode.Hoard && saveUse == SaveUse.Create && startFloor is not (21 or 31)
                ? "目前固定層段挖寶支援 PT 21–30、31–40。"
            : string.Empty;
        if (error.Length != 0) return false;
        plan = new(mode, saveUse, saveUse == SaveUse.Prepared ? 0 : startFloor,
            cycles, infinite, hoard, gold, silver, bronze);
        return true;
    }
}

public readonly record struct FarmingTargets(bool Hoard, bool Gold, bool Silver, bool Bronze);

public static class FarmingTargetPolicy
{
    public static bool LocksHoard(FarmingMode mode) => mode is FarmingMode.Aetherpool or FarmingMode.HoardDiscovery or FarmingMode.DeepProgression;
    public static bool LocksGold(FarmingMode mode) => LocksHoard(mode);
    public static bool LocksSilverBronze(FarmingMode mode) => mode is FarmingMode.HoardDiscovery or FarmingMode.DeepProgression;

    public static FarmingTargets Constrain(FarmingMode mode, in FarmingTargets targets) => mode switch
    {
        FarmingMode.Aetherpool => new(false, false, targets.Silver || !targets.Bronze, targets.Bronze),
        FarmingMode.HoardDiscovery => new(true, false, false, false),
        FarmingMode.DeepProgression => new(false, true, true, false),
        _ => targets
    };

    public static bool IsValid(FarmingMode mode, in FarmingTargets targets) =>
        targets == Constrain(mode, targets) && (mode != FarmingMode.Aetherpool || targets.Silver || targets.Bronze);
}

public readonly record struct SaveSlotSnapshot(int Index, bool Empty, int StartFloor, string Progress, string Identity = "", bool Enterable = true);

public static class SaveSlotUiPolicy
{
    public static bool IsBlockingMessage(string label, string? failedLabel, string? completedLabel) =>
        (!string.IsNullOrWhiteSpace(failedLabel) && label == failedLabel) ||
        (!string.IsNullOrWhiteSpace(completedLabel) && label == completedLabel);

    public static bool TryParse(int index, string? progress, string? emptyLabel, string identity,
        bool enabled, out SaveSlotSnapshot slot, out string error)
    {
        slot = default;
        error = $"存檔 {index + 1} 的進度文字尚未載入或無法識別。";
        if (index < 0 || string.IsNullOrWhiteSpace(progress) || string.IsNullOrWhiteSpace(emptyLabel)) return false;
        progress = progress.Trim();
        bool empty = string.Equals(progress, emptyLabel.Trim(), StringComparison.Ordinal);
        int floor = 0;
        if (!empty)
        {
            var numbers = System.Text.RegularExpressions.Regex.Matches(progress, "[0-9]+");
            if (numbers.Count != 1 || !int.TryParse(numbers[0].Value, out floor)) return false;
        }
        slot = new(index, empty, floor, progress, identity, enabled);
        error = string.Empty;
        return true;
    }
}

public static class PreparedSavePolicy
{
    public static bool TryResolve(IReadOnlyList<SaveSlotSnapshot> slots,
        SaveSlotSnapshot? pinned, bool reusable, int? expectedFloor,
        out SaveSlotSnapshot source, out string error, int? requiredSlot = null)
    {
        source = default;
        error = string.Empty;
        int occupied = 0;
        bool managed = requiredSlot.HasValue && !reusable;
        foreach (var slot in slots)
            if (!slot.Empty && (!managed || slot.Index == requiredSlot.GetValueOrDefault())) { source = slot; occupied++; }
        if (occupied != 1)
            error = managed ? "本任務管理的存檔不存在或槽位資料不唯一；已停止，不會改選其他存檔。"
                : occupied == 0 ? "請先準備一個存檔。" : "使用既有存檔要求有且只有一個存檔；請自行整理後重試。";
        else if (source.Index < 0)
            error = "無法識別來源槽位。";
        else if (!source.Enterable)
            error = managed ? "本任務存檔目前無法入場；已停止，不會改選其他存檔。"
                : "唯一存檔目前無法入場，不能被當成空槽位。";
        else if (source.StartFloor is < 1 or > 91 || source.StartFloor % 10 != 1)
            error = "無法可靠識別存檔起點，或存檔已完成／不可入場。";
        else if (reusable && source.StartFloor > 21)
            error = "PT 31 層起棄本會使存檔失效；複用玩法只接受 1、11、21 層起點。";
        else if (pinned.HasValue && (source.Index != pinned.Value.Index ||
                 source.Progress != pinned.Value.Progress || source.Identity != pinned.Value.Identity))
            error = "來源存檔已變更；已停止，不會改選另一份存檔。";
        else if (expectedFloor.HasValue && source.StartFloor != expectedFloor.Value)
            error = $"存檔起點為 {source.StartFloor}，預期為 {expectedFloor.Value}；已停止。";
        return error.Length == 0;
    }
}

public static class OwnedSavePolicy
{
    public static string? ValidateForDeletion(IReadOnlyList<SaveSlotSnapshot> slots,
        int ownedSlot, int entryFloor, SaveSlotSnapshot? pinned)
    {
        SaveSlotSnapshot source = default;
        int matches = 0;
        foreach (var slot in slots)
            if (slot.Index == ownedSlot) { source = slot; matches++; }
        if (ownedSlot < 0 || entryFloor < 1 || matches != 1 || source.Empty || source.Progress == null ||
            source.StartFloor < entryFloor || source.StartFloor > entryFloor + 10)
            return "本任務存檔內容與預期層段不符；停止清理。";
        if (pinned is { } expected && (source.Index != expected.Index || source.Identity != expected.Identity))
            return "本任務存檔來源資訊已變更；停止清理。";
        return null;
    }
}
