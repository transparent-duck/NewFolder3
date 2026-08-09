namespace DeepDungeon.Fsd.Dalamud;

public sealed class DetailedMapHostOptions
{
    public const string NoOnlineCatalogServiceMessage =
        "此構建未配置詳細地圖服務。";

    public DetailedMapHostOptions(
        Uri? catalogBaseUri,
        bool contributesAnonymousEvidence,
        bool deleteCatalogsWhenDisabled,
        bool supportsControlledPtSurvey)
    {
        if (catalogBaseUri != null)
        {
            if (!catalogBaseUri.IsAbsoluteUri ||
                !string.Equals(
                    catalogBaseUri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "The detailed-map catalog endpoint must be an absolute HTTPS URI.",
                    nameof(catalogBaseUri));
            }

            CatalogBaseUri = catalogBaseUri.AbsoluteUri.EndsWith(
                "/",
                StringComparison.Ordinal)
                ? catalogBaseUri
                : new Uri($"{catalogBaseUri.AbsoluteUri}/", UriKind.Absolute);
        }
        else
        {
            CatalogBaseUri = null;
            if (contributesAnonymousEvidence)
            {
                throw new ArgumentException(
                    "Anonymous detailed-map evidence contribution requires an online catalog service.",
                    nameof(contributesAnonymousEvidence));
            }
        }

        ContributesAnonymousEvidence = contributesAnonymousEvidence;
        DeleteCatalogsWhenDisabled = deleteCatalogsWhenDisabled;
        SupportsControlledPtSurvey = supportsControlledPtSurvey;
    }

    /// <summary>
    /// Absolute HTTPS catalog base URI, or <c>null</c> when this host build has no
    /// online detailed-map catalog service.
    /// </summary>
    public Uri? CatalogBaseUri { get; }

    public bool HasOnlineCatalogService => CatalogBaseUri != null;

    public bool ContributesAnonymousEvidence { get; }
    public bool DeleteCatalogsWhenDisabled { get; }
    public bool SupportsControlledPtSurvey { get; }
}
