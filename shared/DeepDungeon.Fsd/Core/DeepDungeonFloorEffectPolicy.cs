namespace DeepDungeon.Fsd.Core;

/// <summary>
/// Classifies Deep Dungeon floor-effect row IDs in their native Excel-sheet spaces.
/// </summary>
public static class DeepDungeonFloorEffectPolicy
{
    public static bool HasHarmfulSerenityRemovableEffect(
        byte statusRowId,
        byte banRowId,
        byte dangerRowId) =>
        IsHarmfulStatus(statusRowId) ||
        IsHarmfulBan(banRowId) ||
        IsHarmfulDanger(dangerRowId);

    /// <summary>
    /// Classifies a <c>DeepDungeonStatus</c> row ID.
    /// </summary>
    public static bool IsHarmfulStatus(byte rowId) =>
        rowId is 1 or 2 or 3 or 5;

    /// <summary>
    /// Classifies a <c>DeepDungeonBan</c> row ID.
    /// </summary>
    public static bool IsHarmfulBan(byte rowId) =>
        rowId is 1 or 2 or 4 or 5 or 6 or 7;

    /// <summary>
    /// Classifies a <c>DeepDungeonDanger</c> row ID.
    /// </summary>
    public static bool IsHarmfulDanger(byte rowId) =>
        rowId == 1;
}
