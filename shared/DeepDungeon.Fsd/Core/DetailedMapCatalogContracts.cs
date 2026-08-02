using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepDungeon.Fsd.Core;

public enum DetailedMapSuccessorState
{
    Unknown,
    ObservedUnique,
    Conflict
}

public sealed class DetailedMapCatalog
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ScenarioKey { get; init; } = string.Empty;
    public string ReleaseId { get; init; } = string.Empty;
    public string ModelSha256 { get; init; } = string.Empty;
    public string? HoardYieldSha256 { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public uint TerritoryId { get; init; }
    public int[] Floors { get; init; } = [];
    public DetailedMapCatalogFloorSample[] FloorSamples { get; init; } = [];
    public DetailedMapCatalogRoom[] Rooms { get; init; } = [];

    [JsonIgnore]
    private Dictionary<(int LayoutIndex, int RoomIndex), DetailedMapCatalogRoom>? _roomsByKey;

    public void WarmRoomLookup()
    {
        _roomsByKey ??= Rooms.ToDictionary(
            room => (room.LayoutIndex, room.RoomIndex),
            room => room);
    }

    public bool TryGetRoom(
        int layoutIndex,
        int roomIndex,
        out DetailedMapCatalogRoom room)
    {
        WarmRoomLookup();
        return _roomsByKey!.TryGetValue((layoutIndex, roomIndex), out room!);
    }

    public bool TryGetFloorIndex(byte floor, out int floorIndex)
    {
        for (int index = 0; index < Floors.Length; index++)
        {
            if (Floors[index] != floor)
                continue;

            floorIndex = index;
            return true;
        }

        floorIndex = -1;
        return false;
    }
}

public sealed class DetailedMapCatalogFloorSample
{
    public byte Floor { get; init; }
    public int ObservationCount { get; init; }
    public int HoardPositiveCount { get; init; }
    public int NoHoardCount { get; init; }
}

public sealed class DetailedMapCatalogRoom
{
    public int LayoutIndex { get; init; }
    public int RoomIndex { get; init; }
    public DetailedMapCatalogCandidate[] Candidates { get; init; } = [];
}

public sealed class DetailedMapCatalogCandidate
{
    public RawWorldPosition Position { get; init; }
    public DetailedMapCatalogSuccessor Successor { get; init; } = new();
    public int[] HoardCountsByFloor { get; init; } = [];
}

public sealed class DetailedMapCatalogSuccessor
{
    public DetailedMapSuccessorState State { get; init; }
    public RawWorldPosition? Target { get; init; }
    public int ObservationCount { get; init; }
}

public sealed class DetailedMapCatalogSignature
{
    public const int CurrentSchemaVersion = 1;
    public const string EcdsaP256Sha256 = "ecdsa-p256-sha256";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Algorithm { get; init; } = EcdsaP256Sha256;
    public string KeyId { get; init; } = string.Empty;
    public string CatalogSha256 { get; init; } = string.Empty;
    public string Signature { get; init; } = string.Empty;
}

public sealed class DetailedMapCatalogLatest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ScenarioKey { get; init; } = string.Empty;
    public string ReleaseId { get; init; } = string.Empty;
    public string CatalogPath { get; init; } = string.Empty;
    public string SignaturePath { get; init; } = string.Empty;
    public string CatalogSha256 { get; init; } = string.Empty;
    public string ModelSha256 { get; init; } = string.Empty;
}

