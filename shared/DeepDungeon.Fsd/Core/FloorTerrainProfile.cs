namespace DeepDungeon.Fsd.Core;

/// <summary>Shared by the floor-map baker and its runtime adapter.</summary>
public static class FloorTerrainProfile
{
    public const float BakedBodyRadius = .5f;

    // Native ActiveLayoutIndex is 0 for both RoomA and RoomC. BMR's
    // world-state adapter uses the initialization type to select the room bank.
    public static int LayoutIndex(byte initializationType) => initializationType switch
    {
        1 => 0,
        2 => 1,
        5 => 2,
        _ => -1
    };
}
