namespace DeepDungeon.Fsd.Core;

public enum Pt99TargetKind { WaitForColor, Grief, Eater, Minion, WaitForBalance, Finished, WaitForBossState }

public readonly record struct Pt99TargetDecision(Pt99TargetKind Target, double HpDifference)
{
    public bool SuppressOutput => Target is Pt99TargetKind.WaitForColor or Pt99TargetKind.WaitForBalance or Pt99TargetKind.Finished or Pt99TargetKind.WaitForBossState;
}

/// <summary>Attack eligibility only; BMR retains color/mechanic movement ownership.</summary>
public static class Pt99TargetPolicy
{
    public const uint GriefBaseId = 0x48EA, EaterBaseId = 0x48EB, MinionBaseId = 0x48EC;
    public const uint LightStatusId = 4560, DarkStatusId = 4559;

    public static Pt99TargetDecision Decide(bool light, bool dark, uint griefHp, uint griefMaxHp,
        uint eaterHp, uint eaterMaxHp, bool minionAvailable)
    {
        double difference = griefMaxHp == 0 || eaterMaxHp == 0 ? 0 :
            100d * eaterHp / eaterMaxHp - 100d * griefHp / griefMaxHp;
        if (minionAvailable) return new(Pt99TargetKind.Minion, difference);
        if (griefMaxHp == 0 || eaterMaxHp == 0) return new(Pt99TargetKind.WaitForBossState, difference);
        if (griefHp <= 1 && eaterHp <= 1) return new(Pt99TargetKind.Finished, difference);
        // BMR's color AI only runs after pull. Without a color, initiate on a live
        // boss so the encounter can activate instead of waiting on each other.
        if (!light && !dark)
            return new(griefHp > 1 ? Pt99TargetKind.Grief : Pt99TargetKind.Eater, difference);
        if (light && dark)
            return new(Pt99TargetKind.WaitForColor, difference);
        bool canAttack = light ? griefHp > 1 : eaterHp > 1;
        // Match installed BMR's >25 switch boundary: stopping earlier would prevent
        // BMR from requesting the other color and deadlock a solo run.
        bool fallingBehind = light ? difference > 25 : difference < -25;
        return new(!canAttack || fallingBehind ? Pt99TargetKind.WaitForBalance :
            light ? Pt99TargetKind.Grief : Pt99TargetKind.Eater, difference);
    }
}
