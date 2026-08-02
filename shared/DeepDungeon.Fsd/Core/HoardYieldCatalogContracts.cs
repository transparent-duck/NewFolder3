using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepDungeon.Fsd.Core;

public sealed class HoardYieldCatalog
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ScenarioKey { get; init; } = string.Empty;
    public uint TerritoryId { get; init; }
    public int[] Floors { get; init; } = [];
    public double FiniteCandidatePrior { get; init; }
    public double ExactFloorPriorStrength { get; init; }
    public HoardYieldFloorEstimate[] FloorEstimates { get; init; } = [];
    public HoardYieldRoom[] Rooms { get; init; } = [];

    [JsonIgnore]
    private Dictionary<(int LayoutIndex, int RoomIndex), HoardYieldRoom>? _roomsByKey;

    public void WarmRoomLookup() =>
        _roomsByKey ??= Rooms.ToDictionary(
            room => (room.LayoutIndex, room.RoomIndex),
            room => room);

    public bool TryGetRoom(
        int layoutIndex,
        int roomIndex,
        out HoardYieldRoom room)
    {
        WarmRoomLookup();
        return _roomsByKey!.TryGetValue((layoutIndex, roomIndex), out room!);
    }
}

public sealed class HoardYieldFloorEstimate
{
    public byte Floor { get; init; }
    public int EligibleExposureCount { get; init; }
    public int HoardPositiveCount { get; init; }
    public double EstimatedHoardProbability { get; init; }
}

public sealed class HoardYieldRoom
{
    public int LayoutIndex { get; init; }
    public int RoomIndex { get; init; }
    public HoardYieldCandidate[] Candidates { get; init; } = [];
}

public sealed class HoardYieldCandidate
{
    public RawWorldPosition Position { get; init; }
    public int FloorsetHoardCount { get; init; }
    public int EligibleExposureCount { get; init; }
    public double FloorsetPosteriorWeight { get; init; }
    public HoardYieldCandidateFloor[] Floors { get; init; } = [];
}

public sealed class HoardYieldCandidateFloor
{
    public byte Floor { get; init; }
    public int ExactHoardCount { get; init; }
    public int EligibleExposureCount { get; init; }
    public double PosteriorWeight { get; init; }
}

