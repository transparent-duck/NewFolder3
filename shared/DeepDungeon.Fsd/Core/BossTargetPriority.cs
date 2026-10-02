namespace DeepDungeon.Fsd.Core;

/// <summary>Kill adds before the boss, choosing the central add rather than an injured edge add.</summary>
public readonly record struct BossTargetPriority(uint MaxHp, float CenterDistanceSquared, ulong EntityId)
{
    public bool Precedes(in BossTargetPriority other) =>
        MaxHp < other.MaxHp || MaxHp == other.MaxHp &&
        (CenterDistanceSquared < other.CenterDistanceSquared ||
         CenterDistanceSquared == other.CenterDistanceSquared && EntityId < other.EntityId);
}
