namespace DeepDungeon.Fsd.Core;

public static class DetailedMapEvidenceProjector
{
    public static bool TryProject(
        FloorEvidenceBundle source,
        string? catalogReleaseUsed,
        out DetailedMapFloorEvidence? evidence,
        out string rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(source);
        evidence = null;

        if (source.SchemaVersion != FloorEvidenceBundle.CurrentSchemaVersion)
        {
            rejectionReason = $"unsupported-floor-evidence-schema-{source.SchemaVersion}";
            return false;
        }
        if (!DetailedMapScenarioCatalog.TryGetByScope(
                source.DungeonId,
                source.TerritoryId,
                source.FloorSetStart,
                source.Floor,
                out DetailedMapScenarioDefinition? scenario))
        {
            rejectionReason = "unsupported-scenario-scope";
            return false;
        }

        try
        {
            DetailedMapRoomBinding[] roomBindings = source.RoomBindings
                .OrderBy(binding => binding.RoomIndex)
                .Select(binding => new DetailedMapRoomBinding
                {
                    RoomIndex = binding.RoomIndex,
                    ConnectionFlags = binding.ConnectionFlags,
                    RoomCenter = binding.RoomCenter
                })
                .ToArray();
            var roomIndexes = roomBindings
                .Select(binding => binding.RoomIndex)
                .ToHashSet();
            DetailedMapObservedPosition[] hoards = BuildUniquePositions(
                source.ExactHoardObservations.Select(observation =>
                    new DetailedMapObservedPosition
                    {
                        Position = observation.RawWorldPosition,
                        RoomIndex = observation.RoomIndex
                    }));
            DetailedMapObservedPosition[] indicators = BuildUniquePositions(
                source.ExactHoardObservations
                    .Where(observation =>
                        observation.Source == ExactHoardObservationSource.Indicator)
                    .Select(observation =>
                        new DetailedMapObservedPosition
                        {
                            Position = observation.RawWorldPosition,
                            RoomIndex = observation.RoomIndex
                        }));
            DetailedMapObservedPosition[] traps = BuildUniquePositions(
                source.ActiveTrapObservations.Select(observation =>
                    new DetailedMapObservedPosition
                    {
                        Position = observation.RawWorldPosition,
                        RoomIndex = observation.RoomIndex
                    }));

            (DetailedMapIntuitionState intuitionState, string intuitionReason) =
                ResolveIntuitionState(source, indicators.Length);
            bool structurallyInvalid =
                roomBindings.Length == 0 ||
                hoards.Any(position => !roomIndexes.Contains(position.RoomIndex)) ||
                traps.Any(position => !roomIndexes.Contains(position.RoomIndex)) ||
                hoards.Length > 1 ||
                indicators.Length > 1 ||
                intuitionState == DetailedMapIntuitionState.HoardPresent && indicators.Length != 1 ||
                intuitionState == DetailedMapIntuitionState.NoHoard && hoards.Length != 0;
            DetailedMapTerminalState terminalState = structurallyInvalid ||
                                                     intuitionState == DetailedMapIntuitionState.Invalid
                ? DetailedMapTerminalState.Invalid
                : hoards.Length == 1
                    ? DetailedMapTerminalState.HoardPositive
                    : intuitionState == DetailedMapIntuitionState.NoHoard
                        ? DetailedMapTerminalState.NoHoard
                        : DetailedMapTerminalState.Incomplete;
            string terminalReason = structurallyInvalid
                ? "structural-inconsistency"
                : hoards.Length == 1
                    ? "exact-hoard-observed"
                    : intuitionReason;

            (DetailedMapTrapScanState trapScanState, DetailedMapRevealSource revealSource) =
                ResolveTrapScan(source);
            bool controlledJointComplete =
                source.AcquisitionMode == FloorEvidenceAcquisitionMode.ControlledReusableSaveSurvey &&
                source.ControlledSurvey.FloorRole == ControlledSurveyFloorRole.SelectedTarget &&
                source.ControlledSurvey.Outcome == ControlledPtSurveyTargetOutcome.PositiveCaptured;
            bool naturalJointComplete =
                source.AcquisitionMode == FloorEvidenceAcquisitionMode.AutomaticCommunitySurvey &&
                source.SightResearchDecision.AuthoritativeRevealConfirmed &&
                source.SightResearchDecision.JointScanComplete;
            bool jointComplete = controlledJointComplete || naturalJointComplete;
            bool pairEligible =
                jointComplete &&
                terminalState == DetailedMapTerminalState.HoardPositive &&
                hoards.Length == 1;
            string pairReason = pairEligible
                ? controlledJointComplete
                    ? "controlled-positive-joint-complete"
                    : "natural-positive-joint-complete"
                : jointComplete
                    ? "joint-complete-structural-invalid"
                    : trapScanState == DetailedMapTrapScanState.Complete
                        ? "complete-scan-without-eligible-positive"
                        : "joint-scan-incomplete";

            evidence = new DetailedMapFloorEvidence
            {
                CollectorVersion = source.CollectorVersion,
                ScenarioKey = scenario.Key,
                FloorInstanceId = source.FloorInstanceId,
                Floor = source.Floor,
                TerritoryId = source.TerritoryId,
                ActiveLayoutIndex = source.ActiveLayoutIndex,
                AcquisitionMode = source.AcquisitionMode,
                CatalogReleaseUsed = string.IsNullOrWhiteSpace(catalogReleaseUsed)
                    ? null
                    : catalogReleaseUsed,
                RoomBindings = roomBindings,
                Terminal = new DetailedMapTerminalObservation
                {
                    State = terminalState,
                    Reason = terminalReason
                },
                Intuition = new DetailedMapIntuitionObservation
                {
                    State = intuitionState,
                    IndicatorPosition = intuitionState == DetailedMapIntuitionState.HoardPresent
                        ? indicators[0].Position
                        : null
                },
                TrapScan = new DetailedMapTrapScanObservation
                {
                    State = trapScanState,
                    RevealSource = revealSource,
                    Traps = traps
                },
                ExactHoards = hoards,
                PairEligibility = new DetailedMapPairEligibility
                {
                    Eligible = pairEligible,
                    JointScanComplete = jointComplete,
                    Reason = pairReason
                }
            };
            DetailedMapEvidenceContract.Validate(
                new DetailedMapEvidenceBatch { Floors = [evidence] });
            rejectionReason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            evidence = null;
            rejectionReason = ex.Message;
            return false;
        }
    }

