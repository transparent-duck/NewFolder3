using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;

namespace NewFolder3;

internal sealed class CommunityEvidenceCollector : IFloorEvidenceObserver, IDisposable
{
    private const int MaximumSpoolFiles = 4096;
    private const long MaximumSpoolBytes = 64L * 1024L * 1024L;
    private static readonly TimeSpan UploadInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromHours(1);

    private readonly string _pendingDirectory;
    private readonly string _rejectedDirectory;
    private readonly string _installationToken;
    private readonly Uri _endpoint;
    private readonly IPluginLog _log;
    private readonly HttpClient _httpClient;
    private readonly Channel<DetailedMapFloorEvidence> _observations =
        Channel.CreateUnbounded<DetailedMapFloorEvidence>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
    private readonly object _runStateLock = new();
    private readonly Task _worker;

    private bool _runActive;
    private bool _captureCurrentRun;
    private string? _catalogReleaseForCurrentRun;
    private bool _stopping;
    private int _consecutiveUploadFailures;

    public CommunityEvidenceCollector(
        string pluginConfigDirectory,
        string installationToken,
        Uri endpoint,
        IPluginLog log,
        HttpMessageHandler? httpMessageHandler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginConfigDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationToken);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(log);

        _pendingDirectory = Path.Combine(
            pluginConfigDirectory,
            "DetailedMapEvidence",
            "pending");
        _rejectedDirectory = Path.Combine(
            pluginConfigDirectory,
            "DetailedMapEvidence",
            "rejected");
        _installationToken = installationToken;
        _endpoint = endpoint;
        _log = log;
        _httpClient = httpMessageHandler == null
            ? new HttpClient()
            : new HttpClient(httpMessageHandler, disposeHandler: true);
        _httpClient.Timeout = TimeSpan.FromSeconds(20);

