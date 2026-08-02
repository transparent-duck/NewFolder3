using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepDungeon.Fsd.Core;

public enum DetailedMapTerminalState
{
    NoHoard,
    HoardPositive,
    Incomplete,
    Invalid
}

public enum DetailedMapIntuitionState
{
    NotObserved,
    HoardPresent,
    NoHoard,
    Unresolved,
    Invalid
}

public enum DetailedMapTrapScanState
{
    NotAttempted,
    Incomplete,
    Complete
}

public enum DetailedMapRevealSource
{
    None,
    Sight,
    Mazeroot,
    Unknown
}

public sealed class DetailedMapEvidenceBatch
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFloorCount = 32;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public DetailedMapFloorEvidence[] Floors { get; init; } = [];
}

public sealed class DetailedMapFloorEvidence
{
    public string CollectorVersion { get; init; } = string.Empty;
    public string ScenarioKey { get; init; } = string.Empty;
    public string FloorInstanceId { get; init; } = string.Empty;
    public byte Floor { get; init; }
    public uint TerritoryId { get; init; }
    public int ActiveLayoutIndex { get; init; }
    public FloorEvidenceAcquisitionMode AcquisitionMode { get; init; }
    public string? CatalogReleaseUsed { get; init; }
    public DetailedMapRoomBinding[] RoomBindings { get; init; } = [];
    public DetailedMapTerminalObservation Terminal { get; init; } = new();
    public DetailedMapIntuitionObservation Intuition { get; init; } = new();
    public DetailedMapTrapScanObservation TrapScan { get; init; } = new();
    public DetailedMapObservedPosition[] ExactHoards { get; init; } = [];
    public DetailedMapPairEligibility PairEligibility { get; init; } = new();
}

public sealed class DetailedMapRoomBinding
{
    public int RoomIndex { get; init; }
    public uint ConnectionFlags { get; init; }
    public RawWorldPosition RoomCenter { get; init; }
}

