namespace DeepDungeon.Fsd.Core;

public enum CombatBuffUse { Eager, ReserveForBoss, None }

/// <summary>Mode intent; availability, channel ownership and confirmation remain runtime responsibilities.</summary>
public readonly record struct FarmingItemPolicy(CombatBuffUse CombatBuffs, bool EntryPassageIncense)
{
    public const float BossRefreshSeconds = 5f;
    public static FarmingItemPolicy For(FarmingMode? mode) => mode switch
    {
        FarmingMode.DeepProgression => new(CombatBuffUse.ReserveForBoss, true),
        FarmingMode.Aetherpool => new(CombatBuffUse.None, true),
        FarmingMode.HoardDiscovery => new(CombatBuffUse.None, false),
        _ => new(CombatBuffUse.Eager, true)
    };

    public bool AllowsCombatBuff(int stock, bool bossFloor, bool overcapRelief = false) =>
        stock > 0 && (CombatBuffs switch
        {
            CombatBuffUse.Eager => true,
            CombatBuffUse.ReserveForBoss => bossFloor || overcapRelief,
            _ => false
        });

    public static bool NeedsBossRefresh(float remainingSeconds) => remainingSeconds <= BossRefreshSeconds;
    public bool AllowsEntryIncense(bool floorSetup, double secondsSinceReady) =>
        EntryPassageIncense && floorSetup && secondsSinceReady <= 20;
}
