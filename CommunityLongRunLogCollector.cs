using System.Globalization;
using System.IO.Compression;
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

internal sealed class CommunityRunLogEnvelope
{
    internal const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ScenarioKey { get; init; } = string.Empty;
    public string ClientVersion { get; init; } = string.Empty;
    public long DurationMilliseconds { get; init; }
    public string LogSha256 { get; init; } = string.Empty;
    public string Compression { get; init; } = CommunityRunLogContract.GzipBase64Encoding;
    public string LogData { get; init; } = string.Empty;
}

internal static class CommunityRunLogContract
{
    internal const string GzipBase64Encoding = "gzip-base64";
    internal const int MaximumRawLogBytes = 16 * 1024 * 1024;
    internal const int MaximumCompressedLogBytes = 1024 * 1024;
    internal static readonly TimeSpan MinimumDuration = TimeSpan.FromMinutes(30);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly HashSet<string> PersonalIdentityPropertyNames = new(
        [
            "accountId",
            "characterName",
            "contentId",
            "homeWorld",
            "homeWorldId",
            "playerName",
            "recorderPath",
            "worldName"
        ],
        StringComparer.OrdinalIgnoreCase);

    internal static CommunityRunLogEnvelope CreateEnvelope(
        ReadOnlySpan<byte> rawLog,
        string scenarioKey,
        string clientVersion)
    {
        ValidateScenarioAndVersion(scenarioKey, clientVersion);
        TimeSpan duration = ValidateRunLog(rawLog);
        byte[] compressed = Compress(rawLog);
        if (compressed.Length > MaximumCompressedLogBytes)
        {
            throw new InvalidDataException(
                $"Compressed run log exceeds {MaximumCompressedLogBytes} bytes.");
        }

        return new CommunityRunLogEnvelope
        {
            ScenarioKey = scenarioKey,
            ClientVersion = clientVersion,
            DurationMilliseconds = checked((long)duration.TotalMilliseconds),
            LogSha256 = Convert.ToHexString(SHA256.HashData(rawLog)).ToLowerInvariant(),
            LogData = Convert.ToBase64String(compressed)
        };
    }

    internal static byte[] Serialize(CommunityRunLogEnvelope envelope)
    {
        ValidateEnvelope(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
    }

    internal static CommunityRunLogEnvelope Parse(ReadOnlySpan<byte> utf8Json)
    {
        CommunityRunLogEnvelope envelope =
            JsonSerializer.Deserialize<CommunityRunLogEnvelope>(utf8Json, JsonOptions)
            ?? throw new InvalidDataException("Run-log envelope is empty.");
        ValidateEnvelope(envelope);
        return envelope;
    }

    internal static byte[] GetRawLog(CommunityRunLogEnvelope envelope)
    {
        ValidateEnvelope(envelope);
        byte[] compressed;
        try
        {
            compressed = Convert.FromBase64String(envelope.LogData);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("Run-log payload is not canonical base64.", error);
        }
        if (compressed.Length == 0 || compressed.Length > MaximumCompressedLogBytes)
            throw new InvalidDataException("Compressed run-log payload size is invalid.");

        byte[] rawLog;
        try
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                int read = gzip.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                if (output.Length + read > MaximumRawLogBytes)
                    throw new InvalidDataException("Decompressed run log exceeds the size limit.");
                output.Write(buffer, 0, read);
            }
            rawLog = output.ToArray();
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or NotSupportedException)
        {
            throw new InvalidDataException("Run-log gzip payload is invalid.", error);
        }

