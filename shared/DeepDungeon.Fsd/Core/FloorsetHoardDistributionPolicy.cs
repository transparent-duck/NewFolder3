namespace DeepDungeon.Fsd.Core;

public enum FloorsetHoardOpportunity
{
    Possible,
    ExcludedByDistribution,
    Maxed
}

public readonly record struct FloorsetHoardDistributionState(
    int TotalHoardCount,
    int SatisfiedSegmentMask);

public static class FloorsetHoardDistributionPolicy
{
    public const int MaxHoardsPerFloorset = 5;
    public const int AllSegmentsMask = 0b111;

    public static FloorsetHoardOpportunity Decide(
        in FloorsetHoardDistributionState state,
        byte floorNumber)
    {
        if (state.TotalHoardCount >= MaxHoardsPerFloorset)
            return FloorsetHoardOpportunity.Maxed;

        int currentSegmentBit = GetSegmentBit(floorNumber);
        if (currentSegmentBit == 0 ||
            (state.SatisfiedSegmentMask & currentSegmentBit) == 0)
        {
            return FloorsetHoardOpportunity.Possible;
        }

        int unsatisfiedSegmentCount = CountBits(
            AllSegmentsMask & ~state.SatisfiedSegmentMask);
        int remainingCapacity = MaxHoardsPerFloorset - state.TotalHoardCount;
        return remainingCapacity <= unsatisfiedSegmentCount
            ? FloorsetHoardOpportunity.ExcludedByDistribution
            : FloorsetHoardOpportunity.Possible;
    }

    public static bool AllowsHoardPomander(FloorsetHoardOpportunity opportunity)
    {
        return opportunity == FloorsetHoardOpportunity.Possible;
    }

    public static int GetSegmentBit(byte floorNumber)
    {
        int floorWithinFloorset = ((floorNumber - 1) % 10) + 1;
        return floorWithinFloorset switch
        {
            >= 1 and <= 3 => 0b001,
            >= 4 and <= 6 => 0b010,
            >= 7 and <= 9 => 0b100,
            _ => 0
        };
    }

    private static int CountBits(int value)
    {
        int count = 0;
        while (value != 0)
        {
            value &= value - 1;
            count++;
        }
        return count;
    }
}
