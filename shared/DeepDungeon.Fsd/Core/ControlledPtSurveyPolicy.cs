namespace DeepDungeon.Fsd.Core;

public enum ControlledPtSurveyTargetOutcome
{
    None,
    Pending,
    IntuitionNegative,
    InheritedNoHoardInferred,
    InheritedStateInconsistent,
    PositiveJointSampleIncomplete,
    PositiveCaptured
}

public enum ControlledPtIntuitionResolutionSource
{
    None,
    CurrentUseHoardPresent,
    CurrentUseNoHoard,
    InheritedHoardPresent,
    InheritedNoHoardInferred,
    CurrentUseTimeoutError,
    InvalidInheritedNoHoardMessage,
    RejectedInheritedEvidence
}

public readonly record struct ControlledPtIntuitionResolutionDecision(
    bool Terminal,
    bool HoardPresent,
    bool NoHoard,
    bool IsError,
    ControlledPtIntuitionResolutionSource Source,
    int ElapsedMilliseconds);

public enum ControlledPtSurveyItemAction
{
    None,
    UseIntuition,
    UseSight,
    UseMazeroot,
    UsePoisonfruit
}

public enum ControlledPtDispatchGateAction
{
    Allow,
    WaitForExactPassage,
    RelocateAway
}

public enum ControlledPtPostCaptureAccelerationAction
{
    None,
    RetryLater,
    Dispatch,
    CompleteWithoutDispatch
}

public enum ControlledPtRestPromptDecision
{
    Accept,
    RejectDeletePrompt,
    RejectUnowned,
    RejectExpired,
    RejectText
}

public enum ControlledPtJointCaptureAction
{
    WaitForReveal,
    MissingCandidateUniverse,
    ApproachHoardRoom,
    WaitForHoardRoomScan,
    Incomplete,
    Complete
}

public enum ControlledPtInheritedNativeGateAction
{
    WaitForNativeState,
    ProceedInherited,
    ReactivateWithCurrentUse
}

public enum ControlledPtPositiveIndicatorAction
{
    AcquireExactIndicator,
    ContinueCapture
}

public readonly record struct ControlledPtSaveSlotDecision(
    bool IsValid,
    int OccupiedSlotIndex,
    string Error);

public readonly record struct ControlledPtSurveyFloorDecision(
    ControlledPtSurveyItemAction ItemAction,
    bool ShouldAbandon,
    string Reason);

public static class ControlledPtSurveyPolicy
{
    public const byte FirstFloor = 21;
    public const byte LastResearchFloor = 29;
    public const float PassageDispatchExclusionRadius = 3f;
    public const float TrapWitnessSafetyMargin = 1f;

    public static ControlledPtSaveSlotDecision ValidateSaveSlots(
        bool slot1Empty,
        bool slot2Empty,
        int? pinnedOccupiedSlotIndex)
    {
        if (slot1Empty == slot2Empty)
        {
            return new ControlledPtSaveSlotDecision(
                false,
                -1,
                slot1Empty
                    ? "Controlled PT capture requires exactly one occupied save slot; both slots are empty."
                    : "Controlled PT capture requires exactly one empty save slot; both slots are occupied.");
        }

        int occupied = slot1Empty ? 1 : 0;
        if (pinnedOccupiedSlotIndex.HasValue && pinnedOccupiedSlotIndex.Value != occupied)
        {
            return new ControlledPtSaveSlotDecision(
                false,
                occupied,
                $"Controlled PT capture pinned save slot {pinnedOccupiedSlotIndex.Value + 1}, but occupied slot {occupied + 1} was observed.");
        }

        return new ControlledPtSaveSlotDecision(true, occupied, string.Empty);
    }

    public static bool IsResearchFloor(byte floor) =>
        floor >= FirstFloor && floor <= LastResearchFloor;

    public static bool HasSightCapableResource(int sightStock, int mazerootCount) =>
        sightStock > 0 || mazerootCount > 0;

    public static ControlledPtPositiveIndicatorAction DecidePositiveIndicatorAction(
        bool exactPositionResolved,
        bool indicatorPresent) =>
        exactPositionResolved || indicatorPresent
            ? ControlledPtPositiveIndicatorAction.ContinueCapture
            : ControlledPtPositiveIndicatorAction.AcquireExactIndicator;