        TimeSpan duration = ValidateRunLog(rawLog);
        string logSha256 = Convert.ToHexString(SHA256.HashData(rawLog)).ToLowerInvariant();
        if (!string.Equals(logSha256, envelope.LogSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Run-log SHA-256 does not match the payload.");
        if (Math.Abs(duration.TotalMilliseconds - envelope.DurationMilliseconds) >= 1d)
            throw new InvalidDataException("Run-log duration does not match the payload.");
        return rawLog;
    }

    internal static string ComputePayloadId(ReadOnlySpan<byte> utf8Json) =>
        Convert.ToHexString(SHA256.HashData(utf8Json)).ToLowerInvariant();

    private static void ValidateEnvelope(CommunityRunLogEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.SchemaVersion != CommunityRunLogEnvelope.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported run-log schemaVersion {envelope.SchemaVersion}.");
        }
        ValidateScenarioAndVersion(envelope.ScenarioKey, envelope.ClientVersion);
        if (envelope.DurationMilliseconds <= MinimumDuration.TotalMilliseconds ||
            envelope.DurationMilliseconds > TimeSpan.FromHours(24).TotalMilliseconds)
        {
            throw new InvalidDataException("Run-log duration is outside the accepted range.");
        }
        if (!IsLowerHex(envelope.LogSha256, 64))
            throw new InvalidDataException("Run-log SHA-256 is invalid.");
        if (!string.Equals(envelope.Compression, GzipBase64Encoding, StringComparison.Ordinal))
            throw new InvalidDataException("Run-log compression is unsupported.");
        if (string.IsNullOrEmpty(envelope.LogData))
            throw new InvalidDataException("Run-log payload is empty.");
    }

    private static void ValidateScenarioAndVersion(string scenarioKey, string clientVersion)
    {
        if (!DetailedMapScenarioCatalog.TryGetByKey(scenarioKey, out _))
            throw new InvalidDataException("Run-log scenario key is unsupported.");
        if (string.IsNullOrWhiteSpace(clientVersion) || clientVersion.Length > 96)
            throw new InvalidDataException("Run-log client version is invalid.");
    }

    private static TimeSpan ValidateRunLog(ReadOnlySpan<byte> rawLog)
    {
        if (rawLog.Length == 0 || rawLog.Length > MaximumRawLogBytes)
            throw new InvalidDataException("Run-log size is invalid.");

        string text;
        try
        {
            text = StrictUtf8.GetString(rawLog);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("Run log is not valid UTF-8.", error);
        }

        DateTimeOffset? firstTimestamp = null;
        DateTimeOffset? lastTimestamp = null;
        string? firstEventType = null;
        string? lastEventType = null;
        string? closingReason = null;
        int lineCount = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            lineCount++;
            if (lineCount > 250_000)
                throw new InvalidDataException("Run log contains too many events.");

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("timestampUtc", out JsonElement timestampElement) ||
                    !root.TryGetProperty("eventType", out JsonElement eventTypeElement) ||
                    !root.TryGetProperty("data", out JsonElement dataElement) ||
                    timestampElement.ValueKind != JsonValueKind.String ||
                    eventTypeElement.ValueKind != JsonValueKind.String ||
                    !timestampElement.TryGetDateTimeOffset(out DateTimeOffset timestamp))
                {
                    throw new InvalidDataException("Run-log event envelope is invalid.");
                }

                RejectPersonalIdentityProperties(root);
                string eventType = eventTypeElement.GetString() ?? string.Empty;
                firstTimestamp ??= timestamp;
                firstEventType ??= eventType;
                lastTimestamp = timestamp;
                lastEventType = eventType;
                closingReason = eventType == "run-recorder-closing" &&
                                dataElement.ValueKind == JsonValueKind.Object &&
                                dataElement.TryGetProperty("reason", out JsonElement reasonElement) &&
                                reasonElement.ValueKind == JsonValueKind.String
                    ? reasonElement.GetString()
                    : null;
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("Run log contains invalid JSON.", error);
            }
        }

        if (firstTimestamp is null || lastTimestamp is null ||
            firstEventType != "controller-initialized" ||
            lastEventType != "run-recorder-closing" ||
            closingReason is not ("fsd-loop-complete" or "fsd-final-loop-complete"))
        {
            throw new InvalidDataException("Run log is not one completed FSD loop.");
        }

        TimeSpan duration = lastTimestamp.Value - firstTimestamp.Value;
        if (duration <= MinimumDuration || duration > TimeSpan.FromHours(24))
            throw new InvalidDataException("Completed FSD loop did not exceed 30 minutes.");
        return duration;
    }

    private static void RejectPersonalIdentityProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (PersonalIdentityPropertyNames.Contains(property.Name))
                {
                    throw new InvalidDataException(
                        $"Run log contains forbidden identity property '{property.Name}'.");
                }
                RejectPersonalIdentityProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
                RejectPersonalIdentityProperties(item);
        }
    }

    private static byte[] Compress(ReadOnlySpan<byte> rawLog)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(rawLog);
        return output.ToArray();
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
}

