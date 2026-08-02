namespace DeepDungeon.Fsd.Core;

/// <summary>
/// The explicit detailed-map scopes supported by the community evidence pipeline.
/// A scenario is identified by its dungeon, territory, floorset start, and covered
/// non-boss floors; callers must select one of these definitions rather than infer a
/// scope from whatever evidence happens to be present.
/// </summary>
public sealed record DetailedMapScenarioDefinition(
    string Key,
    string DisplayName,
    uint DungeonId,
    uint TerritoryId,
    byte FloorSetStart,
    byte FirstCoveredFloor,
    byte LastCoveredFloor)
{
    public int[] Floors => Enumerable
        .Range(FirstCoveredFloor, LastCoveredFloor - FirstCoveredFloor + 1)
        .ToArray();

    public bool Covers(uint dungeonId, uint territoryId, int floorSetStart, byte floor) =>
        DungeonId == dungeonId &&
        TerritoryId == territoryId &&
        FloorSetStart == floorSetStart &&
        floor >= FirstCoveredFloor &&
        floor <= LastCoveredFloor;
}

public static class DetailedMapScenarioCatalog
{
    public static readonly DetailedMapScenarioDefinition PilgrimsTraverse21To30 =
        new(
            "pt-21-30",
            "Pilgrim's Traverse 21-30",
            DungeonId: 4,
            TerritoryId: 1283,
            FloorSetStart: 21,
            FirstCoveredFloor: 21,
            LastCoveredFloor: 29);

    public static readonly DetailedMapScenarioDefinition PilgrimsTraverse31To40 =
        new(
            "pt-31-40",
            "Pilgrim's Traverse 31-40",
            DungeonId: 4,
            TerritoryId: 1284,
            FloorSetStart: 31,
            FirstCoveredFloor: 31,
            LastCoveredFloor: 39);

    private static readonly IReadOnlyList<DetailedMapScenarioDefinition> All =
    [
        PilgrimsTraverse21To30,
        PilgrimsTraverse31To40
    ];

    public static IReadOnlyList<DetailedMapScenarioDefinition> Definitions => All;

    public static bool TryGetByKey(
        string? key,
        out DetailedMapScenarioDefinition scenario)
    {
        scenario = null!;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        scenario = All.FirstOrDefault(value =>
            string.Equals(value.Key, key, StringComparison.Ordinal))!;
        return scenario != null;
    }

    public static bool TryGetByScope(
        uint dungeonId,
        uint territoryId,
        int floorSetStart,
        byte floor,
        out DetailedMapScenarioDefinition scenario)
    {
        scenario = All.FirstOrDefault(value =>
            value.Covers(dungeonId, territoryId, floorSetStart, floor))!;
        return scenario != null;
    }
}
