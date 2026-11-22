namespace DeepDungeon.Fsd.Core;

public enum FarmingEntryAction { Wait, Enter, Attach, Conflict }

/// <summary>Current observations only. Ready excludes loading and unavailable duty data.</summary>
public readonly record struct FarmingEntryFacts(bool Ready, bool InDuty, uint DungeonId, int Floor);

public readonly record struct FarmingEntryDecision(FarmingEntryAction Action, string Reason);

/// <summary>Select entry or attachment before this attempt has an active entry controller.</summary>
public static class FarmingEntryPolicy
{
    public static FarmingEntryDecision Decide(in FarmingEntryFacts facts, uint dungeonId, int entryFloor, int ownedSlot)
    {
        if (!facts.Ready || facts.InDuty && facts.Floor <= 0)
            return new(FarmingEntryAction.Wait, "等待過圖、角色及副本資料。");
        if (!facts.InDuty)
            return new(FarmingEntryAction.Enter, "由入口確認來源並入場。");
        if (facts.DungeonId != dungeonId || facts.Floor < entryFloor || facts.Floor >= entryFloor + 10)
            return new(FarmingEntryAction.Conflict, "目前副本的地宮／層段與任務不符；保留副本與存檔。");
        if (ownedSlot is < 0 or > 1)
            return new(FarmingEntryAction.Conflict, "目前副本缺少已指定的來源槽位；保留副本與存檔。");
        return new(FarmingEntryAction.Attach, "接管目前副本。");
    }
}