    private static (DetailedMapIntuitionState State, string Reason) ResolveIntuitionState(
        FloorEvidenceBundle source,
        int uniqueIndicatorCount)
    {
        ControlledPtSurveyTargetOutcome controlledOutcome = source.ControlledSurvey.Outcome;
        if (source.AcquisitionMode == FloorEvidenceAcquisitionMode.ControlledReusableSaveSurvey)
        {
            switch (controlledOutcome)
            {
                case ControlledPtSurveyTargetOutcome.IntuitionNegative:
                    return (DetailedMapIntuitionState.NoHoard, "current-intuition-no-hoard");
                case ControlledPtSurveyTargetOutcome.InheritedNoHoardInferred:
                    return (DetailedMapIntuitionState.NoHoard, "inherited-intuition-no-hoard");
                case ControlledPtSurveyTargetOutcome.PositiveCaptured:
                case ControlledPtSurveyTargetOutcome.PositiveJointSampleIncomplete:
                    return uniqueIndicatorCount == 1
                        ? (DetailedMapIntuitionState.HoardPresent, "intuition-hoard-present")
                        : (DetailedMapIntuitionState.Unresolved, "positive-without-unique-indicator");
                case ControlledPtSurveyTargetOutcome.InheritedStateInconsistent:
                    return (DetailedMapIntuitionState.Invalid, "controlled-intuition-inconsistent");
            }
        }

        if (uniqueIndicatorCount == 1)
            return (DetailedMapIntuitionState.HoardPresent, "exact-indicator-observed");
        if (source.InheritedIntuitionResolution.Source == InheritedIntuitionResolutionSource.NoHoardInferred)
            return (DetailedMapIntuitionState.NoHoard, "inherited-intuition-no-hoard");
        if (source.InheritedIntuitionResolution.Source is
            InheritedIntuitionResolutionSource.InvalidNoHoardMessage or
            InheritedIntuitionResolutionSource.RejectedEvidence)
        {
            return (DetailedMapIntuitionState.Invalid, "inherited-intuition-invalid");
        }

        bool positiveMessage = source.SemanticEvents.Any(value =>
            value.MessageId == 7272 && value.Accepted);
        bool negativeMessage = source.SemanticEvents.Any(value =>
            value.MessageId == 7273 && value.Accepted);
        if (positiveMessage && negativeMessage)
            return (DetailedMapIntuitionState.Invalid, "conflicting-intuition-messages");
        if (negativeMessage)
            return (DetailedMapIntuitionState.NoHoard, "current-intuition-no-hoard");
        if (positiveMessage)
            return (DetailedMapIntuitionState.Unresolved, "positive-indicator-not-resolved");
        return (DetailedMapIntuitionState.NotObserved, "intuition-not-observed");
    }

