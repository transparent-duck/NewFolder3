using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public readonly record struct TerminalRewardProfile(uint BaseId, uint ItemId);

/// <summary>Current Lumina EObj/Item identities and the result scene's far event region.</summary>
public static class Potd200RewardPolicy
{
    public const uint ExitBaseId = 2005809;
    public static readonly Vector3 Approach = new(400.4518f, .4192103f, 199.8325f);
    public static bool IsTerminal(uint dungeonId, int floor) => dungeonId == 1 && floor == 200;

    public static bool TryGet(uint baseId, out TerminalRewardProfile profile)
    {
        profile = baseId switch
        {
            2007544 => new(baseId, 16168), // Firecrest
            2007545 => new(baseId, 16169), // Glass Pumpkin
            _ => default
        };
        return profile.BaseId != 0;
    }
}
