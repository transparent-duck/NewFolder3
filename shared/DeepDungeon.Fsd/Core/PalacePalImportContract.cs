using System.Globalization;

namespace DeepDungeon.Fsd.Core;

public static class PalacePalImportPolicy
{
    public const uint PilgrimsTraverseTerritoryId = 1283;
    public const float PilgrimsTraverseCoordinateMagnitudeLimit = 1000f;
    public const string CoordinateClassificationAlgorithm = "pt-target-territory-finite-abs-le-1000-v1";
    public const string SqliteSchemaId = "palacepal-locations-sqlite-v1";

    private static readonly PalacePalSqliteColumn[] ExpectedSqliteColumns =
    [
        new(0, "LocalId", "INTEGER", true, true),
        new(1, "TerritoryType", "INTEGER", true, false),
        new(2, "Type", "INTEGER", true, false),
        new(3, "X", "REAL", true, false),
        new(4, "Y", "REAL", true, false),
        new(5, "Z", "REAL", true, false),
        new(6, "Seen", "INTEGER", true, false),
        new(7, "Source", "INTEGER", true, false),
        new(8, "SinceVersion", "TEXT", true, false)
    ];

    public static string? GetQuarantineReason(uint territoryId, float x, float y, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            return "nonFiniteCoordinate";

        if (territoryId == PilgrimsTraverseTerritoryId &&
            (MathF.Abs(x) > PilgrimsTraverseCoordinateMagnitudeLimit ||
             MathF.Abs(y) > PilgrimsTraverseCoordinateMagnitudeLimit ||
             MathF.Abs(z) > PilgrimsTraverseCoordinateMagnitudeLimit))
        {
            return "coordinateMagnitudeExceeds1000";
        }

        return null;
    }

    public static void ValidateSqliteSchema(IReadOnlyList<PalacePalSqliteColumn> actualColumns)
    {
        if (actualColumns.Count != ExpectedSqliteColumns.Length)
        {
            throw new InvalidDataException(
                $"Unsupported PalacePal Locations schema: found {actualColumns.Count} columns; " +
                $"expected {ExpectedSqliteColumns.Length} for {SqliteSchemaId}.");
        }

        for (int i = 0; i < ExpectedSqliteColumns.Length; i++)
        {
            PalacePalSqliteColumn expected = ExpectedSqliteColumns[i];
            PalacePalSqliteColumn actual = actualColumns[i];
            if (actual.Ordinal != expected.Ordinal ||
                !string.Equals(actual.Name, expected.Name, StringComparison.Ordinal) ||
                !string.Equals(actual.DeclaredType, expected.DeclaredType, StringComparison.OrdinalIgnoreCase) ||
                actual.NotNull != expected.NotNull ||
                actual.PrimaryKey != expected.PrimaryKey)
            {
                throw new InvalidDataException(
                    $"Unsupported PalacePal Locations schema at column {i}: " +
                    $"found {Describe(actual)}; expected {Describe(expected)} for {SqliteSchemaId}.");
            }
        }
    }

    private static string Describe(PalacePalSqliteColumn column) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"({column.Ordinal}, {column.Name}, {column.DeclaredType}, notNull={column.NotNull}, primaryKey={column.PrimaryKey})");
}

public readonly record struct PalacePalSqliteColumn(
    int Ordinal,
    string Name,
    string DeclaredType,
    bool NotNull,
    bool PrimaryKey);

public sealed class PalacePalTerritoryImportReport
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string SourceSchema { get; init; } = PalacePalImportPolicy.SqliteSchemaId;
    public string SourceFileName { get; init; } = string.Empty;
    public uint TerritoryId { get; init; }
    public DateTime SavedAtUtc { get; init; }
    public string CoordinateClassificationAlgorithm { get; init; } =
        PalacePalImportPolicy.CoordinateClassificationAlgorithm;
    public string CoordinateClassificationScope { get; init; } =
        "Magnitude classification applies only to Pilgrim's Traverse Territory 1283.";
    public int SourceMarkerCount { get; init; }
    public int ValidMarkerCount { get; init; }
    public int QuarantinedMarkerCount { get; init; }
    public SortedDictionary<string, int> QuarantineReasonCounts { get; init; } =
        new(StringComparer.Ordinal);
    public PalacePalSourceMarkerRecord[] Records { get; init; } = [];

    public static PalacePalTerritoryImportReport Create(
        string sourceFileName,
        uint territoryId,
        IReadOnlyList<PalacePalSourceMarkerRecord> sourceRecords,
        DateTime savedAtUtc)
    {
        var records = sourceRecords.ToArray();
        var reasonCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int quarantined = 0;
        for (int i = 0; i < records.Length; i++)
        {
            string? reason = records[i].QuarantineReason;
            if (reason == null)
                continue;

            quarantined++;
            reasonCounts[reason] = reasonCounts.GetValueOrDefault(reason) + 1;
        }

        return new PalacePalTerritoryImportReport
        {
            SourceFileName = sourceFileName,
            TerritoryId = territoryId,
            SavedAtUtc = savedAtUtc,
            SourceMarkerCount = records.Length,
            ValidMarkerCount = records.Length - quarantined,
            QuarantinedMarkerCount = quarantined,
            QuarantineReasonCounts = reasonCounts,
            Records = records
        };
    }
}

public sealed class PalacePalSourceMarkerRecord
{
    public int LocalId { get; init; }
    public int Type { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public bool Seen { get; init; }
    public int Source { get; init; }
    public string SinceVersion { get; init; } = string.Empty;
    public string? QuarantineReason { get; init; }
}
