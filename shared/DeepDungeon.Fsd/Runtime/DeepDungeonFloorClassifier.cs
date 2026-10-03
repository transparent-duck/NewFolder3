namespace DeepDungeon.Fsd.Runtime;

/// <summary>Fixed boss/result floors do not use random-room exploration.</summary>
public static class DeepDungeonFloorClassifier
{
    public static DeepDungeonFloorKind Classify(uint dungeonId, int floor)
    {
        int lastFloor = dungeonId == 1 ? 200 : 100;
        if (dungeonId is < 1 or > 4 || floor < 1 || floor > lastFloor)
            return DeepDungeonFloorKind.Unknown;
        if (floor == lastFloor)
            return DeepDungeonFloorKind.Result;
        return floor % 10 == 0 || dungeonId is 3 or 4 && floor == 99
            ? DeepDungeonFloorKind.Boss : DeepDungeonFloorKind.Mob;
    }

    public static float? AutomaticYOffset(in DeepDungeonStateSnapshot state) => state switch
    {
        { IsValid: false } or { IsTransitioning: true } => null,
        { IsInDeepDungeonTerritory: false } => 0f,
        { IsInDuty: true, FloorKind: DeepDungeonFloorKind.Mob } => -7f,
        { IsInDuty: true, FloorKind: DeepDungeonFloorKind.Boss or DeepDungeonFloorKind.Result } => 0f,
        _ => null
    };
}