public static class HoardYieldCatalogContract
{
    public const string FileName = "hoard-yield-catalog.json";
    private const double WeightTolerance = 1e-8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] SerializeCanonical(HoardYieldCatalog catalog)
    {
        Validate(catalog);
        return JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOptions);
    }

    public static HoardYieldCatalog Parse(ReadOnlySpan<byte> utf8Json)
    {
        HoardYieldCatalog catalog = JsonSerializer.Deserialize<HoardYieldCatalog>(
                utf8Json,
                JsonOptions)
            ?? throw new InvalidDataException("Hoard-yield catalog is empty.");
        Validate(catalog);
        return catalog;
    }

    public static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static void Validate(HoardYieldCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.SchemaVersion != HoardYieldCatalog.CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported hoard-yield catalog schemaVersion.");
        if (!DetailedMapScenarioCatalog.TryGetByKey(
                catalog.ScenarioKey,
                out DetailedMapScenarioDefinition? scenario) ||
            catalog.TerritoryId != scenario.TerritoryId ||
            catalog.Floors is null ||
            !catalog.Floors.SequenceEqual(scenario.Floors))
        {
            throw new InvalidDataException("Hoard-yield catalog scope is invalid.");
        }
        if (!IsFinitePositive(catalog.FiniteCandidatePrior) ||
            !IsFinitePositive(catalog.ExactFloorPriorStrength))
        {
            throw new InvalidDataException("Hoard-yield prior parameters are invalid.");
        }
        if (catalog.FloorEstimates is null ||
            catalog.FloorEstimates.Length != catalog.Floors.Length)
        {
            throw new InvalidDataException("Hoard-yield floor estimates are incomplete.");
        }
        for (int index = 0; index < catalog.FloorEstimates.Length; index++)
        {
            HoardYieldFloorEstimate estimate = catalog.FloorEstimates[index]
                ?? throw new InvalidDataException("Hoard-yield floor estimate is null.");
            if (estimate.Floor != catalog.Floors[index] ||
                estimate.EligibleExposureCount < 0 ||
                estimate.HoardPositiveCount < 0 ||
                estimate.HoardPositiveCount > estimate.EligibleExposureCount ||
                !IsProbability(estimate.EstimatedHoardProbability))
            {
                throw new InvalidDataException("Hoard-yield floor estimate is invalid.");
            }
        }

        if (catalog.Rooms is null || catalog.Rooms.Length == 0)
            throw new InvalidDataException("Hoard-yield catalog contains no rooms.");
        var roomKeys = new HashSet<(int Layout, int Room)>();
        foreach (IGrouping<int, HoardYieldRoom> layout in catalog.Rooms.GroupBy(room => room.LayoutIndex))
        {
            var layoutCandidates = new List<HoardYieldCandidate>();
            foreach (HoardYieldRoom room in layout)
            {
                if (room == null ||
                    room.LayoutIndex is < 0 or > 255 ||
                    room.RoomIndex is < 0 or >= DetailedMapEvidenceContract.MaximumRoomCount ||
                    !roomKeys.Add((room.LayoutIndex, room.RoomIndex)) ||
                    room.Candidates is null ||
                    room.Candidates.Length == 0)
                {
                    throw new InvalidDataException("Hoard-yield room is invalid or duplicated.");
                }
                foreach (HoardYieldCandidate candidate in room.Candidates)
                {
                    ValidateCandidate(candidate, catalog.Floors);
                    layoutCandidates.Add(candidate);
                }
            }
            if (Math.Abs(layoutCandidates.Sum(value => value.FloorsetPosteriorWeight) - 1d) > WeightTolerance)
                throw new InvalidDataException("Hoard-yield floorset weights do not sum to one per layout.");
            for (int floorIndex = 0; floorIndex < catalog.Floors.Length; floorIndex++)
            {
                double sum = layoutCandidates.Sum(value => value.Floors[floorIndex].PosteriorWeight);
                if (Math.Abs(sum - 1d) > WeightTolerance)
                    throw new InvalidDataException("Hoard-yield exact-floor weights do not sum to one per layout.");
            }
        }
    }

    public static void ValidateCompatibility(
        DetailedMapCatalog detailedMap,
        HoardYieldCatalog yield)
    {
        ArgumentNullException.ThrowIfNull(detailedMap);
        Validate(yield);
        if (!string.Equals(detailedMap.ScenarioKey, yield.ScenarioKey, StringComparison.Ordinal) ||
            detailedMap.TerritoryId != yield.TerritoryId ||
            !detailedMap.Floors.SequenceEqual(yield.Floors))
        {
            throw new InvalidDataException("Hoard-yield catalog does not match its detailed-map release.");
        }
        foreach (DetailedMapCatalogRoom room in detailedMap.Rooms)
        {
            HoardYieldRoom yieldRoom = yield.Rooms.SingleOrDefault(value =>
                    value.LayoutIndex == room.LayoutIndex && value.RoomIndex == room.RoomIndex)
                ?? throw new InvalidDataException("Hoard-yield catalog is missing a detailed-map room.");
            if (yieldRoom.Candidates.Length != room.Candidates.Length)
                throw new InvalidDataException("Hoard-yield candidate universe is incomplete.");
            foreach (DetailedMapCatalogCandidate candidate in room.Candidates)
            {
                int matches = yieldRoom.Candidates.Count(value =>
                    RawWorldPosition.CanonicallyEquals(value.Position, candidate.Position));
                if (matches != 1)
                    throw new InvalidDataException("Hoard-yield candidate universe does not match detailed map.");
            }
        }
        if (yield.Rooms.Length != detailedMap.Rooms.Length)
            throw new InvalidDataException("Hoard-yield catalog contains unexpected rooms.");
    }

    private static void ValidateCandidate(HoardYieldCandidate candidate, IReadOnlyList<int> floors)
    {
        if (candidate == null ||
            !IsPositionValid(candidate.Position) ||
            candidate.FloorsetHoardCount < 0 ||
            candidate.EligibleExposureCount < candidate.FloorsetHoardCount ||
            !IsFinitePositive(candidate.FloorsetPosteriorWeight) ||
            candidate.Floors is null ||
            candidate.Floors.Length != floors.Count)
        {
            throw new InvalidDataException("Hoard-yield candidate is invalid.");
        }
        int exactCount = 0;
        for (int index = 0; index < candidate.Floors.Length; index++)
        {
            HoardYieldCandidateFloor floor = candidate.Floors[index]
                ?? throw new InvalidDataException("Hoard-yield candidate floor is null.");
            if (floor.Floor != floors[index] ||
                floor.ExactHoardCount < 0 ||
                floor.EligibleExposureCount < floor.ExactHoardCount ||
                !IsFinitePositive(floor.PosteriorWeight))
            {
                throw new InvalidDataException("Hoard-yield candidate floor is invalid.");
            }
            exactCount += floor.ExactHoardCount;
        }
        if (exactCount != candidate.FloorsetHoardCount)
            throw new InvalidDataException("Hoard-yield candidate raw counts are inconsistent.");
    }

    private static bool IsPositionValid(in RawWorldPosition position) =>
        float.IsFinite(position.X) &&
        float.IsFinite(position.Y) &&
        float.IsFinite(position.Z) &&
        MathF.Abs(position.X) <= DetailedMapEvidenceContract.CoordinateMagnitudeLimit &&
        MathF.Abs(position.Y) <= DetailedMapEvidenceContract.CoordinateMagnitudeLimit &&
        MathF.Abs(position.Z) <= DetailedMapEvidenceContract.CoordinateMagnitudeLimit;

    private static bool IsFinitePositive(double value) =>
        double.IsFinite(value) && value > 0d;

    private static bool IsProbability(double value) =>
        double.IsFinite(value) && value >= 0d && value <= 1d;
}
