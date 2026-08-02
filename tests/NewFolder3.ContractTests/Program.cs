using System.Net;
using System.Reflection;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.GameState;

namespace NewFolder3;

internal static class Program
{
    public static int Main()
    {
        int passed = 0;
        passed += Run("public-no-service-build-profile", TestPublicNoServiceBuildProfile);
        passed += Run("public-allow-all-access-gate", TestPublicAllowAllAccessGate);
        passed += Run("detailed-map-host-options-no-service", TestDetailedMapHostOptionsNoService);
        passed += Run("catalog-manager-no-http-when-unavailable", TestCatalogManagerNoHttpWhenUnavailable);
        passed += Run("community-evidence-upload-contract", TestCommunityEvidenceUploadContract);
        Console.WriteLine($"NewFolder3 contract tests: {passed} passed");
        return 0;
    }

    private static int Run(string name, Action test)
    {
        test();
        Console.WriteLine($"  ok  {name}");
        return 1;
    }

    private static void TestPublicNoServiceBuildProfile()
    {
        NewFolder3BuildCapabilities capabilities = NewFolder3BuildProfile.Capabilities;
        Assert(
            capabilities.DetailedMapCatalogEndpoint == null &&
            capabilities.CommunityEvidenceEndpoint == null &&
            !capabilities.HasOnlineCatalogService &&
            !capabilities.HasCommunityEvidenceUpload &&
            !capabilities.ContributesAnonymousEvidence &&
            !capabilities.SupportsControlledPtSurvey &&
            capabilities.CreateAccessGate == null,
            "Public build profile must resolve to an explicit no-service configuration.");

        DetailedMapHostOptions options = NewFolder3BuildProfile.CreateDetailedMapHostOptions();
        Assert(
            !options.HasOnlineCatalogService &&
            options.CatalogBaseUri == null &&
            !options.ContributesAnonymousEvidence,
            "Public build profile must create DetailedMapHostOptions without a catalog URI.");
    }

    private static void TestPublicAllowAllAccessGate()
    {
        var gate = new NewFolder3AllowAllAccessGate();
        Assert(
            gate.Current.IsAllowed &&
            string.IsNullOrEmpty(gate.DenialInstruction) &&
            gate.TryAuthorizeFsdStart(out string error) &&
            error.Length == 0,
            "Public access gate must allow FSD start authorization.");
        Assert(
            NewFolder3FsdPageAccess.CanShowFsdPage(gate.Current),
            "Public allow-all decisions must show the FSD page.");
        Assert(
            !NewFolder3FsdPageAccess.CanShowFsdPage(
                NewFolder3AccessDecision.Denied("contract-denied")),
            "Denied decisions must hide the FSD page.");
    }

