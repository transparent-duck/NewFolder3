namespace DeepDungeon.Fsd.Runtime;

/// <summary>Boss movement strategies supported by the automatic deep-dungeon runner.</summary>
public static class DeepDungeonBossPolicy
{
    // Hiruko requires ordinary mechanic navigation; orbiting does not suppress its casts.
    public static bool UsesSpin(uint dungeonId, int floor) =>
        DeepDungeonFloorClassifier.Classify(dungeonId, floor) == DeepDungeonFloorKind.Boss &&
        dungeonId is 1 or 2 && (dungeonId, floor) != (2u, 30);
}
