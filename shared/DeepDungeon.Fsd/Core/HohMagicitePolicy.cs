namespace DeepDungeon.Fsd.Core;

/// <summary>Shared three-slot inventory: relieve overflow early, spend one per normal floor after 90.</summary>
public static class HohMagicitePolicy
{
    public static bool ShouldUse(int floor, bool overflow, bool alreadyUsedThisFloor) =>
        floor is > 0 and < 100 && floor % 10 != 0 &&
        (overflow || floor > 90 && !alreadyUsedThisFloor);
}