    public static ControlledPtSurveyFloorDecision DecideFloorAction(
        byte floor,
        ControlledPtSurveyTargetOutcome outcome,
        int sightStock,
        int mazerootCount,
        int poisonfruitCount)
    {
        if (!IsResearchFloor(floor))
            return new ControlledPtSurveyFloorDecision(ControlledPtSurveyItemAction.None, true, "outside-controlled-floor-range");

        if (outcome == ControlledPtSurveyTargetOutcome.Pending)
            return new ControlledPtSurveyFloorDecision(ControlledPtSurveyItemAction.None, false, "await-authoritative-intuition-result");

        if (outcome is ControlledPtSurveyTargetOutcome.IntuitionNegative or
            ControlledPtSurveyTargetOutcome.InheritedNoHoardInferred)
        {
            return new ControlledPtSurveyFloorDecision(
                poisonfruitCount > 0 ? ControlledPtSurveyItemAction.UsePoisonfruit : ControlledPtSurveyItemAction.None,
                floor == LastResearchFloor,
                floor == LastResearchFloor ? "negative-final-floor" : "negative-floor-continue");
        }

        if (outcome is ControlledPtSurveyTargetOutcome.PositiveCaptured or
            ControlledPtSurveyTargetOutcome.PositiveJointSampleIncomplete)
        {
            bool hasSightResource = HasSightCapableResource(sightStock, mazerootCount);
            bool incomplete = outcome == ControlledPtSurveyTargetOutcome.PositiveJointSampleIncomplete;
            return new ControlledPtSurveyFloorDecision(
                ControlledPtSurveyItemAction.None,
                !hasSightResource || floor == LastResearchFloor,
                incomplete
                    ? hasSightResource && floor < LastResearchFloor
                        ? "positive-incomplete-floor-continue"
                        : "positive-incomplete-final-opportunity"
                    : hasSightResource && floor < LastResearchFloor
                        ? "positive-floor-continue"
                        : "positive-final-opportunity");
        }

        return new ControlledPtSurveyFloorDecision(ControlledPtSurveyItemAction.None, false, "await-floor-observation");
    }

    public static ControlledPtSurveyItemAction DecidePositiveCaptureItem(
        byte floor,
        bool sightActive,
        int sightStock,
        int mazerootCount)
    {
        if (sightActive)
            return ControlledPtSurveyItemAction.None;
        if (floor < LastResearchFloor && mazerootCount > 0)
            return ControlledPtSurveyItemAction.UseMazeroot;
        if (sightStock > 0)
            return ControlledPtSurveyItemAction.UseSight;
        if (mazerootCount > 0)
            return ControlledPtSurveyItemAction.UseMazeroot;
        return ControlledPtSurveyItemAction.None;
    }

    public static ControlledPtDispatchGateAction DecidePassageDispatchGate(
        bool barrierRequired,
        bool roomRelationAvailable,
        bool playerInPassageRoom,
        bool exactPassageAvailable,
        float distanceSquared)
    {
        if (!barrierRequired)
            return ControlledPtDispatchGateAction.Allow;
        if (roomRelationAvailable && !playerInPassageRoom)
            return ControlledPtDispatchGateAction.Allow;
        if (!exactPassageAvailable)
            return ControlledPtDispatchGateAction.WaitForExactPassage;
        return distanceSquared < PassageDispatchExclusionRadius * PassageDispatchExclusionRadius
            ? ControlledPtDispatchGateAction.RelocateAway
            : ControlledPtDispatchGateAction.Allow;
    }

    public static ControlledPtPostCaptureAccelerationAction DecidePostCaptureAcceleration(
        bool pending,
        bool alreadyDispatched,
        bool passageOpen,
        int poisonfruitStock,
        bool canDispatch)
    {
        if (!pending || alreadyDispatched)
            return ControlledPtPostCaptureAccelerationAction.None;
        if (passageOpen || poisonfruitStock <= 0)
            return ControlledPtPostCaptureAccelerationAction.CompleteWithoutDispatch;
        return canDispatch
            ? ControlledPtPostCaptureAccelerationAction.Dispatch
            : ControlledPtPostCaptureAccelerationAction.RetryLater;
    }

    public static ControlledPtRestPromptDecision DecideRestExitPrompt(
        bool deletePrompt,
        bool interactionDispatched,
        int elapsedMilliseconds,
        int ownershipWindowMilliseconds,
        string expectedQuestion,
        string actualQuestion)
    {
        if (deletePrompt)
            return ControlledPtRestPromptDecision.RejectDeletePrompt;
        if (!interactionDispatched)
            return ControlledPtRestPromptDecision.RejectUnowned;
        if (elapsedMilliseconds < 0 || elapsedMilliseconds > ownershipWindowMilliseconds)
            return ControlledPtRestPromptDecision.RejectExpired;
        return string.Equals(
                NormalizePrompt(expectedQuestion),
                NormalizePrompt(actualQuestion),
                StringComparison.Ordinal)
            ? ControlledPtRestPromptDecision.Accept
            : ControlledPtRestPromptDecision.RejectText;
    }

