namespace DeepDungeon.Fsd.Runtime;

public static class FsdSettingsValidator
{
    public static void ValidateOrThrow(FsdSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!float.IsFinite(settings.NecromancerChestInteractDistance) || settings.NecromancerChestInteractDistance <= 0)
            throw new InvalidOperationException("Chest interaction distance must be finite and greater than zero.");
        if (!float.IsFinite(settings.NecromancerBandedScanRadius) || settings.NecromancerBandedScanRadius <= 0)
            throw new InvalidOperationException("Banded scan radius must be finite and greater than zero.");
        if (!float.IsFinite(settings.NecromancerBandedStandSeconds) || settings.NecromancerBandedStandSeconds <= 0)
            throw new InvalidOperationException("Banded stand duration must be finite and greater than zero.");
        if (settings.RecoveryPotionHpThresholdPercent is < 1 or > 100)
            throw new InvalidOperationException("Recovery potion HP threshold must be between 1 and 100.");
        if (settings.Farming == null || !Enum.IsDefined(settings.Farming.Mode) || settings.Farming.Preferences == null)
            throw new InvalidOperationException("Farming configuration is invalid.");
        foreach (var preferences in settings.Farming.Preferences.Values)
            if (preferences == null || preferences.Cycles < 1)
                throw new InvalidOperationException("Farming cycle count is invalid.");
        if (settings.NecromancerFsdLoopCount < 1)
            throw new InvalidOperationException("FSD loop count must be at least one.");
    }
}