    private static (DetailedMapTrapScanState State, DetailedMapRevealSource Source) ResolveTrapScan(
        FloorEvidenceBundle source)
    {
        DetailedMapRevealSource revealSource = source.AcquisitionMode ==
                                                FloorEvidenceAcquisitionMode.ControlledReusableSaveSurvey
            ? source.ControlledSurvey.CaptureItem switch
            {
                ControlledPtSurveyItemAction.UseSight => DetailedMapRevealSource.Sight,
                ControlledPtSurveyItemAction.UseMazeroot => DetailedMapRevealSource.Mazeroot,
                _ => DetailedMapRevealSource.None
            }
            : source.SightResearchDecision.RevealResource switch
            {
                SightResearchRevealResource.Sight => DetailedMapRevealSource.Sight,
                SightResearchRevealResource.Mazeroot => DetailedMapRevealSource.Mazeroot,
                _ => DetailedMapRevealSource.None
            };
        if (source.AcquisitionMode == FloorEvidenceAcquisitionMode.ControlledReusableSaveSurvey &&
            source.ControlledSurvey.Outcome == ControlledPtSurveyTargetOutcome.PositiveCaptured)
        {
            return (DetailedMapTrapScanState.Complete, revealSource);
        }
        if (source.AcquisitionMode == FloorEvidenceAcquisitionMode.AutomaticCommunitySurvey &&
            source.SightResearchDecision.AuthoritativeRevealConfirmed &&
            source.SightResearchDecision.JointScanComplete)
        {
            return (DetailedMapTrapScanState.Complete, revealSource);
        }
        if (revealSource != DetailedMapRevealSource.None ||
            source.ControlledSurvey.Outcome ==
            ControlledPtSurveyTargetOutcome.PositiveJointSampleIncomplete)
        {
            return (DetailedMapTrapScanState.Incomplete, revealSource);
        }
        return (DetailedMapTrapScanState.NotAttempted, DetailedMapRevealSource.None);
    }

    private static DetailedMapObservedPosition[] BuildUniquePositions(
        IEnumerable<DetailedMapObservedPosition> values)
    {
        var result = new List<DetailedMapObservedPosition>();
        foreach (DetailedMapObservedPosition value in values
                     .OrderBy(position => position.RoomIndex)
                     .ThenBy(position => position.Position.X)
                     .ThenBy(position => position.Position.Y)
                     .ThenBy(position => position.Position.Z))
        {
            if (result.Any(existing =>
                    existing.RoomIndex == value.RoomIndex &&
                    RawWorldPosition.CanonicallyEquals(existing.Position, value.Position)))
            {
                continue;
            }
            result.Add(value);
        }
        return result.ToArray();
    }
}