        Directory.CreateDirectory(_pendingDirectory);
        Directory.CreateDirectory(_rejectedDirectory);
        _worker = Task.Run(WorkerLoopAsync);
    }

    public void ObserveRunState(
        bool isRunActive,
        bool detailedMapEnabled,
        string? catalogReleaseUsed)
    {
        lock (_runStateLock)
        {
            if (_stopping)
                return;

            if (!_runActive && isRunActive)
            {
                _captureCurrentRun = detailedMapEnabled;
                _catalogReleaseForCurrentRun = detailedMapEnabled
                    ? catalogReleaseUsed
                    : null;
            }
            else if (_runActive && !isRunActive)
            {
                _captureCurrentRun = false;
                _catalogReleaseForCurrentRun = null;
            }

            _runActive = isRunActive;
        }
    }

    public void OnFloorEvidencePersisted(FloorEvidenceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        string? catalogReleaseUsed;
        lock (_runStateLock)
        {
            if (_stopping || !_captureCurrentRun)
                return;
            catalogReleaseUsed = _catalogReleaseForCurrentRun;
        }

        if (!DetailedMapEvidenceProjector.TryProject(
                bundle,
                catalogReleaseUsed,
                out DetailedMapFloorEvidence? evidence,
                out string rejectionReason))
        {
            if (!string.Equals(
                    rejectionReason,
                    "unsupported-scenario-scope",
                    StringComparison.Ordinal))
            {
                _log.Warning(
                    "Detailed-map evidence projection rejected floor {FloorInstanceId}: {Reason}",
                    bundle.FloorInstanceId,
                    rejectionReason);
            }
            return;
        }

        if (evidence == null || !_observations.Writer.TryWrite(evidence))
        {
            _log.Error(
                "Detailed-map evidence could not be queued for floor {FloorInstanceId}.",
                bundle.FloorInstanceId);
        }
    }

    private async Task WorkerLoopAsync()
    {
        DateTime nextUploadAttempt = DateTime.UtcNow;
        try
        {
            while (true)
            {
                while (_observations.Reader.TryRead(out DetailedMapFloorEvidence? evidence))
                    await PersistAsync(evidence).ConfigureAwait(false);

                lock (_runStateLock)
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

                Task<bool> observationAvailable =
                    _observations.Reader.WaitToReadAsync().AsTask();
                Task delay = Task.Delay(wait);
                Task completed = await Task.WhenAny(observationAvailable, delay)
                    .ConfigureAwait(false);
                if (completed == observationAvailable &&
                    !await observationAvailable.ConfigureAwait(false))
                {
                    break;
                }
            }

            while (_observations.Reader.TryRead(out DetailedMapFloorEvidence? evidence))
                await PersistAsync(evidence).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _log.Error(error, "Detailed-map community evidence worker stopped unexpectedly.");
        }
    }

    private async Task PersistAsync(DetailedMapFloorEvidence evidence)
    {
        DetailedMapEvidenceBatch batch =
            DetailedMapEvidenceContract.CreateCanonicalBatch([evidence]);
        byte[] canonicalJson = DetailedMapEvidenceContract.SerializeCanonical(batch);
        string batchId = DetailedMapEvidenceContract.ComputeBatchId(canonicalJson);
        string finalPath = Path.Combine(_pendingDirectory, $"{batchId}.json");
        if (File.Exists(finalPath))
            return;

        FileInfo[] existingFiles = new DirectoryInfo(_pendingDirectory)
            .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
            .ToArray();
        long existingBytes = existingFiles.Sum(file => file.Length);
        if (existingFiles.Length >= MaximumSpoolFiles ||
            existingBytes + canonicalJson.Length > MaximumSpoolBytes)
        {
            _log.Error(
                "Detailed-map evidence spool is full ({FileCount} files, {ByteCount} bytes); floor {FloorInstanceId} was not persisted.",
                existingFiles.Length,
                existingBytes,
                evidence.FloorInstanceId);
            return;
        }

        string temporaryPath = Path.Combine(
            _pendingDirectory,
            $".{batchId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, canonicalJson)
                .ConfigureAwait(false);
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
            .Take(DetailedMapEvidenceBatch.MaximumFloorCount)
            .ToArray();
        if (selectedFiles.Length == 0)
            return UploadAttemptResult.NoPendingEvidence;

        var floors = new List<DetailedMapFloorEvidence>(selectedFiles.Length);
        foreach (string file in selectedFiles)
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                DetailedMapEvidenceBatch stored = DetailedMapEvidenceContract.Parse(bytes);
                if (stored.Floors.Length != 1)
                    throw new InvalidDataException("Spool entry must contain exactly one floor.");
                floors.Add(stored.Floors[0]);
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                MoveToRejected(file, "invalid-local-entry");
                _log.Error(
                    error,
                    "Detailed-map evidence spool entry {FileName} is invalid and was retained under rejected.",
                    Path.GetFileName(file));
                return UploadAttemptResult.LocalEntryRejected;
            }
        }

        byte[] payload;
        string batchId;
        try
        {
            DetailedMapEvidenceBatch batch =
                DetailedMapEvidenceContract.CreateCanonicalBatch(floors);
            payload = DetailedMapEvidenceContract.SerializeCanonical(batch);
            batchId = DetailedMapEvidenceContract.ComputeBatchId(payload);
        }
        catch (InvalidDataException error)
        {
            foreach (string file in selectedFiles)
                MoveToRejected(file, "invalid-local-batch");
            _log.Error(
                error,
                "Detailed-map evidence batch was invalid and its entries were retained under rejected.");
            return UploadAttemptResult.LocalEntryRejected;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Add("x-installation-token", _installationToken);
            request.Headers.Add("x-evidence-sha256", batchId);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using HttpResponseMessage response =
                await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted)
            {
                foreach (string file in selectedFiles)
                    File.Delete(file);
                _log.Debug(
                    "Uploaded detailed-map evidence batch {BatchId} ({FloorCount} floors).",
                    batchId,
                    floors.Count);
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
                    "Detailed-map evidence batch {BatchId} was permanently rejected with HTTP {StatusCode}; entries were retained under rejected.",
                    batchId,
                    (int)response.StatusCode);
                return UploadAttemptResult.ServerRejected;
            }

            _log.Warning(
                "Detailed-map evidence upload returned HTTP {StatusCode}; pending evidence will be retried.",
                (int)response.StatusCode);
            return UploadAttemptResult.TransientFailure;
        }
        catch (Exception error) when (
            error is HttpRequestException or TaskCanceledException or IOException)
        {
            _log.Warning(
                error,
                "Detailed-map evidence upload failed; pending evidence will be retried.");
            return UploadAttemptResult.TransientFailure;
        }
    }

    private TimeSpan GetNextDelay(UploadAttemptResult result)
    {
        if (result is
            UploadAttemptResult.Accepted or
            UploadAttemptResult.NoPendingEvidence or
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

    public void Dispose()
    {
        lock (_runStateLock)
        {
            if (_stopping)
                return;
            _stopping = true;
            _captureCurrentRun = false;
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
        NoPendingEvidence,
        Accepted,
        LocalEntryRejected,
        ServerRejected,
        TransientFailure
    }
}