    private static void TestDetailedMapHostOptionsNoService()
    {
        var options = new DetailedMapHostOptions(
            catalogBaseUri: null,
            contributesAnonymousEvidence: false,
            deleteCatalogsWhenDisabled: false,
            supportsControlledPtSurvey: false);
        Assert(
            !options.HasOnlineCatalogService &&
            options.CatalogBaseUri == null,
            "Null catalog URI must mean no online catalog service.");

        bool rejected = false;
        try
        {
            _ = new DetailedMapHostOptions(
                catalogBaseUri: null,
                contributesAnonymousEvidence: true,
                deleteCatalogsWhenDisabled: false,
                supportsControlledPtSurvey: false);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        Assert(
            rejected,
            "Anonymous evidence contribution must require an online catalog service.");
    }

    private static void TestCatalogManagerNoHttpWhenUnavailable()
    {
        string configRoot = Path.Combine(
            Path.GetTempPath(),
            $"newfolder3-catalog-noservice-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(configRoot);
            var options = new DetailedMapHostOptions(
                catalogBaseUri: null,
                contributesAnonymousEvidence: false,
                deleteCatalogsWhenDisabled: false,
                supportsControlledPtSurvey: false);
            using var manager = new DetailedMapCatalogManager(configRoot, options);
            Assert(
                !manager.HasOnlineCatalogService,
                "Catalog manager must report no online catalog service.");

            string scenarioKey =
                DetailedMapEvidenceContract.PilgrimsTraverse21To30ScenarioKey;
            manager.Update(
                enabled: true,
                scenarioKey,
                runActive: false);
            DetailedMapCatalogStatusSnapshot status = manager.GetStatus(
                enabled: true,
                scenarioKey);
            Assert(
                status.Message == DetailedMapHostOptions.NoOnlineCatalogServiceMessage &&
                !status.Checking &&
                !status.HasValidCatalog,
                "Unavailable catalog service must surface the no-service status without checking.");

            Assert(
                !manager.TryAcquireRunSnapshot(
                    useDetailedMap: true,
                    scenarioKey,
                    out _,
                    out string detailedError) &&
                detailedError == DetailedMapHostOptions.NoOnlineCatalogServiceMessage,
                "Detailed-map acquisition must fail explicitly when no service is configured.");
            Assert(
                manager.TryAcquireRunSnapshot(
                    useDetailedMap: false,
                    scenarioKey,
                    out DetailedMapRunSnapshot palacePal,
                    out string palacePalError) &&
                palacePal.Policy == DetailedMapRuntimePolicy.PalacePalOnly &&
                palacePalError.Length == 0,
                "PalacePal-only acquisition must remain available without an online catalog service.");
            manager.ReleaseRunSnapshot();
        }
        finally
        {
            if (Directory.Exists(configRoot))
                Directory.Delete(configRoot, recursive: true);
        }
    }

    private static void TestCommunityEvidenceUploadContract()
    {
        string configRoot = Path.Combine(
            Path.GetTempPath(),
            $"newfolder3-evidence-contract-{Guid.NewGuid():N}");
        try
        {
            Assert(
                !NewFolder3BuildProfile.Capabilities.HasCommunityEvidenceUpload,
                "Public builds must not configure a community evidence upload endpoint.");

            string pendingDirectory = Path.Combine(
                configRoot,
                "DetailedMapEvidence",
                "pending");
            Directory.CreateDirectory(pendingDirectory);
            DetailedMapEvidenceBatch batch =
                DetailedMapEvidenceContract.CreateCanonicalBatch(
                    [CreateNoHoardEvidence()]);
            byte[] payload = DetailedMapEvidenceContract.SerializeCanonical(batch);
            string pendingPath = Path.Combine(
                pendingDirectory,
                $"{DetailedMapEvidenceContract.ComputeBatchId(payload)}.json");
            File.WriteAllBytes(pendingPath, payload);

            var handler = new GatedAcceptedHandler();
            using var collector = new CommunityEvidenceCollector(
                configRoot,
                "contract-installation-token",
                new Uri("https://evidence.invalid/v1/evidence"),
                DispatchProxy.Create<IPluginLog, NoOpDispatchProxy>(),
                handler);

            try
            {
                collector.ObserveRunState(
                    isRunActive: true,
                    detailedMapEnabled: true,
                    catalogReleaseUsed: "contract-release");
                Assert(
                    handler.WaitUntilRequested(TimeSpan.FromSeconds(2)),
                    "The existing pending evidence batch was not selected for upload.");
                collector.ObserveRunState(
                    isRunActive: true,
                    detailedMapEnabled: false,
                    catalogReleaseUsed: null);
                collector.OnFloorEvidencePersisted(CreateCurrentRunEvidence());
            }
            finally
            {
                handler.AllowFirstResponse();
            }
            Assert(
                SpinWait.SpinUntil(
                    () => handler.RequestCount == 2 &&
                          !Directory.EnumerateFiles(
                              pendingDirectory,
                              "*.json",
                              SearchOption.TopDirectoryOnly).Any(),
                    TimeSpan.FromSeconds(2)),
                "Disabling detailed map prevented pending and current-run evidence from completing upload.");
            DetailedMapEvidenceBatch secondRequest =
                DetailedMapEvidenceContract.Parse(handler.GetRequestPayload(1));
            Assert(
                secondRequest.Floors is
                    [{ FloorInstanceId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }],
                "The second request was not the current-run evidence captured after detailed map was disabled.");
        }
        finally
        {
            if (Directory.Exists(configRoot))
                Directory.Delete(configRoot, recursive: true);
        }
    }

    private static DetailedMapFloorEvidence CreateNoHoardEvidence() =>
        new()
        {
            CollectorVersion = "contract",
            ScenarioKey =
                DetailedMapEvidenceContract.PilgrimsTraverse21To30ScenarioKey,
            FloorInstanceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Floor = 21,
            TerritoryId =
                DetailedMapEvidenceContract.PilgrimsTraverse21To30TerritoryId,
            ActiveLayoutIndex = 0,
            AcquisitionMode =
                FloorEvidenceAcquisitionMode.AutomaticCommunitySurvey,
            RoomBindings =
            [
                new DetailedMapRoomBinding
                {
                    RoomIndex = 0,
                    RoomCenter = new RawWorldPosition(0f, 0f, 0f)
                }
            ],
            Terminal = new DetailedMapTerminalObservation
            {
                State = DetailedMapTerminalState.NoHoard,
                Reason = "intuition-no-hoard"
            },
            Intuition = new DetailedMapIntuitionObservation
            {
                State = DetailedMapIntuitionState.NoHoard
            },
            TrapScan = new DetailedMapTrapScanObservation
            {
                State = DetailedMapTrapScanState.NotAttempted,
                RevealSource = DetailedMapRevealSource.None
            },
            PairEligibility = new DetailedMapPairEligibility
            {
                Eligible = false,
                JointScanComplete = false,
                Reason = "no-hoard"
            }
        };

    private static FloorEvidenceBundle CreateCurrentRunEvidence() =>
        new()
        {
            CollectorVersion = "contract",
            FloorInstanceId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            DungeonId = DetailedMapEvidenceContract.PilgrimsTraverseDungeonId,
            Floor = DetailedMapEvidenceContract.FirstCoveredFloor,
            FloorSetStart = DetailedMapEvidenceContract.FirstCoveredFloor,
            TerritoryId =
                DetailedMapEvidenceContract.PilgrimsTraverse21To30TerritoryId,
            ActiveLayoutIndex = 0,
            AcquisitionMode =
                FloorEvidenceAcquisitionMode.ControlledReusableSaveSurvey,
            RoomBindings =
            [
                new FloorRoomBinding
                {
                    RoomIndex = 0,
                    RoomCenter = new RawWorldPosition(0f, 0f, 0f)
                }
            ],
            ControlledSurvey = new ControlledSurveyObservation
            {
                FloorRole = ControlledSurveyFloorRole.SelectedTarget,
                Outcome =
                    ControlledPtSurveyTargetOutcome.InheritedNoHoardInferred
            }
        };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class GatedAcceptedHandler : HttpMessageHandler
    {
        private readonly ManualResetEventSlim _requested = new(false);
        private readonly TaskCompletionSource _allowFirstResponse = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _payloadLock = new();
        private readonly List<byte[]> _requestPayloads = [];
        private int _requestCount;

        internal int RequestCount => Volatile.Read(ref _requestCount);

        internal bool WaitUntilRequested(TimeSpan timeout) =>
            _requested.Wait(timeout);

        internal void AllowFirstResponse() => _allowFirstResponse.TrySetResult();

        internal byte[] GetRequestPayload(int index)
        {
            lock (_payloadLock)
                return _requestPayloads[index];
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            byte[] payload = await request.Content!
                .ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            lock (_payloadLock)
                _requestPayloads.Add(payload);
            int requestCount = Interlocked.Increment(ref _requestCount);
            _requested.Set();
            if (requestCount == 1)
            {
                await _allowFirstResponse.Task.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                RequestMessage = request
            };
        }
    }

    private class NoOpDispatchProxy : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args) =>
            targetMethod?.ReturnType == typeof(void)
                ? null
                : targetMethod?.ReturnType.IsValueType == true
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
    }
}
