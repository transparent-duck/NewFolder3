using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud;

namespace NewFolder3;

/// <summary>
/// Explicit online-service and access-gate capabilities for this host build.
/// Public checkouts resolve to <see cref="PublicNoService"/>. Official private
/// builds may stage <c>OfficialBuildProfile.g.cs</c> to replace these values via
/// <see cref="NewFolder3BuildProfile.ProvideOfficialCapabilities"/>.
/// </summary>
internal sealed class NewFolder3BuildCapabilities
{
    public Uri? DetailedMapCatalogEndpoint { get; init; }
    public Uri? CommunityEvidenceEndpoint { get; init; }
    public bool ContributesAnonymousEvidence { get; init; }
    public bool DeleteCatalogsWhenDisabled { get; init; }
    public bool SupportsControlledPtSurvey { get; init; }
    public NewFolder3AccessGateFactory? CreateAccessGate { get; init; }

    public bool HasOnlineCatalogService => DetailedMapCatalogEndpoint != null;
    public bool HasCommunityEvidenceUpload => CommunityEvidenceEndpoint != null;

    public static NewFolder3BuildCapabilities PublicNoService { get; } = new()
    {
        DetailedMapCatalogEndpoint = null,
        CommunityEvidenceEndpoint = null,
        ContributesAnonymousEvidence = false,
        DeleteCatalogsWhenDisabled = false,
        SupportsControlledPtSurvey = false,
        CreateAccessGate = null
    };
}

/// <summary>
/// Build-profile extension boundary. Without an official overlay this is the
/// public no-service profile: no catalog URI, no evidence URI, allow-all gate.
/// </summary>
internal static partial class NewFolder3BuildProfile
{
    internal static NewFolder3BuildCapabilities Capabilities { get; } = ResolveCapabilities();

    internal static INewFolder3AccessGate CreateAccessGate(
        IDalamudPluginInterface pluginInterface,
        IPluginLog pluginLog)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        ArgumentNullException.ThrowIfNull(pluginLog);

        NewFolder3AccessGateFactory? factory = Capabilities.CreateAccessGate;
        if (factory != null)
            return factory(pluginInterface, pluginLog);

        return new NewFolder3AllowAllAccessGate();
    }

    internal static DetailedMapHostOptions CreateDetailedMapHostOptions()
    {
        NewFolder3BuildCapabilities capabilities = Capabilities;
        return new DetailedMapHostOptions(
            capabilities.DetailedMapCatalogEndpoint,
            contributesAnonymousEvidence: capabilities.ContributesAnonymousEvidence,
            deleteCatalogsWhenDisabled: capabilities.DeleteCatalogsWhenDisabled,
            supportsControlledPtSurvey: capabilities.SupportsControlledPtSurvey);
    }

    /// <summary>
    /// Optional official overlay hook. Implemented only by staged
    /// <c>OfficialBuildProfile.g.cs</c>; public builds leave this unimplemented.
    /// </summary>
    static partial void ProvideOfficialCapabilities(
        ref NewFolder3BuildCapabilities? capabilities);

    private static NewFolder3BuildCapabilities ResolveCapabilities()
    {
        NewFolder3BuildCapabilities? official = null;
        ProvideOfficialCapabilities(ref official);
        return official ?? NewFolder3BuildCapabilities.PublicNoService;
    }
}
