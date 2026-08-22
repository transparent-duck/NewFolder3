using System.Text.Json.Serialization;

namespace DeepDungeon.Fsd.Runtime;

public sealed class FsdSettings
{
    [JsonIgnore]
    private IFsdSettingsStore? _store;

    public bool AutoUseRecoveryPotion { get; set; }
    public int RecoveryPotionHpThresholdPercent { get; set; } = 90;
    public bool NecromancerAutoOpenGoldChest { get; set; }
    public bool NecromancerAutoOpenSilverChest { get; set; }
    public bool NecromancerAutoOpenBronzeChest { get; set; } = true;
    public bool AggressiveChestInteraction { get; set; }
    public float NecromancerChestInteractDistance { get; set; } = 3.5f;
    public bool NecromancerShowRoomCenterOverlay { get; set; } = true;
    public bool NecromancerShowTrapOverlay { get; set; } = true;
    public bool NecromancerShowRoomPathOverlay { get; set; } = true;
    public bool NecromancerShowWaypointOverlay { get; set; } = true;
    public bool NecromancerShowBgCollisionOverlay { get; set; } = true;
    public bool NecromancerAutoBandedFarmEnabled { get; set; } = true;
    public float NecromancerBandedScanRadius { get; set; } = 30f;
    public float NecromancerBandedStandSeconds { get; set; } = 2.5f;
    public bool NecromancerBandedAutoSelect { get; set; }
    public bool NecromancerBandedAutoAttract { get; set; }
    public uint NecromancerBandedAttractSkillId { get; set; } = 2866;
    public int NecromancerAutoLeaveMode { get; set; }
    public int NecromancerAutoLeaveAfterMinutes { get; set; }
    public int NecromancerFsdScenarioIndex { get; set; } = 1;
    public bool UseDetailedMap { get; set; }
    public int NecromancerFsdEndMode { get; set; }
    public bool NecromancerFsdLoopInfinite { get; set; }
    public int NecromancerFsdLoopCount { get; set; } = 1;
    public int NecromancerFsdPotdPotsherdTarget { get; set; }
    public int NecromancerFsdHoHPotsherdTarget { get; set; }
    public int NecromancerFsdEOPotsherdTarget { get; set; }
    public int NecromancerFsdPTPotsherdTarget { get; set; }
    public int NecromancerFsdPotdHoard16170Target { get; set; }
    public int NecromancerFsdPotdHoard16171Target { get; set; }
    public int NecromancerFsdPotdHoard16172Target { get; set; }
    public int NecromancerFsdPotdHoard16173Target { get; set; }
    public int NecromancerFsdHoHHoard23223Target { get; set; }
    public int NecromancerFsdHoHHoard23224Target { get; set; }
    public int NecromancerFsdHoHHoard23225Target { get; set; }
    public int NecromancerFsdEOHoard38945Target { get; set; }
    public int NecromancerFsdEOHoard38946Target { get; set; }
    public int NecromancerFsdEOHoard38947Target { get; set; }
    public int NecromancerFsdPTHoard47104Target { get; set; }
    public int NecromancerFsdPTHoard47105Target { get; set; }
    public int NecromancerFsdPTHoard47106Target { get; set; }

    public void AttachStore(IFsdSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (_store != null && !ReferenceEquals(_store, store))
            throw new InvalidOperationException("FSD settings are already attached to a different store.");
        _store = store;
    }

    public void Refresh()
    {
        (_store ?? throw new InvalidOperationException("FSD settings store is not attached."))
            .Refresh(this);
    }

    public void Save()
    {
        FsdSettingsValidator.ValidateOrThrow(this);
        (_store ?? throw new InvalidOperationException("FSD settings store is not attached."))
            .Save(this);
    }
}
