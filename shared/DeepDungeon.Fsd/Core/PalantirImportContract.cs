using System.Globalization;

namespace DeepDungeon.Fsd.Core;

public static class PalantirImportPolicy
{
    public const uint PilgrimsTraverseTerritoryId = 1283;
    public const float PilgrimsTraverseCoordinateMagnitudeLimit = 1000f;
    public const string CoordinateClassificationAlgorithm = "pt-target-territory-finite-abs-le-1000-v1";
    public const string SqliteSchemaId = "palantir-markers-sqlite-v1";

    private static readonly PalantirSqliteColumn[] ExpectedSqliteColumns =
    [
        new(0, "Id", "TEXT", true, true),
        new(1, "Territory", "INTEGER", true, false),
        new(2, "Type", "INTEGER", true, false),
        new(3, "X", "REAL", true, false),
        new(4, "Y", "REAL", true, false),
        new(5, "Z", "REAL", true, false),
        new(6, "Confirmations", "INTEGER", true, false),
        new(7, "Local", "INTEGER", true, false),
        new(8, "Synced", "INTEGER", true, false)
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

    public static void ValidateSqliteSchema(IReadOnlyList<PalantirSqliteColumn> actualColumns)
    {
        if (actualColumns.Count != ExpectedSqliteColumns.Length)
        {
            throw new InvalidDataException(
                $"Unsupported Palantir Markers schema: found {actualColumns.Count} columns; " +
                $"expected {ExpectedSqliteColumns.Length} for {SqliteSchemaId}.");
        }

        for (int i = 0; i < ExpectedSqliteColumns.Length; i++)
        {
            PalantirSqliteColumn expected = ExpectedSqliteColumns[i];
            PalantirSqliteColumn actual = actualColumns[i];
            if (actual.Ordinal != expected.Ordinal ||
                !string.Equals(actual.Name, expected.Name, StringComparison.Ordinal) ||
                !string.Equals(actual.DeclaredType, expected.DeclaredType, StringComparison.OrdinalIgnoreCase) ||
                actual.NotNull != expected.NotNull ||
                actual.PrimaryKey != expected.PrimaryKey)
            {
                throw new InvalidDataException(
                    $"Unsupported Palantir Markers schema at column {i}: " +
                    $"found {Describe(actual)}; expected {Describe(expected)} for {SqliteSchemaId}.");
            }
        }
    }

    private static string Describe(PalantirSqliteColumn column) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"({column.Ordinal}, {column.Name}, {column.DeclaredType}, notNull={column.NotNull}, primaryKey={column.PrimaryKey})");
}

public readonly record struct PalantirSqliteColumn(
    int Ordinal,
    string Name,
    string DeclaredType,
    bool NotNull,
    bool PrimaryKey);

public sealed class PalantirTerritoryImportReport
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string SourceSchema { get; init; } = PalantirImportPolicy.SqliteSchemaId;
    public string SourceFileName { get; init; } = string.Empty;
    public uint TerritoryId { get; init; }
    public DateTime SavedAtUtc { get; init; }
    public string CoordinateClassificationAlgorithm { get; init; } =
        PalantirImportPolicy.CoordinateClassificationAlgorithm;
    public string CoordinateClassificationScope { get; init; } =
        "Magnitude classification applies only to Pilgrim's Traverse Territory 1283.";
    public int SourceMarkerCount { get; init; }
    public int ValidMarkerCount { get; init; }
    public int QuarantinedMarkerCount { get; init; }
    public SortedDictionary<string, int> QuarantineReasonCounts { get; init; } =
        new(StringComparer.Ordinal);
    public PalantirSourceMarkerRecord[] Records { get; init; } = [];

    public static PalantirTerritoryImportReport Create(
        string sourceFileName,
        uint territoryId,
        IReadOnlyList<PalantirSourceMarkerRecord> sourceRecords,
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

        return new PalantirTerritoryImportReport
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

public sealed class PalantirSourceMarkerRecord
{
    public string Id { get; init; } = string.Empty;
    public int Type { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public int Confirmations { get; init; }
    public bool Local { get; init; }
    public bool Synced { get; init; }
    public string? QuarantineReason { get; init; }
}
