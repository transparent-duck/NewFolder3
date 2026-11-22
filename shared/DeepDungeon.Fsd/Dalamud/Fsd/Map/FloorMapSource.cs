namespace DeepDungeon.Fsd.Dalamud.Map;

internal interface ILiveFloorMaps
{
    string Status { get; }
    string ExportStatus { get; }
    bool Failed { get; }
    bool Exporting { get; }
    void Initialize(string configDirectory);
    void Dispose();
    void Observe();
    bool Ensure(FloorMapKey key, byte floor);
    bool RejectMap(FloorMapKey key);
    string MapPath(FloorMapKey key);
    object Configure(Dictionary<string, string> options);
    void Export();
    void UpdateExportStatus();
}

internal static class FloorMapSource
{
    internal static ILiveFloorMaps? Live { get; } =
#if FSD_LIVE_TERRAIN
        new LiveTerrainCapture();
#else
        null;
#endif

    internal static bool UsesBuiltIn { get; private set; } = Live == null;
    internal static ILiveFloorMaps? ActiveLive => UsesBuiltIn ? null : Live;

    internal static void Select(bool builtIn)
    {
        bool usesBuiltIn = Live == null || builtIn;
        if (UsesBuiltIn == usesBuiltIn) return;
        UsesBuiltIn = usesBuiltIn;
        FloorMap.Reset();
    }

    internal static Stream OpenBuiltIn(FloorMapKey key) =>
        typeof(FloorMap).Assembly.GetManifestResourceStream($"Fsd.FloorMaps.{key}.map.gz")
        ?? throw new InvalidDataException("缺少此版型的內建底圖。");
}
