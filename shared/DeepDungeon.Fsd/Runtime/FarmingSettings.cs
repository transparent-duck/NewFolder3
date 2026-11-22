using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

public sealed class FarmingSettings
{
    public uint DungeonId { get; set; } = 4;
    public FarmingMode Mode { get; set; }
    public Dictionary<string, FarmingPreferences> Preferences { get; set; } = new();

    public FarmingPreferences GetPreferences(FsdSettings legacy, FarmingMode? modeOverride = null)
    {
        var mode = modeOverride ?? Mode;
        string key = mode switch
        {
            FarmingMode.Hoard => "Hoard:Create",
            FarmingMode.Aetherpool => "Aetherpool:Prepared",
            FarmingMode.HoardDiscovery => "HoardDiscovery:Prepared",
            _ => "DeepProgression:Create"
        };
        if (!Preferences.TryGetValue(key, out var value))
        {
            value = new FarmingPreferences
            {
                OpenGold = mode != FarmingMode.HoardDiscovery && legacy.NecromancerAutoOpenGoldChest,
                OpenSilver = mode == FarmingMode.Aetherpool || mode != FarmingMode.HoardDiscovery && legacy.NecromancerAutoOpenSilverChest,
                OpenBronze = mode == FarmingMode.Aetherpool || mode != FarmingMode.HoardDiscovery && legacy.NecromancerAutoOpenBronzeChest,
                Cycles = legacy.NecromancerFsdLoopCount,
                Infinite = legacy.NecromancerFsdLoopInfinite
            };
            Preferences.Add(key, value);
        }
        value.BandedEnabled ??= mode == FarmingMode.Hoard && legacy.NecromancerAutoBandedFarmEnabled;
        var targets = FarmingTargetPolicy.Constrain(mode, new(value.BandedEnabled.Value,
            value.OpenGold, value.OpenSilver, value.OpenBronze));
        value.BandedEnabled = targets.Hoard;
        value.OpenGold = targets.Gold;
        value.OpenSilver = targets.Silver;
        value.OpenBronze = targets.Bronze;
        return value;
    }
}

public sealed class FarmingPreferences
{
    public bool? BandedEnabled { get; set; }
    public bool OpenGold { get; set; }
    public bool OpenSilver { get; set; }
    public bool OpenBronze { get; set; }
    public int Cycles { get; set; } = 1;
    public bool Infinite { get; set; }
}
