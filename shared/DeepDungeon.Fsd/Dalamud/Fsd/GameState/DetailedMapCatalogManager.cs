using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.GameState;

internal enum DetailedMapRuntimePolicy
{
    PalacePalOnly,
    DetailedMap
}

internal sealed class DetailedMapRunSnapshot
{
    private readonly IReadOnlyDictionary<
        (int LayoutIndex, int RoomIndex),
        DetailedMapRoomGraphPresentation> _presentations;

    internal DetailedMapRunSnapshot(
        DetailedMapRuntimePolicy policy,
        string? scenarioKey,
        DetailedMapCatalog? catalog,
        IReadOnlyDictionary<
            (int LayoutIndex, int RoomIndex),
            DetailedMapRoomGraphPresentation>? presentations = null,
        HoardYieldCatalog? hoardYield = null)
    {
        if (catalog != null && presentations == null)
        {
            throw new ArgumentNullException(
                nameof(presentations),
                "A detailed-map run snapshot requires its presentation index.");
        }
        if (hoardYield != null && catalog == null)
        {
            throw new ArgumentException(
                "A hoard-yield snapshot cannot exist without its detailed-map catalog.",
                nameof(hoardYield));
        }

        Policy = policy;
        ScenarioKey = scenarioKey;
        Catalog = catalog;
        HoardYield = hoardYield;
        _presentations = presentations == null
            ? new Dictionary<
                (int LayoutIndex, int RoomIndex),
                DetailedMapRoomGraphPresentation>()
            : new Dictionary<
                (int LayoutIndex, int RoomIndex),
                DetailedMapRoomGraphPresentation>(presentations);
    }

    internal DetailedMapRuntimePolicy Policy { get; }
    internal string? ScenarioKey { get; }
    internal DetailedMapCatalog? Catalog { get; }
    internal HoardYieldCatalog? HoardYield { get; }
    internal string? ReleaseId => Catalog?.ReleaseId;
    internal string Status => Policy == DetailedMapRuntimePolicy.DetailedMap
        ? $"detailed map {Catalog!.ReleaseId}"
        : "PalacePal only";

    internal bool TryGetPresentation(
        int layoutIndex,
        int roomIndex,
        out DetailedMapRoomGraphPresentation presentation) =>
        _presentations.TryGetValue(
            (layoutIndex, roomIndex),
            out presentation!);
}

internal readonly record struct DetailedMapCatalogStatusSnapshot(
    bool ScenarioSupported,
    bool Enabled,
    bool Checking,
    bool HasValidCatalog,
    string? ReleaseId,
    int KnownSuccessorCount,
    int CandidateCount,
    DateTime? LastSuccessfulCheckUtc,
    string Message);

