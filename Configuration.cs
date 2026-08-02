using Dalamud.Configuration;
using Dalamud.Plugin;
using DeepDungeon.Fsd.Runtime;
using System.Security.Cryptography;

namespace NewFolder3;

[Serializable]
public sealed class Configuration : IPluginConfiguration, IFsdSettingsStore
{
    [NonSerialized]
    private IDalamudPluginInterface? _pluginInterface;

    public int Version { get; set; } = 1;
    public FsdSettings Fsd { get; set; } = new();
    public string CommunityEvidenceInstallationToken { get; set; } = string.Empty;

    public void Initialize(IDalamudPluginInterface pluginInterface)
        => _pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));

    public FsdSettings Load() => Fsd ?? throw new InvalidOperationException("Standalone FSD settings are missing.");

    public void Refresh(FsdSettings settings)
    {
        if (!ReferenceEquals(settings, Fsd))
            throw new InvalidOperationException("Standalone FSD settings store received an unknown settings instance.");
    }

    public void Save(FsdSettings settings)
    {
        if (!ReferenceEquals(settings, Fsd))
            throw new InvalidOperationException("Standalone FSD settings store received an unknown settings instance.");
        (_pluginInterface ?? throw new InvalidOperationException("Standalone configuration is not initialized."))
            .SavePluginConfig(this);
    }

    public string GetOrCreateCommunityEvidenceInstallationToken()
    {
        if (IsLowerHex(CommunityEvidenceInstallationToken, 32))
            return CommunityEvidenceInstallationToken;

        CommunityEvidenceInstallationToken =
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        (_pluginInterface ?? throw new InvalidOperationException("Standalone configuration is not initialized."))
            .SavePluginConfig(this);
        return CommunityEvidenceInstallationToken;
    }

    private static bool IsLowerHex(string value, int expectedLength)
    {
        if (value.Length != expectedLength)
            return false;
        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }
}
