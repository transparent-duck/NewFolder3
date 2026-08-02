namespace DeepDungeon.Fsd.Core;

public static class DetailedMapCatalogTrust
{
    public const string KeyId = "catalog-p256-v1";

    private const string SubjectPublicKeyInfoBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEhBosvNVpQ5c/vqFH1NKIca7RemC8wK0YYDxhHrFO+jGwXv+9Uf9/dH91rMNmtVJzFehf3fo1JTYG36LUiI273Q==";

    private static readonly byte[] SubjectPublicKeyInfo =
        Convert.FromBase64String(SubjectPublicKeyInfoBase64);

    public static bool Verify(
        ReadOnlySpan<byte> canonicalCatalog,
        DetailedMapCatalogSignature signature) =>
        string.Equals(signature.KeyId, KeyId, StringComparison.Ordinal) &&
        DetailedMapCatalogContract.Verify(
            canonicalCatalog,
            signature,
            SubjectPublicKeyInfo);

    public static byte[] ExportSubjectPublicKeyInfo() =>
        [.. SubjectPublicKeyInfo];
}
