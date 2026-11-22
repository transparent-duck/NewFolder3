namespace DeepDungeon.Fsd.Core;

/// <summary>Deep-progression pull exclusions, keyed by language-independent BNpcName row IDs.</summary>
public static class MobPullPolicy
{
    public static bool CanInitiate(FarmingMode? mode, uint dungeonId, int floor, uint nameId)
    {
        if (mode != FarmingMode.DeepProgression) return true;
        return dungeonId switch
        {
            1 => !IsPotdHighRisk(floor, nameId),
            2 => nameId != 7584, // Heavenly centaur.
            4 => !IsPtHighRisk(floor, nameId),
            _ => true
        };
    }

    private static bool IsPotdHighRisk(int floor, uint nameId) =>
        nameId == 5464 || // Flood dragon.
        floor is >= 41 and <= 50 && nameId == 5026 || // Nightmare gnat.
        floor is >= 71 and <= 80 && nameId == 5331 || // Palace anzu.
        floor is >= 81 and <= 90 && nameId == 5338 || // Palace worm.
        floor is >= 111 and <= 115 && nameId == 5375 || // Palace slime.
        floor is >= 141 and <= 145 && nameId == 5412 || // Deep palace horsefly.
        floor >= 176 && nameId == 5459 || // Deep palace anzu.
        floor >= 181 && nameId == 5469; // Deep palace vamoth.

    private static bool IsPtHighRisk(int floor, uint nameId) =>
        floor is >= 61 and <= 69 && nameId == 14179 || // PT66: lethal needle cast.
        floor is >= 71 and <= 79 && nameId == 14194; // Rockeater poison.
}
