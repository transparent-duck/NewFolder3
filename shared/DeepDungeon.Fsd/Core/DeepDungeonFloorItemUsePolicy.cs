namespace DeepDungeon.Fsd.Core;

public static class DeepDungeonFloorItemUsePolicy
{
    public const byte PomanderUseProhibitedBanId = 1;
    public const byte PtIncenseUseProhibitedBanId = 7;

    public static bool CanUsePomanders(byte banId) =>
        banId != PomanderUseProhibitedBanId;

    public static bool CanUsePtIncense(byte banId) =>
        banId != PtIncenseUseProhibitedBanId;
}
