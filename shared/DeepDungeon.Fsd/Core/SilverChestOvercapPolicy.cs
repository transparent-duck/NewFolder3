namespace DeepDungeon.Fsd.Core;

public enum SilverChestOvercapDecision
{
    RetryChest,
    UseSerenityIncense,
    SkipChest
}

public static class SilverChestOvercapPolicy
{
    public static SilverChestOvercapDecision Decide(int sharedIncenseCount, bool serenityAvailable, bool incenseAllowed)
    {
        if (sharedIncenseCount < 3)
            return SilverChestOvercapDecision.RetryChest;
        return serenityAvailable && incenseAllowed
            ? SilverChestOvercapDecision.UseSerenityIncense
            : SilverChestOvercapDecision.SkipChest;
    }
}