internal sealed class DetailedMapCatalogManager : IDisposable
{
    private const string CatalogDirectoryName = "DetailedMapCatalogs";
    private const string InstalledPointerFileName = "installed.json";
    private const string CheckStateFileName = "check-state.json";
    private const int MaximumLatestBytes = 16 * 1024;
    private const int MaximumCatalogBytes = 2 * 1024 * 1024;
    private const int MaximumSignatureBytes = 16 * 1024;
    private const int MaximumHoardYieldBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan SuccessfulCheckCooldown =
        TimeSpan.FromHours(24);
    private static readonly TimeSpan FailedCheckRetryDelay =
        TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions LocalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true
    };

    private readonly string _catalogConfigRoot;
    private string _catalogRoot;
    private string _loadedScenarioKey;
    private readonly Uri? _catalogBaseUri;
    private readonly bool _deleteCatalogsWhenDisabled;
    private readonly HttpClient? _httpClient;
    private readonly Dictionary<(int LayoutIndex, int RoomIndex), DetailedMapRoomGraphPresentation>
        _presentations = new();

    private DetailedMapCatalog? _currentCatalog;
    private HoardYieldCatalog? _currentHoardYield;
    private DetailedMapCatalogLatest? _installedPointer;
    private DetailedMapRunSnapshot? _activeRun;
    private Task<CatalogCheckResult>? _checkTask;
    private string? _checkScenarioKey;
    private DateTime? _lastSuccessfulCheckUtc;
    private DateTime _nextFailedCheckRetryUtc = DateTime.MinValue;
    private string? _latestEtag;
    private string _localLoadError = string.Empty;
    private string _hoardYieldLoadError = string.Empty;
    private string _lastCheckError = string.Empty;
    private bool _lastEnabled;
    private bool _disposed;

    internal DetailedMapCatalogManager(
        string pluginConfigDirectory,
        DetailedMapHostOptions hostOptions,
        HttpMessageHandler? httpMessageHandler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginConfigDirectory);
        ArgumentNullException.ThrowIfNull(hostOptions);

        string configRoot = Path.GetFullPath(pluginConfigDirectory);
        _catalogConfigRoot = configRoot;
        _loadedScenarioKey = DetailedMapEvidenceContract.PilgrimsTraverse21To30ScenarioKey;
        _catalogRoot = GetScenarioRoot(_loadedScenarioKey);
        string expectedPrefix =
            configRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!_catalogRoot.StartsWith(
                expectedPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Detailed-map catalog directory resolved outside the plugin configuration directory.");
        }

        _catalogBaseUri = hostOptions.CatalogBaseUri;
        _deleteCatalogsWhenDisabled = hostOptions.DeleteCatalogsWhenDisabled;
        if (_catalogBaseUri != null)
        {
            _httpClient = httpMessageHandler == null
                ? new HttpClient(new HttpClientHandler
                {
                    AutomaticDecompression =
                        DecompressionMethods.GZip |
                        DecompressionMethods.Deflate |
                        DecompressionMethods.Brotli
                }, disposeHandler: true)
                : new HttpClient(httpMessageHandler, disposeHandler: true);
            _httpClient.Timeout = TimeSpan.FromSeconds(20);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "DeepDungeonFsd/2.0");
        }
        else if (httpMessageHandler != null)
        {
            httpMessageHandler.Dispose();
            throw new ArgumentException(
                "An HTTP message handler cannot be supplied when no online catalog service is configured.",
                nameof(httpMessageHandler));
        }
        else
        {
            _httpClient = null;
        }

        LoadCheckState();
        ReloadInstalledCatalog();
    }

    internal bool HasOnlineCatalogService => _catalogBaseUri != null;

    internal static string? GetScenarioKey(int scenarioIndex) =>
        scenarioIndex switch
        {
            0 or 2 => DetailedMapScenarioCatalog.PilgrimsTraverse21To30.Key,
            1 => DetailedMapScenarioCatalog.PilgrimsTraverse31To40.Key,
            _ => null
        };

    internal DetailedMapCatalog? CurrentCatalog => _currentCatalog;
    internal DetailedMapCatalog? PresentationCatalog =>
        SelectPresentationCatalog(_activeRun, _currentCatalog);
    internal string? ActiveRunReleaseId => _activeRun?.ReleaseId;
    internal DetailedMapRunSnapshot? ActiveRunSnapshot => _activeRun;

    internal static DetailedMapCatalog? SelectPresentationCatalog(
        DetailedMapRunSnapshot? activeRun,
        DetailedMapCatalog? currentCatalog) =>
        activeRun != null ? activeRun.Catalog : currentCatalog;

    internal bool TryGetPresentation(
        int layoutIndex,
        int roomIndex,
        out DetailedMapRoomGraphPresentation presentation) =>
        _activeRun != null
            ? _activeRun.TryGetPresentation(
                layoutIndex,
                roomIndex,
                out presentation)
            : _presentations.TryGetValue(
                (layoutIndex, roomIndex),
                out presentation!);

    internal void Update(
        bool enabled,
        string? selectedScenarioKey,
        bool runActive)
    {
        ThrowIfDisposed();

        EnsureScenarioLoaded(selectedScenarioKey);

        if (_activeRun != null && !runActive)
            ReleaseRunSnapshot();

        CompleteCatalogCheck(enabled, selectedScenarioKey);

        if (!enabled)
        {
            if (_lastEnabled)
                _nextFailedCheckRetryUtc = DateTime.MinValue;
            _lastEnabled = false;
            if (_deleteCatalogsWhenDisabled && _activeRun == null)
                DeleteDownloadedCatalogs();
            return;
        }

        if (!_lastEnabled)
            _nextFailedCheckRetryUtc = DateTime.MinValue;
        _lastEnabled = true;

        if (_catalogBaseUri == null ||
            _httpClient == null ||
            !IsSupportedScenario(selectedScenarioKey) ||
            _checkTask != null ||
            DateTime.UtcNow < _nextFailedCheckRetryUtc ||
            !ShouldCheckForUpdate())
        {
            return;
        }

        bool installedReleaseComplete = IsInstalledReleaseComplete(
            _currentCatalog,
            _currentHoardYield);
        _checkScenarioKey = selectedScenarioKey;
        _checkTask = CheckForUpdateAsync(
            selectedScenarioKey!,
            installedReleaseComplete ? _installedPointer : null,
            installedReleaseComplete ? _latestEtag : null,
            CancellationToken.None);
    }

    internal bool TryAcquireRunSnapshot(
        bool useDetailedMap,
        string? scenarioKey,
        out DetailedMapRunSnapshot snapshot,
        out string error)
    {
        ThrowIfDisposed();
        EnsureScenarioLoaded(scenarioKey);
        CompleteCatalogCheck(useDetailedMap, scenarioKey);

        if (_activeRun != null)
        {
            snapshot = null!;
            error = "A detailed-map run snapshot is already active.";
            return false;
        }

        if (!useDetailedMap || !IsSupportedScenario(scenarioKey))
        {
            snapshot = new DetailedMapRunSnapshot(
                DetailedMapRuntimePolicy.PalacePalOnly,
                scenarioKey,
                null);
            _activeRun = snapshot;
            error = string.Empty;
            return true;
        }

        if (_catalogBaseUri == null)
        {
            snapshot = null!;
            error = DetailedMapHostOptions.NoOnlineCatalogServiceMessage;
            return false;
        }

        if (_currentCatalog == null)
        {
            snapshot = null!;
            error = _checkTask != null
                ? "The detailed-map data version is still downloading. Please retry when its status is ready."
                : !string.IsNullOrWhiteSpace(_lastCheckError)
                    ? $"The detailed-map data version is unavailable: {_lastCheckError}"
                    : !string.IsNullOrWhiteSpace(_localLoadError)
                        ? $"The detailed-map data version is unavailable: {_localLoadError}"
                        : "The detailed-map data version has not been downloaded yet.";
            return false;
        }

        snapshot = new DetailedMapRunSnapshot(
            DetailedMapRuntimePolicy.DetailedMap,
            scenarioKey,
            _currentCatalog,
            _presentations,
            _currentHoardYield);
        _activeRun = snapshot;
        error = string.Empty;
        return true;
    }

    internal void ReleaseRunSnapshot()
    {
        _activeRun = null;
        if (_deleteCatalogsWhenDisabled && !_lastEnabled)
            DeleteDownloadedCatalogs();
    }

    internal DetailedMapCatalogStatusSnapshot GetStatus(
        bool enabled,
        string? selectedScenarioKey)
    {
        EnsureScenarioLoaded(selectedScenarioKey);
        bool supported = IsSupportedScenario(selectedScenarioKey);
        string message;
        if (_catalogBaseUri == null)
        {
            message = DetailedMapHostOptions.NoOnlineCatalogServiceMessage;
        }
        else if (!supported)
        {
            message = "Detailed map is not available for this scenario.";
        }
        else if (_checkTask != null)
        {
            message = _currentCatalog == null
                ? "Downloading and verifying the detailed-map data version..."
                : $"Checking for updates; data version {_currentCatalog.ReleaseId} remains available.";
        }
        else if (_currentCatalog != null)
        {
            message = string.IsNullOrWhiteSpace(_lastCheckError)
                ? $"Data version {_currentCatalog.ReleaseId} is ready."
                : $"Data version {_currentCatalog.ReleaseId} is ready; the latest update check failed: {_lastCheckError}";
        }
        else if (!string.IsNullOrWhiteSpace(_lastCheckError))
        {
            message = $"Detailed-map update failed: {_lastCheckError}";
        }
        else if (!string.IsNullOrWhiteSpace(_localLoadError))
        {
            message = $"Local detailed-map data version is invalid: {_localLoadError}";
        }
        else
        {
            message = enabled
                ? "No detailed-map data version is installed yet."
                : "Detailed map is disabled.";
        }

        CountCoverage(
            _currentCatalog,
            out int knownSuccessors,
            out int candidates);
        return new DetailedMapCatalogStatusSnapshot(
            supported,
            enabled,
            _checkTask != null,
            _currentCatalog != null,
            _currentCatalog?.ReleaseId,
            knownSuccessors,
            candidates,
            _lastSuccessfulCheckUtc,
            message);
    }

    private bool ShouldCheckForUpdate()
    {
        if (!IsInstalledReleaseComplete(
                _currentCatalog,
                _currentHoardYield))
            return true;
        return !_lastSuccessfulCheckUtc.HasValue ||
               DateTime.UtcNow - _lastSuccessfulCheckUtc.Value >=
               SuccessfulCheckCooldown;
    }

    internal static bool IsInstalledReleaseComplete(
        DetailedMapCatalog? catalog,
        HoardYieldCatalog? hoardYield) =>
        catalog != null &&
        (catalog.HoardYieldSha256 == null || hoardYield != null);

    private void CompleteCatalogCheck(
        bool enabled,
        string? selectedScenarioKey)
    {
        Task<CatalogCheckResult>? task = _checkTask;
        if (task == null || !task.IsCompleted)
            return;

        string? taskScenarioKey = _checkScenarioKey;
        _checkTask = null;
        _checkScenarioKey = null;

        CatalogCheckResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            _lastCheckError = error.Message;
            _nextFailedCheckRetryUtc =
                DateTime.UtcNow + FailedCheckRetryDelay;
            Service.Log.Error(
                $"[DetailedMapCatalog] Update check failed: {error}");
            return;
        }

        if (!enabled ||
            !string.Equals(
                taskScenarioKey,
                selectedScenarioKey,
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (result.Kind == CatalogCheckResultKind.Updated)
            {
                InstallCatalog(
                    result.Latest!,
                    result.CatalogBytes!,
                    result.SignatureBytes!,
                    result.HoardYieldBytes);
                ReloadInstalledCatalog();
                if (_currentCatalog == null)
                {
                    throw new InvalidDataException(
                        "The installed detailed-map catalog could not be reloaded.");
                }
            }

            _latestEtag = result.Etag ?? _latestEtag;
            _lastSuccessfulCheckUtc = DateTime.UtcNow;
            _lastCheckError = string.Empty;
            _nextFailedCheckRetryUtc = DateTime.MinValue;
            SaveCheckState();
            Service.Log.Info(
                result.Kind == CatalogCheckResultKind.Updated
                    ? $"[DetailedMapCatalog] Installed release {_currentCatalog!.ReleaseId}."
                    : $"[DetailedMapCatalog] Catalog {_currentCatalog!.ReleaseId} is current.");
        }
        catch (Exception error) when (
            error is IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
            _lastCheckError = error.Message;
            _nextFailedCheckRetryUtc =
                DateTime.UtcNow + FailedCheckRetryDelay;
            Service.Log.Error(
                $"[DetailedMapCatalog] Failed to install verified catalog: {error}");
        }
    }

    private async Task<CatalogCheckResult> CheckForUpdateAsync(
        string scenarioKey,
        DetailedMapCatalogLatest? installedPointer,
        string? etag,
        CancellationToken cancellationToken)
    {
        if (_catalogBaseUri == null || _httpClient == null)
        {
            throw new InvalidOperationException(
                DetailedMapHostOptions.NoOnlineCatalogServiceMessage);
        }

        Uri latestUri = new(
            _catalogBaseUri,
            $"{scenarioKey}/{DetailedMapCatalogContract.LatestFileName}");
        using var request = new HttpRequestMessage(HttpMethod.Get, latestUri);
        if (installedPointer != null &&
            !string.IsNullOrWhiteSpace(etag) &&
            EntityTagHeaderValue.TryParse(
                etag,
                out EntityTagHeaderValue? parsedEtag))
        {
            request.Headers.IfNoneMatch.Add(parsedEtag);
        }

        using HttpResponseMessage response = await _httpClient
            .SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (installedPointer == null)
            {
                throw new InvalidDataException(
                    "The catalog endpoint returned 304 without a valid local catalog.");
            }
            return CatalogCheckResult.NotModified(
                response.Headers.ETag?.ToString());
        }
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"Catalog latest request returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        byte[] latestBytes = await ReadBoundedAsync(
                response,
                MaximumLatestBytes,
                cancellationToken)
            .ConfigureAwait(false);
        DetailedMapCatalogLatest latest =
            DetailedMapCatalogContract.ParseLatest(latestBytes);
        ValidateLatestPaths(latest, scenarioKey);

        if (installedPointer != null &&
            string.Equals(
                installedPointer.ReleaseId,
                latest.ReleaseId,
                StringComparison.Ordinal) &&
            string.Equals(
                installedPointer.CatalogSha256,
                latest.CatalogSha256,
                StringComparison.Ordinal))
        {
            return CatalogCheckResult.NotModified(
                response.Headers.ETag?.ToString());
        }

        byte[] catalogBytes = await GetBoundedAsync(
                new Uri(_catalogBaseUri, latest.CatalogPath),
                MaximumCatalogBytes,
                cancellationToken)
            .ConfigureAwait(false);
        byte[] signatureBytes = await GetBoundedAsync(
                new Uri(_catalogBaseUri, latest.SignaturePath),
                MaximumSignatureBytes,
                cancellationToken)
            .ConfigureAwait(false);
        DetailedMapCatalog downloadedCatalog = ValidateDownloadedCatalog(
            latest,
            catalogBytes,
            signatureBytes,
            scenarioKey);
        byte[]? hoardYieldBytes = null;
        if (downloadedCatalog.HoardYieldSha256 != null)
        {
            string yieldPath =
                $"{latest.ScenarioKey}/{latest.ReleaseId}/{HoardYieldCatalogContract.FileName}";
            hoardYieldBytes = await GetBoundedAsync(
                    new Uri(_catalogBaseUri, yieldPath),
                    MaximumHoardYieldBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateHoardYield(downloadedCatalog, hoardYieldBytes);
        }
        return CatalogCheckResult.Updated(
            latest,
            catalogBytes,
            signatureBytes,
            hoardYieldBytes,
            response.Headers.ETag?.ToString());
    }

    private async Task<byte[]> GetBoundedAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (_httpClient == null)
        {
            throw new InvalidOperationException(
                DetailedMapHostOptions.NoOnlineCatalogServiceMessage);
        }

        using HttpResponseMessage response = await _httpClient
            .GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"Catalog object request returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
        return await ReadBoundedAsync(
                response,
                maximumBytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        long? declaredContentLength =
            response.Content.Headers.ContentLength;
        if (declaredContentLength > maximumBytes)
        {
            throw new InvalidDataException(
                $"Catalog response exceeds the {maximumBytes}-byte limit.");
        }

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream(
            declaredContentLength.HasValue &&
            declaredContentLength.Value > 0 &&
            declaredContentLength.Value <= maximumBytes
                ? (int)declaredContentLength.Value
                : Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException(
                    $"Catalog response exceeds the {maximumBytes}-byte limit.");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private void InstallCatalog(
        DetailedMapCatalogLatest latest,
        byte[] catalogBytes,
        byte[] signatureBytes,
        byte[]? hoardYieldBytes)
    {
        DetailedMapCatalog catalog = ValidateDownloadedCatalog(
            latest,
            catalogBytes,
            signatureBytes,
            latest.ScenarioKey);
        if (catalog.HoardYieldSha256 != null)
        {
            if (hoardYieldBytes == null)
                throw new InvalidDataException("Signed catalog requires a hoard-yield artifact.");
            ValidateHoardYield(catalog, hoardYieldBytes);
        }

        string releasesDirectory = Path.Combine(_catalogRoot, "releases");
        Directory.CreateDirectory(releasesDirectory);
        string releaseDirectory = GetReleaseDirectory(latest);
        bool releaseReady = false;
        if (Directory.Exists(releaseDirectory))
        {
            try
            {
                ValidateDownloadedCatalog(
                    latest,
                    File.ReadAllBytes(Path.Combine(
                        releaseDirectory,
                        DetailedMapCatalogContract.CatalogFileName)),
                    File.ReadAllBytes(Path.Combine(
                        releaseDirectory,
                        DetailedMapCatalogContract.SignatureFileName)),
                    latest.ScenarioKey);
                if (catalog.HoardYieldSha256 != null)
                {
                    ValidateHoardYield(
                        catalog,
                        File.ReadAllBytes(Path.Combine(
                            releaseDirectory,
                            HoardYieldCatalogContract.FileName)));
                }
                releaseReady = true;
            }
            catch (Exception error) when (
                error is IOException or
                UnauthorizedAccessException or
                InvalidDataException)
            {
                Directory.Delete(releaseDirectory, recursive: true);
            }
        }

        if (!releaseReady)
        {
            string temporaryDirectory = Path.Combine(
                releasesDirectory,
                $".{latest.ReleaseId}.{Guid.NewGuid():N}.tmp");
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                File.WriteAllBytes(
                    Path.Combine(
                        temporaryDirectory,
                        DetailedMapCatalogContract.CatalogFileName),
                    catalogBytes);
                File.WriteAllBytes(
                    Path.Combine(
                        temporaryDirectory,
                        DetailedMapCatalogContract.SignatureFileName),
                    signatureBytes);
                if (hoardYieldBytes != null)
                {
                    File.WriteAllBytes(
                        Path.Combine(
                            temporaryDirectory,
                            HoardYieldCatalogContract.FileName),
                        hoardYieldBytes);
                }
                Directory.Move(temporaryDirectory, releaseDirectory);
            }
            catch
            {
                try
                {
                    if (Directory.Exists(temporaryDirectory))
                    {
                        Directory.Delete(
                            temporaryDirectory,
                            recursive: true);
                    }
                }
                catch
                {
                }
                throw;
            }
        }

        WriteAtomic(
            Path.Combine(_catalogRoot, InstalledPointerFileName),
            DetailedMapCatalogContract.SerializeLatest(latest));
    }

    private void ReloadInstalledCatalog()
    {
        _currentCatalog = null;
        _currentHoardYield = null;
        _installedPointer = null;
        _presentations.Clear();
        _localLoadError = string.Empty;
        _hoardYieldLoadError = string.Empty;

        string pointerPath = Path.Combine(
            _catalogRoot,
            InstalledPointerFileName);
        if (!File.Exists(pointerPath))
            return;

        try
        {
            DetailedMapCatalogLatest latest =
                DetailedMapCatalogContract.ParseLatest(
                    File.ReadAllBytes(pointerPath));
            ValidateLatestPaths(latest, latest.ScenarioKey);
            string releaseDirectory = GetReleaseDirectory(latest);
            byte[] catalogBytes = File.ReadAllBytes(
                Path.Combine(
                    releaseDirectory,
                    DetailedMapCatalogContract.CatalogFileName));
            byte[] signatureBytes = File.ReadAllBytes(
                Path.Combine(
                    releaseDirectory,
                    DetailedMapCatalogContract.SignatureFileName));
            DetailedMapCatalog catalog = ValidateDownloadedCatalog(
                latest,
                catalogBytes,
                signatureBytes,
                latest.ScenarioKey);
            if (catalog.HoardYieldSha256 != null)
            {
                try
                {
                    byte[] yieldBytes = File.ReadAllBytes(Path.Combine(
                        releaseDirectory,
                        HoardYieldCatalogContract.FileName));
                    _currentHoardYield = ValidateHoardYield(catalog, yieldBytes);
                }
                catch (Exception error) when (
                    error is IOException or
                    UnauthorizedAccessException or
                    InvalidDataException or
                    JsonException)
                {
                    _hoardYieldLoadError = error.Message;
                    Service.Log.Warning(
                        $"[DetailedMapCatalog] Hoard-yield artifact unavailable: {error.Message}");
                }
            }
            catalog.WarmRoomLookup();
            foreach (DetailedMapCatalogRoom room in catalog.Rooms)
            {
                _presentations.Add(
                    (room.LayoutIndex, room.RoomIndex),
                    DetailedMapRoomGraphAnalyzer.Analyze(room));
            }

            _currentCatalog = catalog;
            _installedPointer = latest;
            Service.Log.Info(
                $"[DetailedMapCatalog] Loaded release {catalog.ReleaseId}; rooms={catalog.Rooms.Length}.");
        }
        catch (Exception error) when (
            error is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            JsonException)
        {
            _localLoadError = error.Message;
            Service.Log.Error(
                $"[DetailedMapCatalog] Local catalog is invalid: {error}");
        }
    }

    private static DetailedMapCatalog ValidateDownloadedCatalog(
        DetailedMapCatalogLatest latest,
        ReadOnlySpan<byte> catalogBytes,
        ReadOnlySpan<byte> signatureBytes,
        string expectedScenarioKey)
    {
        ValidateLatestPaths(latest, expectedScenarioKey);
        DetailedMapCatalog catalog =
            DetailedMapCatalogContract.Parse(catalogBytes);
        DetailedMapCatalogSignature signature =
            DetailedMapCatalogContract.ParseSignature(signatureBytes);
        string digest =
            DetailedMapCatalogContract.ComputeSha256(catalogBytes);
        if (!string.Equals(
                latest.CatalogSha256,
                digest,
                StringComparison.Ordinal) ||
            !string.Equals(
                signature.CatalogSha256,
                digest,
                StringComparison.Ordinal) ||
            !string.Equals(
                catalog.ScenarioKey,
                latest.ScenarioKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                catalog.ReleaseId,
                latest.ReleaseId,
                StringComparison.Ordinal) ||
            !string.Equals(
                catalog.ModelSha256,
                latest.ModelSha256,
                StringComparison.Ordinal) ||
            !DetailedMapCatalogTrust.Verify(catalogBytes, signature))
        {
            throw new InvalidDataException(
                "Detailed-map catalog hash, scope, release, or signature validation failed.");
        }
        return catalog;
    }

    private static HoardYieldCatalog ValidateHoardYield(
        DetailedMapCatalog catalog,
        ReadOnlySpan<byte> bytes)
    {
        if (catalog.HoardYieldSha256 == null ||
            !string.Equals(
                catalog.HoardYieldSha256,
                HoardYieldCatalogContract.ComputeSha256(bytes),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Hoard-yield artifact hash validation failed.");
        }
        HoardYieldCatalog yield = HoardYieldCatalogContract.Parse(bytes);
        HoardYieldCatalogContract.ValidateCompatibility(catalog, yield);
        return yield;
    }

    private static void ValidateLatestPaths(
        DetailedMapCatalogLatest latest,
        string expectedScenarioKey)
    {
        if (!string.Equals(
                latest.ScenarioKey,
                expectedScenarioKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Detailed-map latest pointer has the wrong scenario.");
        }

        string expectedCatalogPath =
            $"{latest.ScenarioKey}/{latest.ReleaseId}/{DetailedMapCatalogContract.CatalogFileName}";
        string expectedSignaturePath =
            $"{latest.ScenarioKey}/{latest.ReleaseId}/{DetailedMapCatalogContract.SignatureFileName}";
        if (!string.Equals(
                latest.CatalogPath,
                expectedCatalogPath,
                StringComparison.Ordinal) ||
            !string.Equals(
                latest.SignaturePath,
                expectedSignaturePath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Detailed-map latest pointer contains unexpected object paths.");
        }
    }

    private string GetReleaseDirectory(
        DetailedMapCatalogLatest latest) =>
        Path.Combine(
            _catalogRoot,
            "releases",
            $"{latest.ReleaseId}-{latest.CatalogSha256[..12]}");

    private void DeleteDownloadedCatalogs()
    {
        string catalogParent = GetValidatedCatalogParent();
        string catalogParentPrefix =
            catalogParent.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        bool deletedAny = false;
        try
        {
            foreach (DetailedMapScenarioDefinition scenario in
                     DetailedMapScenarioCatalog.Definitions)
            {
                string scenarioRoot = GetScenarioRoot(scenario.Key);
                if (!scenarioRoot.StartsWith(
                        catalogParentPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Detailed-map scenario catalog resolved outside its validated parent.");
                }
                if (!Directory.Exists(scenarioRoot))
                    continue;

                Directory.Delete(scenarioRoot, recursive: true);
                deletedAny = true;
            }
            ClearLocalCatalogState();
            if (deletedAny)
            {
                Service.Log.Info(
                    "[DetailedMapCatalog] Downloaded catalogs were removed because detailed map was disabled.");
            }
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException)
        {
            _lastCheckError =
                $"Downloaded detailed-map catalogs could not be removed: {error.Message}";
            Service.Log.Error(
                $"[DetailedMapCatalog] {_lastCheckError}");
        }
    }

    private void ClearLocalCatalogState()
    {
        _currentCatalog = null;
        _currentHoardYield = null;
        _installedPointer = null;
        _presentations.Clear();
        _lastSuccessfulCheckUtc = null;
        _latestEtag = null;
        _localLoadError = string.Empty;
        _hoardYieldLoadError = string.Empty;
    }

    private void LoadCheckState()
    {
        string path = Path.Combine(_catalogRoot, CheckStateFileName);
        if (!File.Exists(path))
            return;

        try
        {
            LocalCheckState? state = JsonSerializer.Deserialize<LocalCheckState>(
                File.ReadAllBytes(path),
                LocalJsonOptions);
            if (state == null)
                throw new InvalidDataException("Catalog check state is empty.");
            _lastSuccessfulCheckUtc =
                state.LastSuccessfulCheckUtc?.ToUniversalTime();
            _latestEtag = state.LatestEtag;
        }
        catch (Exception error) when (
            error is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException)
        {
            Service.Log.Warning(
                $"[DetailedMapCatalog] Ignoring invalid local check state: {error.Message}");
        }
    }

    private void SaveCheckState()
    {
        var state = new LocalCheckState
        {
            LastSuccessfulCheckUtc = _lastSuccessfulCheckUtc,
            LatestEtag = _latestEtag
        };
        WriteAtomic(
            Path.Combine(_catalogRoot, CheckStateFileName),
            JsonSerializer.SerializeToUtf8Bytes(state, LocalJsonOptions));
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidDataException("Atomic-write path has no directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, path, overwrite: true);
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

    private static bool IsSupportedScenario(string? scenarioKey) =>
        DetailedMapScenarioCatalog.TryGetByKey(scenarioKey, out _);

    private string GetValidatedCatalogParent()
    {
        string parent = Path.GetFullPath(Path.Combine(
            _catalogConfigRoot,
            CatalogDirectoryName));
        string configPrefix =
            _catalogConfigRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!parent.StartsWith(
                configPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Detailed-map catalog parent resolved outside the plugin configuration directory.");
        }
        return parent;
    }

    private string GetScenarioRoot(string scenarioKey) =>
        Path.GetFullPath(Path.Combine(
            _catalogConfigRoot,
            CatalogDirectoryName,
            scenarioKey));

    private void EnsureScenarioLoaded(string? scenarioKey)
    {
        if (!IsSupportedScenario(scenarioKey) ||
            string.Equals(_loadedScenarioKey, scenarioKey, StringComparison.Ordinal))
        {
            return;
        }
        if (_activeRun != null)
        {
            throw new InvalidOperationException(
                "The detailed-map scenario cannot change while a run snapshot is active.");
        }

        _loadedScenarioKey = scenarioKey!;
        _catalogRoot = GetScenarioRoot(_loadedScenarioKey);
        ClearLocalCatalogState();
        _lastCheckError = string.Empty;
        _nextFailedCheckRetryUtc = DateTime.MinValue;
        LoadCheckState();
        ReloadInstalledCatalog();
    }

    private static void CountCoverage(
        DetailedMapCatalog? catalog,
        out int knownSuccessors,
        out int candidates)
    {
        knownSuccessors = 0;
        candidates = 0;
        if (catalog == null)
            return;

        foreach (DetailedMapCatalogRoom room in catalog.Rooms)
        {
            candidates += room.Candidates.Length;
            foreach (DetailedMapCatalogCandidate candidate in room.Candidates)
            {
                if (candidate.Successor.State ==
                    DetailedMapSuccessorState.ObservedUnique)
                {
                    knownSuccessors++;
                }
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _activeRun = null;
        _httpClient?.Dispose();
        _disposed = true;
    }

    private sealed class LocalCheckState
    {
        public DateTime? LastSuccessfulCheckUtc { get; init; }
        public string? LatestEtag { get; init; }
    }

    private enum CatalogCheckResultKind
    {
        NotModified,
        Updated
    }

    private sealed record CatalogCheckResult(
        CatalogCheckResultKind Kind,
        DetailedMapCatalogLatest? Latest,
        byte[]? CatalogBytes,
        byte[]? SignatureBytes,
        byte[]? HoardYieldBytes,
        string? Etag)
    {
        internal static CatalogCheckResult NotModified(string? etag) =>
            new(
                CatalogCheckResultKind.NotModified,
                null,
                null,
                null,
                null,
                etag);

        internal static CatalogCheckResult Updated(
            DetailedMapCatalogLatest latest,
            byte[] catalogBytes,
            byte[] signatureBytes,
            byte[]? hoardYieldBytes,
            string? etag) =>
            new(
                CatalogCheckResultKind.Updated,
                latest,
                catalogBytes,
                signatureBytes,
                hoardYieldBytes,
                etag);
    }
}