public sealed class DetailedMapTerminalObservation
{
    public DetailedMapTerminalState State { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class DetailedMapIntuitionObservation
{
    public DetailedMapIntuitionState State { get; init; }
    public RawWorldPosition? IndicatorPosition { get; init; }
}

public sealed class DetailedMapTrapScanObservation
{
    public DetailedMapTrapScanState State { get; init; }
    public DetailedMapRevealSource RevealSource { get; init; }
    public DetailedMapObservedPosition[] Traps { get; init; } = [];
}

public sealed class DetailedMapObservedPosition
{
    public RawWorldPosition Position { get; init; }
    public int RoomIndex { get; init; }
}

public sealed class DetailedMapPairEligibility
{
    public bool Eligible { get; init; }
    public bool JointScanComplete { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public static class DetailedMapEvidenceContract
{
    // Kept as named aliases for existing callers; validation below is driven by
    // the explicit scenario matrix rather than by a single hardcoded scope.
    public const string PilgrimsTraverse21To30ScenarioKey = "pt-21-30";
    public const uint PilgrimsTraverse21To30TerritoryId = 1283;
    public const uint PilgrimsTraverseDungeonId = 4;
    public const byte FirstCoveredFloor = 21;
    public const byte LastCoveredFloor = 29;
    public const int MaximumRoomCount = 36;
    public const int MaximumPositionCount = 72;
    public const int MaximumCollectorVersionLength = 96;
    public const int MaximumCatalogReleaseLength = 32;
    public const float CoordinateMagnitudeLimit = 1000f;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static DetailedMapEvidenceBatch CreateCanonicalBatch(
        IEnumerable<DetailedMapFloorEvidence> floors)
    {
        ArgumentNullException.ThrowIfNull(floors);
        DetailedMapFloorEvidence[] ordered = floors
            .OrderBy(floor => floor.FloorInstanceId, StringComparer.Ordinal)
            .ToArray();
        var batch = new DetailedMapEvidenceBatch { Floors = ordered };
        Validate(batch);
        return batch;
    }

    public static byte[] SerializeCanonical(DetailedMapEvidenceBatch batch)
    {
        Validate(batch);
        return JsonSerializer.SerializeToUtf8Bytes(batch, JsonOptions);
    }

    public static DetailedMapEvidenceBatch Parse(ReadOnlySpan<byte> utf8Json)
    {
        DetailedMapEvidenceBatch batch = JsonSerializer.Deserialize<DetailedMapEvidenceBatch>(
                utf8Json,
                JsonOptions)
            ?? throw new InvalidDataException("Detailed-map evidence batch is empty.");
        Validate(batch);
        return batch;
    }

    public static string ComputeBatchId(ReadOnlySpan<byte> canonicalUtf8Json) =>
        Convert.ToHexString(SHA256.HashData(canonicalUtf8Json)).ToLowerInvariant();

    public static void Validate(DetailedMapEvidenceBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.SchemaVersion != DetailedMapEvidenceBatch.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported detailed-map evidence schemaVersion {batch.SchemaVersion}.");
        }
        if (batch.Floors is null ||
            batch.Floors.Length == 0 ||
            batch.Floors.Length > DetailedMapEvidenceBatch.MaximumFloorCount)
        {
            throw new InvalidDataException(
                $"Detailed-map evidence must contain 1-{DetailedMapEvidenceBatch.MaximumFloorCount} floors.");
        }

        var floorIds = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < batch.Floors.Length; index++)
        {
            DetailedMapFloorEvidence floor = batch.Floors[index]
                ?? throw new InvalidDataException($"Detailed-map floor {index} is null.");
            ValidateFloor(floor, floorIds);
        }
    }

    private static void ValidateFloor(
        DetailedMapFloorEvidence floor,
        HashSet<string> floorIds)
    {
        if (string.IsNullOrWhiteSpace(floor.CollectorVersion) ||
            floor.CollectorVersion.Length > MaximumCollectorVersionLength)
        {
            throw new InvalidDataException("Detailed-map collectorVersion is invalid.");
        }
        if (!DetailedMapScenarioCatalog.TryGetByKey(
                floor.ScenarioKey,
                out DetailedMapScenarioDefinition? scenario))
        {
            throw new InvalidDataException(
                $"Unsupported detailed-map scenarioKey {floor.ScenarioKey}.");
        }
        if (!IsLowerHex(floor.FloorInstanceId, 32) ||
            !floorIds.Add(floor.FloorInstanceId))
        {
            throw new InvalidDataException(
                $"Detailed-map floorInstanceId {floor.FloorInstanceId} is invalid or duplicated.");
        }
        if (floor.Floor is < 1 or > 255 ||
            floor.Floor < scenario.FirstCoveredFloor ||
            floor.Floor > scenario.LastCoveredFloor ||
            floor.TerritoryId != scenario.TerritoryId)
        {
            throw new InvalidDataException(
                $"Detailed-map floor scope {floor.TerritoryId}/{floor.Floor} is unsupported.");
        }
        if (floor.ActiveLayoutIndex is < 0 or > 255)
            throw new InvalidDataException("Detailed-map activeLayoutIndex is invalid.");
        if (floor.CatalogReleaseUsed is { Length: > MaximumCatalogReleaseLength })
            throw new InvalidDataException("Detailed-map catalogReleaseUsed is too long.");
        if (floor.RoomBindings is null ||
            floor.RoomBindings.Length == 0 ||
            floor.RoomBindings.Length > MaximumRoomCount)
        {
            throw new InvalidDataException(
                $"Detailed-map roomBindings must contain 1-{MaximumRoomCount} rooms.");
        }
        if (floor.Terminal is null ||
            string.IsNullOrWhiteSpace(floor.Terminal.Reason) ||
            floor.Intuition is null ||
            floor.TrapScan is null ||
            floor.PairEligibility is null)
        {
            throw new InvalidDataException("Detailed-map normalized terminal facts are incomplete.");
        }
        var roomIndexes = new HashSet<int>();
        for (int index = 0; index < floor.RoomBindings.Length; index++)
        {
            DetailedMapRoomBinding binding = floor.RoomBindings[index]
                ?? throw new InvalidDataException($"Detailed-map room binding {index} is null.");
            if (binding.RoomIndex is < 0 or >= MaximumRoomCount ||
                !roomIndexes.Add(binding.RoomIndex))
            {
                throw new InvalidDataException(
                    $"Detailed-map room index {binding.RoomIndex} is invalid or duplicated.");
            }
            ValidatePosition(binding.RoomCenter, "room center");
        }

        ValidateObservedPositions(floor.ExactHoards, roomIndexes, "exact hoard");
        ValidateObservedPositions(floor.TrapScan.Traps, roomIndexes, "trap");
        if (floor.Intuition.IndicatorPosition.HasValue)
            ValidatePosition(floor.Intuition.IndicatorPosition.Value, "indicator");

        if (floor.Intuition.State == DetailedMapIntuitionState.HoardPresent)
        {
            if (!floor.Intuition.IndicatorPosition.HasValue ||
                floor.Terminal.State != DetailedMapTerminalState.HoardPositive ||
                floor.ExactHoards.Length != 1 ||
                !RawWorldPosition.CanonicallyEquals(
                    floor.Intuition.IndicatorPosition.Value,
                    floor.ExactHoards[0].Position))
            {
                throw new InvalidDataException(
                    "Hoard-present Intuition is inconsistent with terminal exact-hoard evidence.");
            }
        }
        else if (floor.Intuition.IndicatorPosition.HasValue)
        {
            throw new InvalidDataException(
                "Only hoard-present Intuition may contain an indicator position.");
        }

        if (floor.Intuition.State == DetailedMapIntuitionState.NoHoard &&
            (floor.Terminal.State != DetailedMapTerminalState.NoHoard ||
             floor.ExactHoards.Length != 0))
        {
            throw new InvalidDataException(
                "No-hoard Intuition is inconsistent with terminal exact-hoard evidence.");
        }
        if (floor.Terminal.State == DetailedMapTerminalState.NoHoard &&
            (floor.Intuition.State != DetailedMapIntuitionState.NoHoard ||
             floor.ExactHoards.Length != 0))
        {
            throw new InvalidDataException(
                "No-hoard terminal evidence requires a no-hoard Intuition result.");
        }
        if (floor.Terminal.State == DetailedMapTerminalState.HoardPositive &&
            floor.ExactHoards.Length != 1)
        {
            throw new InvalidDataException(
                "Hoard-positive terminal evidence requires one exact hoard.");
        }
        if (floor.TrapScan.State == DetailedMapTrapScanState.NotAttempted &&
            (floor.TrapScan.RevealSource != DetailedMapRevealSource.None ||
             floor.TrapScan.Traps.Length != 0))
        {
            throw new InvalidDataException(
                "A non-attempted trap scan cannot contain reveal evidence.");
        }
        if (floor.TrapScan.State == DetailedMapTrapScanState.Complete &&
            floor.TrapScan.RevealSource is not
                (DetailedMapRevealSource.Sight or DetailedMapRevealSource.Mazeroot))
        {
            throw new InvalidDataException(
                "A complete trap scan requires an authoritative reveal source.");
        }
        if (floor.PairEligibility.Eligible &&
            (!floor.PairEligibility.JointScanComplete ||
             floor.Terminal.State != DetailedMapTerminalState.HoardPositive ||
             floor.TrapScan.State != DetailedMapTrapScanState.Complete ||
             floor.ExactHoards.Length != 1))
        {
            throw new InvalidDataException("Detailed-map pair eligibility is structurally inconsistent.");
        }
    }

    private static void ValidateObservedPositions(
        DetailedMapObservedPosition[] positions,
        IReadOnlySet<int> roomIndexes,
        string label)
    {
        if (positions is null || positions.Length > MaximumPositionCount)
            throw new InvalidDataException($"Detailed-map {label} count is invalid.");

        for (int index = 0; index < positions.Length; index++)
        {
            DetailedMapObservedPosition position = positions[index]
                ?? throw new InvalidDataException($"Detailed-map {label} {index} is null.");
            if (!roomIndexes.Contains(position.RoomIndex))
            {
                throw new InvalidDataException(
                    $"Detailed-map {label} references unknown room {position.RoomIndex}.");
            }
            ValidatePosition(position.Position, label);
        }
    }

    private static void ValidatePosition(in RawWorldPosition position, string label)
    {
        if (!float.IsFinite(position.X) ||
            !float.IsFinite(position.Y) ||
            !float.IsFinite(position.Z) ||
            MathF.Abs(position.X) > CoordinateMagnitudeLimit ||
            MathF.Abs(position.Y) > CoordinateMagnitudeLimit ||
            MathF.Abs(position.Z) > CoordinateMagnitudeLimit)
        {
            throw new InvalidDataException($"Detailed-map {label} is outside the accepted coordinate range.");
        }
    }

    private static bool IsLowerHex(string value, int length)
    {
        if (value.Length != length)
            return false;
        for (int index = 0; index < value.Length; index++)
        {
            char valueAtIndex = value[index];
            if (valueAtIndex is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }
}