internal sealed class CommunityLongRunLogCollector : IRunTelemetryObserver, IDisposable
{
    private const int MaximumSpoolFiles = 128;
    private const long MaximumSpoolBytes = 64L * 1024L * 1024L;
    private static readonly TimeSpan UploadInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromHours(1);

    private readonly string _pendingDirectory;
    private readonly string _rejectedDirectory;
    private readonly string _installationToken;
    private readonly string _clientVersion;
    private readonly Uri _endpoint;
    private readonly IPluginLog _log;
    private readonly HttpClient _httpClient;
    private readonly Channel<RunRecordingClosedTelemetry> _observations =
        Channel.CreateUnbounded<RunRecordingClosedTelemetry>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
    private readonly object _stateLock = new();
    private readonly Task _worker;

    private bool _stopping;
    private int _consecutiveUploadFailures;

    internal CommunityLongRunLogCollector(
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

        _pendingDirectory = Path.Combine(pluginConfigDirectory, "LongRunLogs", "pending");
        _rejectedDirectory = Path.Combine(pluginConfigDirectory, "LongRunLogs", "rejected");
        _installationToken = installationToken;
        _clientVersion = clientVersion;
        _endpoint = endpoint;
        _log = log;
        _httpClient = httpMessageHandler == null
            ? new HttpClient()
            : new HttpClient(httpMessageHandler, disposeHandler: true);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        Directory.CreateDirectory(_pendingDirectory);
        Directory.CreateDirectory(_rejectedDirectory);
        _worker = Task.Run(WorkerLoopAsync);
    }

    public void ObserveWaypointTerminal(in RunWaypointTerminalTelemetry observation)
    {
    }

    public void ObserveFloorBoundary(in RunFloorBoundaryTelemetry observation)
    {
    }

    public void ObserveFloorTerminal(in RunFloorTerminalTelemetry observation)
    {
    }

    public void ObserveFloorState(RunFloorStateTelemetry observation)
    {
    }

    public void ObserveRunRecordingClosed(in RunRecordingClosedTelemetry observation)
    {
        lock (_stateLock)
        {
            if (_stopping)
                return;
        }

        if (!observation.DetailedMapActive ||
            observation.ControlledSurvey ||
            observation.ClosedAtUtc - observation.StartedAtUtc <= CommunityRunLogContract.MinimumDuration ||
            observation.Reason is not ("fsd-loop-complete" or "fsd-final-loop-complete") ||
            observation.ScenarioKey is not { } scenarioKey ||
            !DetailedMapScenarioCatalog.TryGetByKey(scenarioKey, out _))
        {
            return;
        }

        if (!_observations.Writer.TryWrite(observation))
        {
            _log.Error(
                "Completed long-run log {FileName} could not be queued.",
                Path.GetFileName(observation.FilePath));
        }
    }