public static class DetailedMapCatalogContract
{
    public const int MaximumCatalogRoomCount = 64;
    public const int MaximumRoomIndexExclusive =
        DetailedMapEvidenceContract.MaximumRoomCount;
    public const int MaximumCandidatesPerRoom = 72;
    public const string SignatureFileName = "catalog.sig.json";
    public const string CatalogFileName = "catalog.json";
    public const string LatestFileName = "latest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static byte[] SerializeCanonical(DetailedMapCatalog catalog)
    {
        Validate(catalog);
        return JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOptions);
    }

    public static DetailedMapCatalog Parse(ReadOnlySpan<byte> utf8Json)
    {
        DetailedMapCatalog catalog = JsonSerializer.Deserialize<DetailedMapCatalog>(
                utf8Json,
                JsonOptions)
            ?? throw new InvalidDataException("Detailed-map catalog is empty.");
        Validate(catalog);
        return catalog;
    }

    public static byte[] SerializeSignature(DetailedMapCatalogSignature signature)
    {
        ValidateSignature(signature);
        return JsonSerializer.SerializeToUtf8Bytes(signature, JsonOptions);
    }

    public static DetailedMapCatalogSignature ParseSignature(ReadOnlySpan<byte> utf8Json)
    {
        DetailedMapCatalogSignature signature =
            JsonSerializer.Deserialize<DetailedMapCatalogSignature>(utf8Json, JsonOptions)
            ?? throw new InvalidDataException("Detailed-map catalog signature is empty.");
        ValidateSignature(signature);
        return signature;
    }

    public static byte[] SerializeLatest(DetailedMapCatalogLatest latest)
    {
        ValidateLatest(latest);
        return JsonSerializer.SerializeToUtf8Bytes(latest, JsonOptions);
    }

    public static DetailedMapCatalogLatest ParseLatest(ReadOnlySpan<byte> utf8Json)
    {
        DetailedMapCatalogLatest latest =
            JsonSerializer.Deserialize<DetailedMapCatalogLatest>(utf8Json, JsonOptions)
            ?? throw new InvalidDataException("Detailed-map latest pointer is empty.");
        ValidateLatest(latest);
        return latest;
    }

    public static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string ComputeModelSha256(DetailedMapCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var model = new DetailedMapCatalogModel
        {
            ScenarioKey = catalog.ScenarioKey,
            DisplayName = catalog.DisplayName,
            HoardYieldSha256 = catalog.HoardYieldSha256,
            TerritoryId = catalog.TerritoryId,
            Floors = catalog.Floors,
            FloorSamples = catalog.FloorSamples,
            Rooms = catalog.Rooms
        };
        return ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(model, JsonOptions));
    }

    public static DetailedMapCatalogSignature Sign(
        ReadOnlySpan<byte> canonicalCatalog,
        string keyId,
        ReadOnlySpan<byte> pkcs8PrivateKey)
    {
        if (string.IsNullOrWhiteSpace(keyId) || keyId.Length > 64)
            throw new InvalidDataException("Detailed-map signing keyId is invalid.");

        using ECDsa signer = ECDsa.Create();
        signer.ImportPkcs8PrivateKey(pkcs8PrivateKey, out int bytesRead);
        if (bytesRead != pkcs8PrivateKey.Length ||
            signer.KeySize != 256)
        {
            throw new InvalidDataException("Detailed-map signing key must be one P-256 PKCS#8 key.");
        }

        byte[] signature = signer.SignData(
            canonicalCatalog,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        return new DetailedMapCatalogSignature
        {
            KeyId = keyId,
            CatalogSha256 = ComputeSha256(canonicalCatalog),
            Signature = Convert.ToBase64String(signature)
        };
    }

    public static bool Verify(
        ReadOnlySpan<byte> canonicalCatalog,
        DetailedMapCatalogSignature signature,
        ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        ValidateSignature(signature);
        if (!string.Equals(
                ComputeSha256(canonicalCatalog),
                signature.CatalogSha256,
                StringComparison.Ordinal))
        {
            return false;
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature.Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        using ECDsa verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out int bytesRead);
        if (bytesRead != subjectPublicKeyInfo.Length || verifier.KeySize != 256)
            return false;
        return verifier.VerifyData(
            canonicalCatalog,
            signatureBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
    }

    public static void Validate(DetailedMapCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.SchemaVersion != DetailedMapCatalog.CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported detailed-map catalog schemaVersion.");
        ValidateScope(
            catalog.ScenarioKey,
            catalog.TerritoryId,
            catalog.Floors,
            catalog.ReleaseId);
        if (!IsLowerHex(catalog.ModelSha256, 64) ||
            !string.Equals(
                catalog.ModelSha256,
                ComputeModelSha256(catalog),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Detailed-map catalog modelSha256 is invalid.");
        }
        if (catalog.HoardYieldSha256 != null &&
            !IsLowerHex(catalog.HoardYieldSha256, 64))
        {
            throw new InvalidDataException("Detailed-map hoardYieldSha256 is invalid.");
        }
        if (string.IsNullOrWhiteSpace(catalog.DisplayName) ||
            catalog.DisplayName.Length > 96)
        {
            throw new InvalidDataException("Detailed-map catalog displayName is invalid.");
        }
        if (catalog.FloorSamples is null ||
            catalog.FloorSamples.Length != catalog.Floors.Length)
        {
            throw new InvalidDataException("Detailed-map catalog floorSamples are incomplete.");
        }
        if (catalog.Rooms is null ||
            catalog.Rooms.Length == 0 ||
            catalog.Rooms.Length > MaximumCatalogRoomCount)
        {
            throw new InvalidDataException("Detailed-map catalog room count is invalid.");
        }

        for (int index = 0; index < catalog.FloorSamples.Length; index++)
        {
            DetailedMapCatalogFloorSample sample = catalog.FloorSamples[index]
                ?? throw new InvalidDataException($"Detailed-map floor sample {index} is null.");
            if (sample.Floor != catalog.Floors[index] ||
                sample.ObservationCount < 0 ||
                sample.HoardPositiveCount < 0 ||
                sample.NoHoardCount < 0 ||
                sample.ObservationCount != sample.HoardPositiveCount + sample.NoHoardCount)
            {
                throw new InvalidDataException(
                    $"Detailed-map floor sample {sample.Floor} is inconsistent.");
            }
        }

        var roomKeys = new HashSet<(int LayoutIndex, int RoomIndex)>();
        var observedHoardCounts = new int[catalog.Floors.Length];
        foreach (DetailedMapCatalogRoom room in catalog.Rooms)
        {
            if (room == null ||
                room.LayoutIndex is < 0 or > 255 ||
                room.RoomIndex is < 0 or >= MaximumRoomIndexExclusive ||
                !roomKeys.Add((room.LayoutIndex, room.RoomIndex)) ||
                room.Candidates is null ||
                room.Candidates.Length == 0 ||
                room.Candidates.Length > MaximumCandidatesPerRoom)
            {
                throw new InvalidDataException("Detailed-map catalog room is invalid or duplicated.");
            }

            for (int candidateIndex = 0;
                 candidateIndex < room.Candidates.Length;
                 candidateIndex++)
            {
                DetailedMapCatalogCandidate candidate = room.Candidates[candidateIndex]
                    ?? throw new InvalidDataException("Detailed-map catalog candidate is null.");
                ValidatePosition(candidate.Position, "candidate");
                for (int previousIndex = 0; previousIndex < candidateIndex; previousIndex++)
                {
                    if (RawWorldPosition.CanonicallyEquals(
                            room.Candidates[previousIndex].Position,
                            candidate.Position))
                    {
                        throw new InvalidDataException(
                            "Detailed-map catalog contains duplicate canonical candidates.");
                    }
                }

                if (candidate.HoardCountsByFloor is null ||
                    candidate.HoardCountsByFloor.Length != catalog.Floors.Length ||
                    candidate.HoardCountsByFloor.Any(value => value < 0))
                {
                    throw new InvalidDataException(
                        "Detailed-map candidate hoardCountsByFloor is invalid.");
                }
                for (int floorIndex = 0;
                     floorIndex < candidate.HoardCountsByFloor.Length;
                     floorIndex++)
                {
                    observedHoardCounts[floorIndex] +=
                        candidate.HoardCountsByFloor[floorIndex];
                }

                DetailedMapCatalogSuccessor successor = candidate.Successor
                    ?? throw new InvalidDataException("Detailed-map candidate successor is null.");
                if (successor.ObservationCount < 0)
                    throw new InvalidDataException("Detailed-map successor count cannot be negative.");
                switch (successor.State)
                {
                    case DetailedMapSuccessorState.Unknown:
                        if (successor.Target.HasValue ||
                            successor.ObservationCount != 0)
                        {
                            throw new InvalidDataException(
                                "Unknown detailed-map successor cannot contain evidence.");
                        }
                        break;
                    case DetailedMapSuccessorState.ObservedUnique:
                        if (!successor.Target.HasValue ||
                            successor.ObservationCount <= 0 ||
                            FindUniqueCandidate(room.Candidates, successor.Target.Value) < 0 ||
                            RawWorldPosition.CanonicallyEquals(
                                candidate.Position,
                                successor.Target.Value))
                        {
                            throw new InvalidDataException(
                                "Observed detailed-map successor is invalid.");
                        }
                        break;
                    case DetailedMapSuccessorState.Conflict:
                        if (successor.Target.HasValue ||
                            successor.ObservationCount <= 1)
                        {
                            throw new InvalidDataException(
                                "Conflicted detailed-map successor is invalid.");
                        }
                        break;
                    default:
                        throw new InvalidDataException("Unsupported detailed-map successor state.");
                }
            }
        }

        for (int floorIndex = 0; floorIndex < observedHoardCounts.Length; floorIndex++)
        {
            if (observedHoardCounts[floorIndex] !=
                catalog.FloorSamples[floorIndex].HoardPositiveCount)
            {
                throw new InvalidDataException(
                    $"Detailed-map floor {catalog.Floors[floorIndex]} hoard counts are inconsistent.");
            }
        }
    }

    public static void ValidateSignature(DetailedMapCatalogSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.SchemaVersion != DetailedMapCatalogSignature.CurrentSchemaVersion ||
            !string.Equals(
                signature.Algorithm,
                DetailedMapCatalogSignature.EcdsaP256Sha256,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(signature.KeyId) ||
            signature.KeyId.Length > 64 ||
            !IsLowerHex(signature.CatalogSha256, 64) ||
            string.IsNullOrWhiteSpace(signature.Signature))
        {
            throw new InvalidDataException("Detailed-map catalog signature envelope is invalid.");
        }
        try
        {
            _ = Convert.FromBase64String(signature.Signature);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException(
                "Detailed-map catalog signature is not valid Base64.",
                error);
        }
    }

    public static void ValidateLatest(DetailedMapCatalogLatest latest)
    {
        ArgumentNullException.ThrowIfNull(latest);
        if (latest.SchemaVersion != DetailedMapCatalogLatest.CurrentSchemaVersion ||
            !DetailedMapScenarioCatalog.TryGetByKey(latest.ScenarioKey, out _) ||
            !IsReleaseId(latest.ReleaseId) ||
            !IsSafeRelativeObjectPath(latest.CatalogPath) ||
            !IsSafeRelativeObjectPath(latest.SignaturePath) ||
            !IsLowerHex(latest.CatalogSha256, 64) ||
            !IsLowerHex(latest.ModelSha256, 64))
        {
            throw new InvalidDataException("Detailed-map latest pointer is invalid.");
        }
    }

    public static bool IsReleaseId(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        int separator = value.LastIndexOf('.');
        if (separator != 10 ||
            !DateOnly.TryParseExact(
                value.AsSpan(0, separator),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _) ||
            !int.TryParse(
                value.AsSpan(separator + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int sequence) ||
            sequence <= 0)
        {
            return false;
        }
        return true;
    }

    private static void ValidateScope(
        string scenarioKey,
        uint territoryId,
        int[] floors,
        string releaseId)
    {
        if (!DetailedMapScenarioCatalog.TryGetByKey(
                scenarioKey,
                out DetailedMapScenarioDefinition? scenario) ||
            territoryId != scenario.TerritoryId ||
            floors is null ||
            !floors.SequenceEqual(scenario.Floors) ||
            !IsReleaseId(releaseId))
        {
            throw new InvalidDataException("Detailed-map catalog scope or releaseId is invalid.");
        }
    }

    private static int FindUniqueCandidate(
        IReadOnlyList<DetailedMapCatalogCandidate> candidates,
        in RawWorldPosition position)
    {
        int match = -1;
        for (int index = 0; index < candidates.Count; index++)
        {
            if (!RawWorldPosition.CanonicallyEquals(candidates[index].Position, position))
                continue;
            if (match >= 0)
                return -1;
            match = index;
        }
        return match;
    }

    private static void ValidatePosition(in RawWorldPosition position, string label)
    {
        if (!float.IsFinite(position.X) ||
            !float.IsFinite(position.Y) ||
            !float.IsFinite(position.Z) ||
            MathF.Abs(position.X) > DetailedMapEvidenceContract.CoordinateMagnitudeLimit ||
            MathF.Abs(position.Y) > DetailedMapEvidenceContract.CoordinateMagnitudeLimit ||
            MathF.Abs(position.Z) > DetailedMapEvidenceContract.CoordinateMagnitudeLimit)
        {
            throw new InvalidDataException($"Detailed-map catalog {label} position is invalid.");
        }
    }

    private static bool IsSafeRelativeObjectPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith("/", StringComparison.Ordinal) ||
            value.Contains('\\') ||
            value.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }
        return value.Split('/').All(part =>
            part.Length > 0 &&
            part.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_' or '.'));
    }

    private static bool IsLowerHex(string value, int length)
    {
        if (value.Length != length)
            return false;
        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private sealed class DetailedMapCatalogModel
    {
        public string ScenarioKey { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string? HoardYieldSha256 { get; init; }
        public uint TerritoryId { get; init; }
        public int[] Floors { get; init; } = [];
        public DetailedMapCatalogFloorSample[] FloorSamples { get; init; } = [];
        public DetailedMapCatalogRoom[] Rooms { get; init; } = [];
    }
}

public readonly record struct DetailedMapRoomCandidate(
    RawWorldPosition Position,
    int HoardObservationCount);

public readonly record struct DetailedMapRoomCandidatePlan(
    IReadOnlyList<DetailedMapRoomCandidate> DirectCandidates,
    IReadOnlyList<DetailedMapRoomCandidate> OrdinaryCandidates,
    bool UsePalacePalFallback);

public static class DetailedMapRoomCandidatePlanner
{
    public static DetailedMapRoomCandidatePlan BuildPriorityOrder(
        DetailedMapCatalog catalog,
        DetailedMapCatalogRoom? room,
        byte floor,
        IReadOnlyList<RawWorldPosition> observedSightTraps)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(observedSightTraps);
        if (!catalog.TryGetFloorIndex(floor, out int floorIndex))
        {
            throw new ArgumentOutOfRangeException(
                nameof(floor),
                floor,
                "The floor is outside the detailed-map catalog scope.");
        }
        if (room == null)
        {
            return new DetailedMapRoomCandidatePlan(
                Array.Empty<DetailedMapRoomCandidate>(),
                Array.Empty<DetailedMapRoomCandidate>(),
                UsePalacePalFallback: true);
        }

        // A real bootstrap catalog deliberately contains the full PalacePal
        // candidate universe with zero observations. Until one of those
        // candidates has learned evidence, keep the runtime on the complete
        // PalacePal set instead of accidentally treating an empty learned
        // ordering as a valid short route.
        if (room.Candidates.All(candidate =>
                candidate.Successor.State == DetailedMapSuccessorState.Unknown &&
                candidate.HoardCountsByFloor[floorIndex] == 0))
        {
            return new DetailedMapRoomCandidatePlan(
                Array.Empty<DetailedMapRoomCandidate>(),
                Array.Empty<DetailedMapRoomCandidate>(),
                UsePalacePalFallback: true);
        }

        var directIndices = new HashSet<int>();
        for (int trapIndex = 0; trapIndex < observedSightTraps.Count; trapIndex++)
        {
            int sourceIndex = FindUniqueCandidate(
                room.Candidates,
                observedSightTraps[trapIndex]);
            if (sourceIndex < 0)
                continue;

            DetailedMapCatalogSuccessor successor =
                room.Candidates[sourceIndex].Successor;
            if (successor.State != DetailedMapSuccessorState.ObservedUnique ||
                !successor.Target.HasValue)
            {
                continue;
            }

            int targetIndex = FindUniqueCandidate(
                room.Candidates,
                successor.Target.Value);
            if (targetIndex >= 0)
                directIndices.Add(targetIndex);
        }

        DetailedMapRoomCandidate CreateCandidate(int candidateIndex)
        {
            DetailedMapCatalogCandidate candidate = room.Candidates[candidateIndex];
            return new DetailedMapRoomCandidate(
                candidate.Position,
                candidate.HoardCountsByFloor[floorIndex]);
        }

        DetailedMapRoomCandidate[] direct = directIndices
            .Select(CreateCandidate)
            .OrderByDescending(candidate => candidate.HoardObservationCount)
            .ThenBy(candidate => candidate.Position.X)
            .ThenBy(candidate => candidate.Position.Y)
            .ThenBy(candidate => candidate.Position.Z)
            .ToArray();
        if (direct.Length > 0)
        {
            return new DetailedMapRoomCandidatePlan(
                direct,
                Array.Empty<DetailedMapRoomCandidate>(),
                UsePalacePalFallback: false);
        }

        int probeDepth = GetFixedProbeDepth(floor);
        DetailedMapRoomGraphPresentation graph =
            DetailedMapRoomGraphAnalyzer.Analyze(room);
        DetailedMapRoomCandidate[] ordinary;
        if (graph.State == DetailedMapRoomGraphPresentationState.Complete)
        {
            ordinary = graph.CompleteChainOrder
                .Skip(1)
                .Take(probeDepth)
                .Select(position =>
                {
                    int candidateIndex = FindUniqueCandidate(
                        room.Candidates,
                        position);
                    return CreateCandidate(candidateIndex);
                })
                .ToArray();
        }
        else
        {
            bool hasExactFloorHoardCount = room.Candidates.Any(
                candidate =>
                    candidate.HoardCountsByFloor[floorIndex] > 0);
            if (!hasExactFloorHoardCount)
            {
                return new DetailedMapRoomCandidatePlan(
                    Array.Empty<DetailedMapRoomCandidate>(),
                    Array.Empty<DetailedMapRoomCandidate>(),
                    UsePalacePalFallback: true);
            }

            ordinary = Enumerable
                .Range(0, room.Candidates.Length)
                .Select(CreateCandidate)
                .OrderByDescending(candidate => candidate.HoardObservationCount)
                .ThenBy(candidate => candidate.Position.X)
                .ThenBy(candidate => candidate.Position.Y)
                .ThenBy(candidate => candidate.Position.Z)
                .Take(probeDepth)
                .ToArray();
        }

        return new DetailedMapRoomCandidatePlan(
            Array.Empty<DetailedMapRoomCandidate>(),
            ordinary,
            UsePalacePalFallback: false);
    }

    public static int GetFixedProbeDepth(byte floor) =>
        (floor % 10) switch
        {
            1 => 1,
            >= 2 and <= 4 => 2,
            >= 5 and <= 9 => 3,
            _ => throw new ArgumentOutOfRangeException(
                nameof(floor),
                floor,
                "The fixed detailed-map strategy does not apply to boss floors.")
        };

    public static int FindUniqueCandidate(
        IReadOnlyList<DetailedMapCatalogCandidate> candidates,
        in RawWorldPosition position)
    {
        int match = -1;
        for (int index = 0; index < candidates.Count; index++)
        {
            if (!RawWorldPosition.CanonicallyEquals(
                    candidates[index].Position,
                    position))
            {
                continue;
            }

            if (match >= 0)
                return -1;
            match = index;
        }

        return match;
    }
}
