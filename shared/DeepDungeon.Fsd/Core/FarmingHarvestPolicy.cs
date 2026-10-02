namespace DeepDungeon.Fsd.Core;

public static class FarmingHarvestPolicy
{
    // A confirmed item use belongs to this floor only. It permits its passage
    // transition even if the last item was consumed; the next floor must exit
    // after chest work unless it has another usable item. Natural passage state
    // never overrides this resource limit.
    public static bool ShouldLeave(bool chestWorkComplete, bool canUsePassageItem,
        bool passageItemPending, bool passageItemConfirmed) =>
        chestWorkComplete && !canUsePassageItem && !passageItemPending && !passageItemConfirmed;
}
