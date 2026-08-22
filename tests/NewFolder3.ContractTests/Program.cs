using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;

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
        passed += Run("community-usage-scenario-index-mapping", TestCommunityUsageScenarioIndexMapping);
        passed += Run("community-usage-telemetry-contract", TestCommunityUsageTelemetryContract);
        passed += Run("community-long-run-log-contract", TestCommunityLongRunLogContract);
        passed += Run("public-manifest-and-release-feed-roots", TestPublicManifestAndReleaseFeedRoots);
        Console.WriteLine($"NewFolder3 contract tests: {passed} passed");
        return 0;
    }

    private static int Run(string name, Action test)
    {
        test();
        Console.WriteLine($"  ok  {name}");
        return 1;
    }

    private static void TestPublicManifestAndReleaseFeedRoots()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string manifestPath = Path.Combine(baseDirectory, "NewFolder3.json");
        string releaseFeedPath = Path.Combine(baseDirectory, "NewFolder3-release.json");
        Assert(
            File.Exists(manifestPath),
            "Contract output must include repository NewFolder3.json.");
        Assert(
            File.Exists(releaseFeedPath),
            "Contract output must include repository NewFolder3-release.json.");

        using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)))
        {
            Assert(
                manifest.RootElement.ValueKind == JsonValueKind.Object,
                "NewFolder3.json must have an Object root (plugin manifest).");
        }

        using (JsonDocument releaseFeed = JsonDocument.Parse(File.ReadAllText(releaseFeedPath)))
        {
            Assert(
                releaseFeed.RootElement.ValueKind == JsonValueKind.Array,
                "NewFolder3-release.json must have an Array root (repo feed).");
            Assert(
                releaseFeed.RootElement.GetArrayLength() == 1,
                "NewFolder3-release.json must contain exactly one feed entry.");
            Assert(
                releaseFeed.RootElement[0].ValueKind == JsonValueKind.Object,
                "NewFolder3-release.json's sole feed entry must be an Object.");
        }
    }

    private static void TestPublicNoServiceBuildProfile()
    {
        NewFolder3BuildCapabilities capabilities = NewFolder3BuildProfile.Capabilities;
        Assert(
            capabilities.DetailedMapCatalogEndpoint == null &&
            capabilities.CommunityEvidenceEndpoint == null &&
            capabilities.UsageTelemetryEndpoint == null &&
            capabilities.LongRunLogEndpoint == null &&
            !capabilities.HasOnlineCatalogService &&
            !capabilities.HasCommunityEvidenceUpload &&
            !capabilities.HasUsageTelemetry &&
            !capabilities.HasLongRunLogUpload &&
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
            gate.TryAuthorizeFsdStart(out string error) &&
            error.Length == 0,
            "Public access gate must allow FSD start authorization.");
        Assert(
            string.IsNullOrEmpty(gate.FsdStartDenialNotice),
            "Public allow-all start-denial notice must be empty.");
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

    private static void TestCommunityUsageScenarioIndexMapping()
    {
        Assert(
            CommunityUsageTelemetryScenarios.MapScenarioIndex(0) ==
            DetailedMapEvidenceContract.PilgrimsTraverse21To30ScenarioKey,
            "Scenario index 0 must map to pt-21-30.");
        Assert(
            CommunityUsageTelemetryScenarios.MapScenarioIndex(2) ==
            DetailedMapEvidenceContract.PilgrimsTraverse21To30ScenarioKey,
            "Controlled-survey scenario index 2 must map to pt-21-30.");
        Assert(
            CommunityUsageTelemetryScenarios.MapScenarioIndex(1) ==
            DetailedMapScenarioCatalog.PilgrimsTraverse31To40.Key,
            "Scenario index 1 must map to pt-31-40.");
        Assert(
            CommunityUsageTelemetryScenarios.MapScenarioIndex(3) ==
            DetailedMapScenarioCatalog.PilgrimsTraverse31To40.Key,
            "Legacy scenario index 3 must map to pt-31-40.");
        Assert(
            CommunityUsageTelemetryScenarios.MapScenarioIndex(4) == null &&
            CommunityUsageTelemetryScenarios.MapScenarioIndex(-1) == null,
            "Unknown scenario indices must map to null without a silent fallback.");
    }

    private static void TestCommunityUsageTelemetryContract()
    {
        string configRoot = Path.Combine(
            Path.GetTempPath(),
            $"newfolder3-usage-contract-{Guid.NewGuid():N}");
        const string installationToken = "0123456789abcdef0123456789abcdef";
        string pendingDirectory = Path.Combine(configRoot, "UsageTelemetry", "pending");
        try
        {
            Assert(
                !NewFolder3BuildProfile.Capabilities.HasUsageTelemetry,
                "Public builds must not configure a usage telemetry endpoint.");

            string? pluginActiveEventId;
            {
                var handler = new GatedAcceptedHandler();
                using var collector = new CommunityUsageTelemetryCollector(
                    configRoot,
                    installationToken,
                    "0.0.0-contract",
                    new Uri("https://telemetry.invalid/v1/telemetry"),
                    DispatchProxy.Create<IPluginLog, NoOpDispatchProxy>(),
                    handler);

                string controlledScenario =
                    CommunityUsageTelemetryScenarios.MapScenarioIndex(2)
                    ?? throw new InvalidOperationException("Index 2 must map to a scenario.");
                Assert(
                    controlledScenario ==
                    DetailedMapEvidenceContract.PilgrimsTraverse21To30ScenarioKey,
                    "Index 2 mapping must remain pt-21-30 for run-start telemetry.");

                collector.ObserveRunState(
                    isRunActive: true,
                    detailedMapEnabled: true,
                    scenarioKey: null);
                collector.ObserveRunState(
                    isRunActive: false,
                    detailedMapEnabled: true,
                    scenarioKey: null);
                collector.ObserveRunState(
                    isRunActive: true,
                    detailedMapEnabled: true,
                    controlledScenario);
                collector.ObserveRunState(
                    isRunActive: true,
                    detailedMapEnabled: true,
                    controlledScenario);
                collector.ObserveFloorTerminal(
                    CreateUsageFloorTerminal(
                        floor: 29,
                        controlledSurvey: true,
                        outcome: RunFloorTerminalOutcome.PassageCompleted));
                collector.ObserveFloorTerminal(
                    CreateUsageFloorTerminal(
                        floor: 21,
                        controlledSurvey: false,
                        outcome: RunFloorTerminalOutcome.PassageCompleted));
                collector.ObserveFloorTerminal(
                    CreateUsageFloorTerminal(
                        floor: 29,
                        controlledSurvey: false,
                        outcome: RunFloorTerminalOutcome.PlayerDeath));
                collector.ObserveFloorTerminal(
                    CreateUsageFloorTerminal(
                        floor: 29,
                        controlledSurvey: false,
                        outcome: RunFloorTerminalOutcome.PassageCompleted));
                collector.ObserveRunState(
                    isRunActive: false,
                    detailedMapEnabled: true,
                    controlledScenario);

                Assert(
                    handler.WaitUntilRequested(TimeSpan.FromSeconds(2)),
                    "The usage telemetry worker did not start an upload.");
                handler.AllowFirstResponse();

                string[] expectedTypes =
                [
                    CommunityUsageEventTypes.PluginActive,
                    CommunityUsageEventTypes.FsdStarted,
                    CommunityUsageEventTypes.DetailedMapRunStarted,
                    CommunityUsageEventTypes.FsdCompleted
                ];
                Assert(
                    SpinWait.SpinUntil(
                        () => ReadUsageEvents(handler)
                            .Select(value => value.EventType)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(value => value, StringComparer.Ordinal)
                            .SequenceEqual(
                                expectedTypes.OrderBy(value => value, StringComparer.Ordinal),
                                StringComparer.Ordinal),
                        TimeSpan.FromSeconds(2)),
                    "The usage telemetry lifecycle did not upload the expected event set.");

                CommunityUsageEvent[] events = ReadUsageEvents(handler);
                Assert(
                    events.Count(value => value.EventType == CommunityUsageEventTypes.FsdStarted) == 1,
                    "An unchanged active run emitted duplicate fsd_started events.");
                Assert(
                    events.Count(value => value.EventType == CommunityUsageEventTypes.FsdCompleted) == 1,
                    "Controlled-survey, non-final-floor, and non-completion terminals must omit fsd_completed.");
                CommunityUsageEvent started = events.Single(
                    value => value.EventType == CommunityUsageEventTypes.FsdStarted);
                Assert(
                    started.ScenarioKey ==
                    DetailedMapEvidenceContract.PilgrimsTraverse21To30ScenarioKey,
                    "Index-2 run starts must upload pt-21-30 rather than pt-31-40.");
                Assert(
                    events.All(value => IsLowerHex(value.EventId, 32)),
                    "Usage telemetry event IDs must be 32 lowercase hex characters.");

                pluginActiveEventId = events.Single(
                    value => value.EventType == CommunityUsageEventTypes.PluginActive).EventId;

                for (int index = 0; index < handler.RequestCount; index++)
                    AssertUsagePayloadAllowlist(handler.GetRequestPayload(index));
            }

            {
                var handler = new GatedAcceptedHandler();
                using var collector = new CommunityUsageTelemetryCollector(
                    configRoot,
                    installationToken,
                    "0.0.0-contract",
                    new Uri("https://telemetry.invalid/v1/telemetry"),
                    DispatchProxy.Create<IPluginLog, NoOpDispatchProxy>(),
                    handler);
                Assert(
                    handler.WaitUntilRequested(TimeSpan.FromSeconds(2)),
                    "A same-day restart did not re-queue plugin_active.");
                handler.AllowFirstResponse();
                Assert(
                    SpinWait.SpinUntil(
                        () => ReadUsageEvents(handler).Any(
                            value => value.EventType == CommunityUsageEventTypes.PluginActive),
                        TimeSpan.FromSeconds(2)),
                    "A same-day restart did not upload plugin_active.");
                string restartedPluginActiveId = ReadUsageEvents(handler)
                    .Single(value => value.EventType == CommunityUsageEventTypes.PluginActive)
                    .EventId;
                Assert(
                    restartedPluginActiveId == pluginActiveEventId,
                    "Same-day plugin_active event IDs must be deterministic for one installation token.");
            }

            {
                var failingHandler = new FixedStatusHandler(HttpStatusCode.InternalServerError);
                using (var collector = new CommunityUsageTelemetryCollector(
                    configRoot,
                    installationToken,
                    "0.0.0-contract",
                    new Uri("https://telemetry.invalid/v1/telemetry"),
                    DispatchProxy.Create<IPluginLog, NoOpDispatchProxy>(),
                    failingHandler))
                {
                    collector.ObserveRunState(
                        isRunActive: true,
                        detailedMapEnabled: false,
                        DetailedMapScenarioCatalog.PilgrimsTraverse31To40.Key);
                    Assert(
                        SpinWait.SpinUntil(
                            () => failingHandler.RequestCount > 0 &&
                                  Directory.EnumerateFiles(
                                      pendingDirectory,
                                      "*.json",
                                      SearchOption.TopDirectoryOnly).Any(),
                            TimeSpan.FromSeconds(2)),
                        "Failed uploads must leave usage telemetry events on the pending spool.");
                }

                Assert(
                    Directory.EnumerateFiles(
                        pendingDirectory,
                        "*.json",
                        SearchOption.TopDirectoryOnly).Any(),
                    "Pending usage telemetry spool must survive collector disposal.");

                var recoveryHandler = new GatedAcceptedHandler();
                using var recovered = new CommunityUsageTelemetryCollector(
                    configRoot,
                    installationToken,
                    "0.0.0-contract",
                    new Uri("https://telemetry.invalid/v1/telemetry"),
                    DispatchProxy.Create<IPluginLog, NoOpDispatchProxy>(),
                    recoveryHandler);
                Assert(
                    recoveryHandler.WaitUntilRequested(TimeSpan.FromSeconds(2)),
                    "A restarted collector did not resume pending usage telemetry upload.");
                recoveryHandler.AllowFirstResponse();
                Assert(
                    SpinWait.SpinUntil(
                        () => ReadUsageEvents(recoveryHandler).Any(
                            value =>
                                value.EventType == CommunityUsageEventTypes.FsdStarted &&
                                value.ScenarioKey ==
                                DetailedMapScenarioCatalog.PilgrimsTraverse31To40.Key) &&
                          !Directory.EnumerateFiles(
                              pendingDirectory,
                              "*.json",
                              SearchOption.TopDirectoryOnly).Any(),
                        TimeSpan.FromSeconds(2)),
                    "Pending usage telemetry events were not uploaded after collector restart.");
            }
        }
        finally
        {
            if (Directory.Exists(configRoot))
                Directory.Delete(configRoot, recursive: true);
        }
    }

    private static RunFloorTerminalTelemetry CreateUsageFloorTerminal(
        byte floor,
        bool controlledSurvey,
        RunFloorTerminalOutcome outcome) =>
        new(
            ObservedAtUtc: DateTime.UtcNow,
            StableStartedAtUtc: DateTime.UtcNow,
            StableMeasurementEndedAtUtc: DateTime.UtcNow,
            JobId: 30,
            DungeonId: DetailedMapEvidenceContract.PilgrimsTraverseDungeonId,
            FloorsetStart: 21,
            Floor: floor,
            FloorGeneration: 1,
            ControlledSurvey: controlledSurvey,
            NormalMobFloor: true,
            PassageCommitObserved: outcome == RunFloorTerminalOutcome.PassageCompleted,
            HoardOrIntelExecutionStarted: false,
            HoardOpenedThisFloor: false,
            NavigationIssueCount: 0,
            UnclassifiedSeconds: 0,
            StableBaselineSeconds: 1,
            Outcome: outcome,
            Reason: outcome == RunFloorTerminalOutcome.PassageCompleted
                ? "transitioning"
                : "player-death");

    private static void TestCommunityLongRunLogContract()
    {
        string configRoot = Path.Combine(
            Path.GetTempPath(),
            $"newfolder3-long-run-log-contract-{Guid.NewGuid():N}");
        string logDirectory = Path.Combine(configRoot, "source");
        string pendingDirectory = Path.Combine(configRoot, "LongRunLogs", "pending");
        const string installationToken = "0123456789abcdef0123456789abcdef";
        string scenarioKey =
            DetailedMapEvidenceContract.PilgrimsTraverse21To30ScenarioKey;
        DateTime startedAtUtc = new(2026, 8, 22, 1, 0, 0, DateTimeKind.Utc);
        try
        {
            Assert(
                !NewFolder3BuildProfile.Capabilities.HasLongRunLogUpload,
                "Public builds must not configure a long-run log endpoint.");
            Directory.CreateDirectory(logDirectory);
            string exactThirtyPath = Path.Combine(logDirectory, "exact-thirty.jsonl");
            string longPath = Path.Combine(logDirectory, "long.jsonl");
            File.WriteAllBytes(
                exactThirtyPath,
                CreateCompletedRunLog(startedAtUtc, TimeSpan.FromMinutes(30)));
            byte[] expectedLongLog =
                CreateCompletedRunLog(startedAtUtc, TimeSpan.FromMinutes(31));
            File.WriteAllBytes(longPath, expectedLongLog);

            var handler = new GatedAcceptedHandler();
            using var collector = new CommunityLongRunLogCollector(
                configRoot,
                installationToken,
                "0.0.0-contract",
                new UriBuilder(Uri.UriSchemeHttps, IPAddress.Loopback.ToString()).Uri,
                DispatchProxy.Create<IPluginLog, NoOpDispatchProxy>(),
                handler);

            collector.ObserveFloorState(new RunFloorStateTelemetry
            {
                FloorGeneration = 1,
                DetailedMapActive = true,
                Candidates =
                [
                    new RunFloorCandidateFact(
                        RoomIndex: 1,
                        Position: new RawWorldPosition(0, 0, 0),
                        PosteriorWeight: 1,
                        DirectSightSuccessor: false)
                ],
                ExactHoardIndicator = new RawWorldPosition(0, 0, 0)
            });
            collector.ObserveRunRecordingClosed(
                CreateRunRecordingClosed(
                    exactThirtyPath,
                    startedAtUtc,
                    TimeSpan.FromMinutes(30),
                    detailedMapActive: true,
                    reason: "fsd-final-loop-complete",
                    scenarioKey));
            collector.ObserveRunRecordingClosed(
                CreateRunRecordingClosed(
                    longPath,
                    startedAtUtc,
                    TimeSpan.FromMinutes(31),
                    detailedMapActive: false,
                    reason: "fsd-final-loop-complete",
                    scenarioKey));
            collector.ObserveRunRecordingClosed(
                CreateRunRecordingClosed(
                    longPath,
                    startedAtUtc,
                    TimeSpan.FromMinutes(31),
                    detailedMapActive: true,
                    reason: "fsd-stopped",
                    scenarioKey));
            collector.ObserveRunRecordingClosed(
                CreateRunRecordingClosed(
                    longPath,
                    startedAtUtc,
                    TimeSpan.FromMinutes(31),
                    detailedMapActive: true,
                    reason: "fsd-final-loop-complete",
                    scenarioKey));

            try
            {
                Assert(
                    handler.WaitUntilRequested(TimeSpan.FromSeconds(3)),
                    "An eligible completed long detailed-map run was not uploaded.");
                CommunityRunLogEnvelope envelope =
                    CommunityRunLogContract.Parse(handler.GetRequestPayload(0));
                byte[] uploadedLog = CommunityRunLogContract.GetRawLog(envelope);
                Assert(
                    uploadedLog.SequenceEqual(expectedLongLog),
                    "The uploaded gzip envelope did not preserve the complete run log.");
                Assert(
                    envelope.ScenarioKey == scenarioKey &&
                    envelope.DurationMilliseconds ==
                        (long)TimeSpan.FromMinutes(31).TotalMilliseconds,
                    "The uploaded long-run metadata was incorrect.");
            }
            finally
            {
                handler.AllowFirstResponse();
            }

            Assert(
                SpinWait.SpinUntil(
                    () => handler.RequestCount == 1 &&
                          !Directory.EnumerateFiles(
                              pendingDirectory,
                              "*.json",
                              SearchOption.TopDirectoryOnly).Any(),
                    TimeSpan.FromSeconds(3)),
                "Ineligible runs were uploaded or the accepted log remained pending.");

            string unmatchedRoot = Path.Combine(configRoot, "unmatched-hoard");
            string shortPath = Path.Combine(logDirectory, "short-unmatched.jsonl");
            byte[] expectedShortLog =
                CreateCompletedRunLog(startedAtUtc, TimeSpan.FromMinutes(10));
            File.WriteAllBytes(shortPath, expectedShortLog);
            var unmatchedHandler = new GatedAcceptedHandler();
            using (var unmatchedCollector = new CommunityLongRunLogCollector(
                       unmatchedRoot,
                       installationToken,
                       "0.0.0-contract",
                       new UriBuilder(Uri.UriSchemeHttps, IPAddress.Loopback.ToString()).Uri,
                       DispatchProxy.Create<IPluginLog, NoOpDispatchProxy>(),
                       unmatchedHandler))
            {
                unmatchedCollector.ObserveFloorState(new RunFloorStateTelemetry
                {
                    FloorGeneration = 1,
                    DetailedMapActive = true,
                    Candidates =
                    [
                        new RunFloorCandidateFact(
                            RoomIndex: 1,
                            Position: new RawWorldPosition(0, 0, 0),
                            PosteriorWeight: 1,
                            DirectSightSuccessor: false)
                    ],
                    ExactHoardIndicator = new RawWorldPosition(6, 0, 0)
                });
                unmatchedCollector.ObserveRunRecordingClosed(
                    CreateRunRecordingClosed(
                        shortPath,
                        startedAtUtc,
                        TimeSpan.FromMinutes(10),
                        detailedMapActive: true,
                        reason: "fsd-final-loop-complete",
                        scenarioKey));
                try
                {
                    Assert(
                        unmatchedHandler.WaitUntilRequested(TimeSpan.FromSeconds(3)),
                        "A short run containing an unmatched hoard was not uploaded.");
                    CommunityRunLogEnvelope unmatchedEnvelope =
                        CommunityRunLogContract.Parse(
                            unmatchedHandler.GetRequestPayload(0));
                    Assert(
                        unmatchedEnvelope.UploadReason ==
                            CommunityRunLogContract.UnmatchedHoardReason &&
                        CommunityRunLogContract.GetRawLog(unmatchedEnvelope)
                            .SequenceEqual(expectedShortLog),
                        "The unmatched-hoard upload did not preserve its reason and full log.");
                }
                finally
                {
                    unmatchedHandler.AllowFirstResponse();
                }
            }

            byte[] personalLog = Encoding.UTF8.GetBytes(
                "{\"timestampUtc\":\"2026-08-22T01:00:00Z\",\"eventType\":\"controller-initialized\",\"data\":{\"recorderPath\":\"C:\\\\Users\\\\Example\\\\run.jsonl\"}}\n" +
                "{\"timestampUtc\":\"2026-08-22T01:31:00Z\",\"eventType\":\"run-recorder-closing\",\"data\":{\"reason\":\"fsd-final-loop-complete\"}}\n");
            bool personalLogRejected = false;
            try
            {
                _ = CommunityRunLogContract.CreateEnvelope(
                    personalLog,
                    scenarioKey,
                    "0.0.0-contract");
            }
            catch (InvalidDataException)
            {
                personalLogRejected = true;
            }
            Assert(
                personalLogRejected,
                "Run logs containing an absolute recorder path must be rejected.");
        }
        finally
        {
            if (Directory.Exists(configRoot))
                Directory.Delete(configRoot, recursive: true);
        }
    }

    private static RunRecordingClosedTelemetry CreateRunRecordingClosed(
        string path,
        DateTime startedAtUtc,
        TimeSpan duration,
        bool detailedMapActive,
        string reason,
        string scenarioKey) =>
        new(
            startedAtUtc,
            startedAtUtc + duration,
            path,
            reason,
            scenarioKey,
            detailedMapActive,
            ControlledSurvey: false);

    private static byte[] CreateCompletedRunLog(DateTime startedAtUtc, TimeSpan duration)
    {
        string first = JsonSerializer.Serialize(new
        {
            timestampUtc = startedAtUtc,
            eventType = "controller-initialized",
            data = new { mode = "unknown" }
        });
        string last = JsonSerializer.Serialize(new
        {
            timestampUtc = startedAtUtc + duration,
            eventType = "run-recorder-closing",
            data = new { reason = "fsd-final-loop-complete" }
        });
        return Encoding.UTF8.GetBytes($"{first}\n{last}\n");
    }

    private static void AssertUsagePayloadAllowlist(byte[] payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        Assert(
            document.RootElement.ValueKind == JsonValueKind.Object,
            "Usage telemetry payloads must be JSON objects.");
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            Assert(
                property.Name is "schemaVersion" or "events",
                $"Usage telemetry batch property '{property.Name}' is outside the allowlist.");
        }

        Assert(
            document.RootElement.TryGetProperty("events", out JsonElement events) &&
            events.ValueKind == JsonValueKind.Array,
            "Usage telemetry payloads must contain an events array.");
        foreach (JsonElement value in events.EnumerateArray())
        {
            Assert(
                value.ValueKind == JsonValueKind.Object,
                "Usage telemetry events must be JSON objects.");
            foreach (JsonProperty property in value.EnumerateObject())
            {
                Assert(
                    property.Name is
                        "eventId" or
                        "eventType" or
                        "occurredDateUtc" or
                        "clientVersion" or
                        "scenarioKey",
                    $"Usage telemetry event property '{property.Name}' is outside the allowlist.");
            }
        }
    }

    private static CommunityUsageEvent[] ReadUsageEvents(GatedAcceptedHandler handler)
    {
        var events = new List<CommunityUsageEvent>();
        int requestCount = handler.RequestCount;
        for (int index = 0; index < requestCount; index++)
        {
            CommunityUsageEventBatch batch =
                CommunityUsageTelemetryContract.Parse(handler.GetRequestPayload(index));
            events.AddRange(batch.Events);
        }
        return events.ToArray();
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

    private sealed class FixedStatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private int _requestCount;

        internal FixedStatusHandler(HttpStatusCode statusCode) =>
            _statusCode = statusCode;

        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                RequestMessage = request
            });
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