    public static ControlledPtJointCaptureAction DecideJointCapture(
        bool authoritativeRevealConfirmed,
        bool candidateUniverseAvailable,
        bool trapWitnessAvailable,
        bool allCandidatesCovered,
        bool synchronizedScanAvailable,
        bool hoardRoomTargetReached,
        bool postArrivalScanAvailable)
    {
        if (!authoritativeRevealConfirmed)
            return ControlledPtJointCaptureAction.WaitForReveal;
        if (!candidateUniverseAvailable)
            return ControlledPtJointCaptureAction.MissingCandidateUniverse;
        if (synchronizedScanAvailable && trapWitnessAvailable && allCandidatesCovered)
            return ControlledPtJointCaptureAction.Complete;
        if (!hoardRoomTargetReached)
            return ControlledPtJointCaptureAction.ApproachHoardRoom;
        if (!synchronizedScanAvailable || !postArrivalScanAvailable)
            return ControlledPtJointCaptureAction.WaitForHoardRoomScan;
        return ControlledPtJointCaptureAction.Incomplete;
    }

    public static float GetProvenTrapLoadSafeRadius(float maximumFirstAppearanceDistance) =>
        MathF.Max(0f, maximumFirstAppearanceDistance - TrapWitnessSafetyMargin);

    public static bool AreAllCandidatesCovered(
        in RawWorldPosition playerPosition,
        IReadOnlyList<RawWorldPosition> candidates,
        float safeRadius)
    {
        if (candidates.Count == 0 || safeRadius <= 0f)
            return false;

        float safeRadiusSquared = safeRadius * safeRadius;
        for (int i = 0; i < candidates.Count; i++)
        {
            float dx = playerPosition.X - candidates[i].X;
            float dz = playerPosition.Z - candidates[i].Z;
            if (dx * dx + dz * dz > safeRadiusSquared)
                return false;
        }

        return true;
    }

    public static bool IsAuthoritativeCaptureReveal(
        ControlledPtSurveyItemAction captureItem,
        bool postDispatchSightLog,
        bool postDispatchMazerootLog)
    {
        return captureItem switch
        {
            ControlledPtSurveyItemAction.UseMazeroot => postDispatchMazerootLog,
            ControlledPtSurveyItemAction.UseSight => postDispatchSightLog,
            ControlledPtSurveyItemAction.None => postDispatchSightLog,
            _ => false
        };
    }

    public static ControlledPtInheritedNativeGateAction DecideInheritedNativeGate(
        bool nativeStateAvailable,
        bool nativeIntuitionActive)
    {
        if (!nativeStateAvailable)
            return ControlledPtInheritedNativeGateAction.WaitForNativeState;
        return nativeIntuitionActive
            ? ControlledPtInheritedNativeGateAction.ProceedInherited
            : ControlledPtInheritedNativeGateAction.ReactivateWithCurrentUse;
    }

    public static ControlledPtIntuitionResolutionDecision ResolveCurrentIntuition(
        bool chatSaysHoard,
        bool chatSaysNoHoard,
        int elapsedMilliseconds,
        int resolutionWindowMilliseconds)
    {
        int elapsed = Math.Max(0, elapsedMilliseconds);
        int window = Math.Max(0, resolutionWindowMilliseconds);
        if (chatSaysHoard)
        {
            return new ControlledPtIntuitionResolutionDecision(
                true,
                true,
                false,
                false,
                ControlledPtIntuitionResolutionSource.CurrentUseHoardPresent,
                elapsed);
        }

        if (chatSaysNoHoard)
        {
            return new ControlledPtIntuitionResolutionDecision(
                true,
                false,
                true,
                false,
                ControlledPtIntuitionResolutionSource.CurrentUseNoHoard,
                elapsed);
        }

        if (elapsed < window)
            return new ControlledPtIntuitionResolutionDecision(false, false, false, false, ControlledPtIntuitionResolutionSource.None, elapsed);

        return new ControlledPtIntuitionResolutionDecision(
            true,
            false,
            false,
            true,
            ControlledPtIntuitionResolutionSource.CurrentUseTimeoutError,
            elapsed);
    }

    private static string NormalizePrompt(string value) =>
        string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character)));
}