    private async Task WorkerLoopAsync()
    {
        DateTime nextUploadAttempt = DateTime.UtcNow;
        try
        {
            while (true)
            {
                bool persistedObservation = false;
                while (_observations.Reader.TryRead(out RunRecordingClosedTelemetry observation))
                {
                    await PersistAsync(observation).ConfigureAwait(false);
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

            while (_observations.Reader.TryRead(out RunRecordingClosedTelemetry observation))
                await PersistAsync(observation).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _log.Error(error, "Long detailed-map run-log worker stopped unexpectedly.");
        }
    }

    private async Task PersistAsync(RunRecordingClosedTelemetry observation)
    {
        try
        {
            byte[] rawLog = await File.ReadAllBytesAsync(observation.FilePath).ConfigureAwait(false);
            CommunityRunLogEnvelope envelope = CommunityRunLogContract.CreateEnvelope(
                rawLog,
                observation.ScenarioKey!,
                _clientVersion);
            byte[] payload = CommunityRunLogContract.Serialize(envelope);
            string finalPath = Path.Combine(_pendingDirectory, $"{envelope.LogSha256}.json");
            if (File.Exists(finalPath))
                return;

            FileInfo[] existingFiles = new DirectoryInfo(_pendingDirectory)
                .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
                .ToArray();
            long existingBytes = existingFiles.Sum(file => file.Length);
            if (existingFiles.Length >= MaximumSpoolFiles ||
                existingBytes + payload.Length > MaximumSpoolBytes)
            {
                _log.Error(
                    "Long-run log spool is full ({FileCount} files, {ByteCount} bytes); {FileName} was not persisted.",
                    existingFiles.Length,
                    existingBytes,
                    Path.GetFileName(observation.FilePath));
                return;
            }

            string temporaryPath = Path.Combine(
                _pendingDirectory,
                $".{envelope.LogSha256}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, payload).ConfigureAwait(false);
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
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.Error(
                error,
                "Completed long-run log {FileName} was not eligible for upload.",
                Path.GetFileName(observation.FilePath));
        }
    }

    private async Task<UploadAttemptResult> TryUploadAsync()
    {
        string? selectedFile = Directory
            .EnumerateFiles(_pendingDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .FirstOrDefault();
        if (selectedFile == null)
            return UploadAttemptResult.NoPendingLogs;

        byte[] payload;
        string payloadId;
        try
        {
            payload = await File.ReadAllBytesAsync(selectedFile).ConfigureAwait(false);
            CommunityRunLogEnvelope envelope = CommunityRunLogContract.Parse(payload);
            _ = CommunityRunLogContract.GetRawLog(envelope);
            payloadId = CommunityRunLogContract.ComputePayloadId(payload);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MoveToRejected(selectedFile, "invalid-local-entry");
            _log.Error(
                error,
                "Long-run log spool entry {FileName} is invalid and was retained under rejected.",
                Path.GetFileName(selectedFile));
            return UploadAttemptResult.LocalEntryRejected;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Add("x-installation-token", _installationToken);
            request.Headers.Add("x-run-log-sha256", payloadId);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using HttpResponseMessage response =
                await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted)
            {
                File.Delete(selectedFile);
                return UploadAttemptResult.Accepted;
            }
            if (response.StatusCode is
                HttpStatusCode.BadRequest or
                HttpStatusCode.RequestEntityTooLarge or
                HttpStatusCode.UnsupportedMediaType)
            {
                MoveToRejected(selectedFile, $"server-{(int)response.StatusCode}");
                _log.Error(
                    "Long-run log {PayloadId} was permanently rejected with HTTP {StatusCode}.",
                    payloadId,
                    (int)response.StatusCode);
                return UploadAttemptResult.ServerRejected;
            }

            _log.Warning(
                "Long-run log upload returned HTTP {StatusCode}; the pending log will be retried.",
                (int)response.StatusCode);
            return UploadAttemptResult.TransientFailure;
        }
        catch (Exception error) when (
            error is HttpRequestException or TaskCanceledException or IOException)
        {
            _log.Warning(error, "Long-run log upload failed; the pending log will be retried.");
            return UploadAttemptResult.TransientFailure;
        }
    }

    private TimeSpan GetNextDelay(UploadAttemptResult result)
    {
        if (result is
            UploadAttemptResult.Accepted or
            UploadAttemptResult.NoPendingLogs or
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
        NoPendingLogs,
        Accepted,
        LocalEntryRejected,
        ServerRejected,
        TransientFailure
    }
}
