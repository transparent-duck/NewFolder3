namespace DeepDungeon.Fsd.Runtime;

public enum DeepDungeonFloorKind
{
    Unknown,
    Mob,
    Boss
}

public readonly record struct DeepDungeonStateSnapshot(
    bool IsValid,
    bool IsInDeepDungeonTerritory,
    bool IsInDuty,
    uint DungeonId,
    byte Floor,
    DeepDungeonFloorKind FloorKind,
    bool IsTransitioning,
    long Revision)
{
    public bool SemanticallyEquals(in DeepDungeonStateSnapshot other) =>
        IsValid == other.IsValid &&
        IsInDeepDungeonTerritory == other.IsInDeepDungeonTerritory &&
        IsInDuty == other.IsInDuty &&
        DungeonId == other.DungeonId &&
        Floor == other.Floor &&
        FloorKind == other.FloorKind &&
        IsTransitioning == other.IsTransitioning;
}
