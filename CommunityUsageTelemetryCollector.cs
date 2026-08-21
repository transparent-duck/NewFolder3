using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime;

namespace NewFolder3;

internal static class CommunityUsageEventTypes
{
    internal const string PluginActive = "plugin_active";
    internal const string FsdStarted = "fsd_started";
    internal const string DetailedMapRunStarted = "detailed_map_run_started";
    internal const string FsdCompleted = "fsd_completed";

    internal static bool IsSupported(string value) => value is
        PluginActive or
        FsdStarted or
        DetailedMapRunStarted or
        FsdCompleted;
}

internal sealed class CommunityUsageEventBatch
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumEventCount = 32;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public CommunityUsageEvent[] Events { get; init; } = [];
}

internal sealed class CommunityUsageEvent
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string OccurredDateUtc { get; init; } = string.Empty;
    public string ClientVersion { get; init; } = string.Empty;
    public string? ScenarioKey { get; init; }
}

internal static class CommunityUsageTelemetryContract
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static CommunityUsageEventBatch CreateBatch(
        IEnumerable<CommunityUsageEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        CommunityUsageEvent[] ordered = events
            .OrderBy(value => value.EventId, StringComparer.Ordinal)
            .ToArray();
        var batch = new CommunityUsageEventBatch { Events = ordered };
        Validate(batch);
        return batch;
    }

    internal static byte[] Serialize(CommunityUsageEventBatch batch)
    {
        Validate(batch);
        return JsonSerializer.SerializeToUtf8Bytes(batch, JsonOptions);
    }

    internal static CommunityUsageEventBatch Parse(ReadOnlySpan<byte> utf8Json)
    {
        CommunityUsageEventBatch batch =
            JsonSerializer.Deserialize<CommunityUsageEventBatch>(utf8Json, JsonOptions)
            ?? throw new InvalidDataException("Usage telemetry batch is empty.");
        Validate(batch);
        return batch;
    }

    internal static string ComputeBatchId(ReadOnlySpan<byte> utf8Json) =>
        Convert.ToHexString(SHA256.HashData(utf8Json)).ToLowerInvariant();

    private static void Validate(CommunityUsageEventBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.SchemaVersion != CommunityUsageEventBatch.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported usage telemetry schemaVersion {batch.SchemaVersion}.");
        }
        if (batch.Events is null ||
            batch.Events.Length == 0 ||
            batch.Events.Length > CommunityUsageEventBatch.MaximumEventCount)
        {
            throw new InvalidDataException(
                $"Usage telemetry must contain 1-{CommunityUsageEventBatch.MaximumEventCount} events.");
        }

        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (CommunityUsageEvent value in batch.Events)
        {
            if (!IsLowerHex(value.EventId, 32) || !eventIds.Add(value.EventId))
                throw new InvalidDataException("Usage telemetry eventId is invalid or duplicated.");
            if (!CommunityUsageEventTypes.IsSupported(value.EventType))
                throw new InvalidDataException("Usage telemetry eventType is unsupported.");
            if (!DateOnly.TryParseExact(
                    value.OccurredDateUtc,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
            {
                throw new InvalidDataException("Usage telemetry occurredDateUtc is invalid.");
            }
            if (string.IsNullOrWhiteSpace(value.ClientVersion) || value.ClientVersion.Length > 96)
                throw new InvalidDataException("Usage telemetry clientVersion is invalid.");

            bool requiresScenario = value.EventType != CommunityUsageEventTypes.PluginActive;
            if (requiresScenario != (value.ScenarioKey != null) ||
                value.ScenarioKey is not null && !IsSupportedScenario(value.ScenarioKey))
            {
                throw new InvalidDataException("Usage telemetry scenarioKey is invalid.");
            }
        }
    }

    private static bool IsSupportedScenario(string value) =>
        DetailedMapScenarioCatalog.TryGetByKey(value, out _);

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

/// <summary>
/// Host-side Necromancer FSD scenario index → usage telemetry scenario key.
/// Unknown indices stay null so callers do not invent a fallback scenario.
/// </summary>
internal static class CommunityUsageTelemetryScenarios
{
    internal static string? MapScenarioIndex(int scenarioIndex) => scenarioIndex switch
    {
        0 or 2 => DetailedMapEvidenceContract.PilgrimsTraverse21To30ScenarioKey,
        1 or 3 => DetailedMapScenarioCatalog.PilgrimsTraverse31To40.Key,
        _ => null
    };
}

internal sealed class CommunityUsageTelemetryCollector : IRunTelemetryObserver, IDisposable
{
    private const int MaximumSpoolFiles = 4096;
    private const long MaximumSpoolBytes = 8L * 1024L * 1024L;
    private static readonly TimeSpan UploadInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromHours(1);

    private readonly string _pendingDirectory;
    private readonly string _rejectedDirectory;
    private readonly string _installationToken;
    private readonly string _clientVersion;
    private readonly Uri _endpoint;
    private readonly IPluginLog _log;
    private readonly HttpClient _httpClient;
    private readonly Channel<CommunityUsageEvent> _observations =
        Channel.CreateUnbounded<CommunityUsageEvent>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
    private readonly object _stateLock = new();
    private readonly Task _worker;

    private bool _runActive;
    private bool _stopping;
    private int _consecutiveUploadFailures;

    internal CommunityUsageTelemetryCollector(
        string pluginConfigDirectory,
        string installationToken,
        string clientVersion,
        Uri endpoint,
        IPluginLog log,
        HttpMessageHandler? httpMessageHandler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginConfigDirectory);
        if (!IsLowerHex(installationToken, 32))
            throw new ArgumentException("Installation token must be 32 lowercase hex characters.", nameof(installationToken));
        ArgumentException.ThrowIfNullOrWhiteSpace(clientVersion);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(log);

        _pendingDirectory = Path.Combine(pluginConfigDirectory, "UsageTelemetry", "pending");
        _rejectedDirectory = Path.Combine(pluginConfigDirectory, "UsageTelemetry", "rejected");
        _installationToken = installationToken;
        _clientVersion = clientVersion;
        _endpoint = endpoint;
        _log = log;
        _httpClient = httpMessageHandler == null
            ? new HttpClient()
            : new HttpClient(httpMessageHandler, disposeHandler: true);
        _httpClient.Timeout = TimeSpan.FromSeconds(20);

        Directory.CreateDirectory(_pendingDirectory);
        Directory.CreateDirectory(_rejectedDirectory);
        _worker = Task.Run(WorkerLoopAsync);
        DateTime pluginActiveUtc = DateTime.UtcNow;
        QueueEvent(
            CommunityUsageEventTypes.PluginActive,
            scenarioKey: null,
            CreateDailyPluginActiveEventId(pluginActiveUtc),
            pluginActiveUtc);
    }

    internal void ObserveRunState(
        bool isRunActive,
        bool detailedMapEnabled,
        string? scenarioKey)
    {
        bool runStarted;
        lock (_stateLock)
        {
            if (_stopping)
                return;
            runStarted = !_runActive && isRunActive;
            _runActive = isRunActive;
        }

        if (!runStarted)
            return;
        if (scenarioKey is null ||
            !DetailedMapScenarioCatalog.TryGetByKey(scenarioKey, out _))
        {
            _log.Error(
                "Usage telemetry run start ignored because scenario key {ScenarioKey} is invalid.",
                scenarioKey ?? "<null>");
            return;
        }

        QueueEvent(CommunityUsageEventTypes.FsdStarted, scenarioKey);
        if (detailedMapEnabled)
        {
            QueueEvent(
                CommunityUsageEventTypes.DetailedMapRunStarted,
                scenarioKey);
        }
    }

    public void ObserveWaypointTerminal(in RunWaypointTerminalTelemetry observation)
    {
    }

    public void ObserveFloorBoundary(in RunFloorBoundaryTelemetry observation)
    {
    }

    public void ObserveFloorTerminal(in RunFloorTerminalTelemetry observation)
    {
        if (observation.ControlledSurvey ||
            observation.Outcome != RunFloorTerminalOutcome.PassageCompleted ||
            !TryResolveCompletedScenario(observation, out string? scenarioKey))
        {
            return;
        }

        QueueEvent(CommunityUsageEventTypes.FsdCompleted, scenarioKey);
    }

    public void ObserveFloorState(RunFloorStateTelemetry observation)
    {
    }

    public void ObserveRunRecordingClosed(in RunRecordingClosedTelemetry observation)
    {
    }

    private void QueueEvent(
        string eventType,
        string? scenarioKey,
        string? eventId = null,
        DateTime? occurredUtc = null)
    {
        lock (_stateLock)
        {
            if (_stopping)
                return;
        }

        var value = new CommunityUsageEvent
        {
            EventId = eventId ?? Guid.NewGuid().ToString("N"),
            EventType = eventType,
            OccurredDateUtc = (occurredUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ClientVersion = _clientVersion,
            ScenarioKey = scenarioKey
        };
        try
        {
            CommunityUsageTelemetryContract.CreateBatch([value]);
        }
        catch (InvalidDataException error)
        {
            _log.Error(error, "Usage telemetry event {EventType} was invalid and was not queued.", eventType);
            return;
        }

        if (!_observations.Writer.TryWrite(value))
            _log.Error("Usage telemetry event {EventType} could not be queued.", eventType);
    }

    private async Task WorkerLoopAsync()
    {
        DateTime nextUploadAttempt = DateTime.UtcNow;
        try
        {
            while (true)
            {
                bool persistedObservation = false;
                while (_observations.Reader.TryRead(out CommunityUsageEvent? value))
                {
                    await PersistAsync(value).ConfigureAwait(false);
                    persistedObservation = true;
                }
                if (persistedObservation && _consecutiveUploadFailures == 0)
                    nextUploadAttempt = DateTime.UtcNow;

                lock (_stateLock)
                {
                    if (_stopping)
                        break;
                }

                DateTime now = DateTime.UtcNow;
                if (now >= nextUploadAttempt)
                {
                    UploadAttemptResult result = await TryUploadAsync().ConfigureAwait(false);
                    nextUploadAttempt = DateTime.UtcNow + GetNextDelay(result);
                }

                TimeSpan wait = nextUploadAttempt - DateTime.UtcNow;
                if (wait < TimeSpan.Zero)
                    wait = TimeSpan.Zero;
                Task<bool> observationAvailable = _observations.Reader.WaitToReadAsync().AsTask();
                Task delay = Task.Delay(wait);
                Task completed = await Task.WhenAny(observationAvailable, delay).ConfigureAwait(false);
                if (completed == observationAvailable &&
                    !await observationAvailable.ConfigureAwait(false))
                {
                    break;
                }
            }

            while (_observations.Reader.TryRead(out CommunityUsageEvent? value))
                await PersistAsync(value).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _log.Error(error, "Usage telemetry worker stopped unexpectedly.");
        }
    }

    private async Task PersistAsync(CommunityUsageEvent value)
    {
        CommunityUsageEventBatch batch = CommunityUsageTelemetryContract.CreateBatch([value]);
        byte[] bytes = CommunityUsageTelemetryContract.Serialize(batch);
        string finalPath = Path.Combine(_pendingDirectory, $"{value.EventId}.json");
        if (File.Exists(finalPath))
            return;

        FileInfo[] existingFiles = new DirectoryInfo(_pendingDirectory)
            .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
            .ToArray();
        long existingBytes = existingFiles.Sum(file => file.Length);
        if (existingFiles.Length >= MaximumSpoolFiles ||
            existingBytes + bytes.Length > MaximumSpoolBytes)
        {
            _log.Error(
                "Usage telemetry spool is full ({FileCount} files, {ByteCount} bytes); event {EventId} was not persisted.",
                existingFiles.Length,
                existingBytes,
                value.EventId);
            return;
        }

        string temporaryPath = Path.Combine(
            _pendingDirectory,
            $".{value.EventId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes).ConfigureAwait(false);
            try
            {
                File.Move(temporaryPath, finalPath);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
            throw;
        }
    }

    private async Task<UploadAttemptResult> TryUploadAsync()
    {
        string[] selectedFiles = Directory
            .EnumerateFiles(_pendingDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Take(CommunityUsageEventBatch.MaximumEventCount)
            .ToArray();
        if (selectedFiles.Length == 0)
            return UploadAttemptResult.NoPendingEvents;

        var events = new List<CommunityUsageEvent>(selectedFiles.Length);
        foreach (string file in selectedFiles)
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                CommunityUsageEventBatch stored = CommunityUsageTelemetryContract.Parse(bytes);
                if (stored.Events.Length != 1)
                    throw new InvalidDataException("Spool entry must contain exactly one event.");
                events.Add(stored.Events[0]);
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                MoveToRejected(file, "invalid-local-entry");
                _log.Error(
                    error,
                    "Usage telemetry spool entry {FileName} is invalid and was retained under rejected.",
                    Path.GetFileName(file));
                return UploadAttemptResult.LocalEntryRejected;
            }
        }

        byte[] payload;
        string batchId;
        try
        {
            CommunityUsageEventBatch batch = CommunityUsageTelemetryContract.CreateBatch(events);
            payload = CommunityUsageTelemetryContract.Serialize(batch);
            batchId = CommunityUsageTelemetryContract.ComputeBatchId(payload);
        }
        catch (InvalidDataException error)
        {
            foreach (string file in selectedFiles)
                MoveToRejected(file, "invalid-local-batch");
            _log.Error(error, "Usage telemetry batch was invalid and retained under rejected.");
            return UploadAttemptResult.LocalEntryRejected;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Add("x-installation-token", _installationToken);
            request.Headers.Add("x-telemetry-sha256", batchId);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using HttpResponseMessage response =
                await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted)
            {
                foreach (string file in selectedFiles)
                    File.Delete(file);
                return UploadAttemptResult.Accepted;
            }
            if (response.StatusCode is
                HttpStatusCode.BadRequest or
                HttpStatusCode.RequestEntityTooLarge or
                HttpStatusCode.UnsupportedMediaType)
            {
                foreach (string file in selectedFiles)
                    MoveToRejected(file, $"server-{(int)response.StatusCode}");
                _log.Error(
                    "Usage telemetry batch {BatchId} was permanently rejected with HTTP {StatusCode}.",
                    batchId,
                    (int)response.StatusCode);
                return UploadAttemptResult.ServerRejected;
            }

            _log.Warning(
                "Usage telemetry upload returned HTTP {StatusCode}; pending events will be retried.",
                (int)response.StatusCode);
            return UploadAttemptResult.TransientFailure;
        }
        catch (Exception error) when (
            error is HttpRequestException or TaskCanceledException or IOException)
        {
            _log.Warning(error, "Usage telemetry upload failed; pending events will be retried.");
            return UploadAttemptResult.TransientFailure;
        }
    }

    private TimeSpan GetNextDelay(UploadAttemptResult result)
    {
        if (result is
            UploadAttemptResult.Accepted or
            UploadAttemptResult.NoPendingEvents or
            UploadAttemptResult.LocalEntryRejected or
            UploadAttemptResult.ServerRejected)
        {
            _consecutiveUploadFailures = 0;
            return result == UploadAttemptResult.Accepted
                ? TimeSpan.Zero
                : UploadInterval;
        }

        _consecutiveUploadFailures = Math.Min(_consecutiveUploadFailures + 1, 11);
        double seconds = Math.Min(
            UploadInterval.TotalSeconds * Math.Pow(2, _consecutiveUploadFailures - 1),
            MaximumRetryDelay.TotalSeconds);
        double jitter = 0.85 + Random.Shared.NextDouble() * 0.3;
        return TimeSpan.FromSeconds(seconds * jitter);
    }

    private void MoveToRejected(string sourcePath, string reason)
    {
        string destinationPath = Path.Combine(
            _rejectedDirectory,
            $"{reason}-{Path.GetFileName(sourcePath)}");
        if (File.Exists(destinationPath))
        {
            File.Delete(sourcePath);
            return;
        }
        File.Move(sourcePath, destinationPath);
    }

    private string CreateDailyPluginActiveEventId(DateTime nowUtc)
    {
        string source = string.Join(
            '\n',
            _installationToken,
            CommunityUsageEventTypes.PluginActive,
            nowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))
            .ToLowerInvariant()[..32];
    }

    private static bool TryResolveCompletedScenario(
        in RunFloorTerminalTelemetry observation,
        out string? scenarioKey)
    {
        scenarioKey = null;
        if (observation.DungeonId != DetailedMapEvidenceContract.PilgrimsTraverseDungeonId)
            return false;
        if (observation.FloorsetStart == 21 && observation.Floor == 29)
        {
            scenarioKey = DetailedMapEvidenceContract.PilgrimsTraverse21To30ScenarioKey;
            return true;
        }
        if (observation.FloorsetStart == 31 && observation.Floor == 39)
        {
            scenarioKey = DetailedMapScenarioCatalog.PilgrimsTraverse31To40.Key;
            return true;
        }
        return false;
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

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_stopping)
                return;
            _stopping = true;
            _runActive = false;
        }
        _observations.Writer.TryComplete();
        try
        {
            _worker.GetAwaiter().GetResult();
        }
        finally
        {
            _httpClient.Dispose();
        }
    }

    private enum UploadAttemptResult
    {
        NoPendingEvents,
        Accepted,
        LocalEntryRejected,
        ServerRejected,
        TransientFailure
    }
}
