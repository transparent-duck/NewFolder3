namespace DeepDungeon.Fsd.Core;

public enum FloorEvidenceAcquisitionMode
{
    NaturalGameplay,
    AutomaticCommunitySurvey,
    ControlledReusableSaveSurvey
}

public enum FloorEvidenceEffect
{
    Intuition,
    Sight
}

public enum ExactHoardObservationSource
{
    Indicator,
    Banded
}

public enum ActiveTrapObservationSource
{
    Sight,
    Trigger
}

public enum FloorObjectVisibilityType
{
    HoardIndicator,
    SightTrap
}

public enum FloorObjectVisibilityTransitionKind
{
    Appeared,
    Disappeared
}

public enum ControlledSurveyFloorRole
{
    None,
    Transit,
    SelectedTarget
}

public sealed class FloorEvidenceBundle
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string CollectorVersion { get; set; } = string.Empty;
    public string GameBuild { get; set; } = string.Empty;
    public string FloorInstanceId { get; set; } = string.Empty;
    public uint DungeonId { get; set; }
    public byte Floor { get; set; }
    public int FloorSetStart { get; set; }
    public uint TerritoryId { get; set; }
    public int ActiveLayoutIndex { get; set; }
    public FloorEvidenceAcquisitionMode AcquisitionMode { get; set; }
    public FloorRoomBinding[] RoomBindings { get; set; } = [];
    public FloorEvidenceSemanticEvent[] SemanticEvents { get; set; } = [];
    public InheritedIntuitionResolutionObservation InheritedIntuitionResolution { get; set; } = new();
    public FloorEvidenceEffectTransition[] EffectTransitions { get; set; } = [];
    public SightResearchDecisionObservation SightResearchDecision { get; set; } = new();
    public ControlledSurveyObservation ControlledSurvey { get; set; } = new();
    public ExactHoardPositionObservation[] ExactHoardObservations { get; set; } = [];
    public ActiveTrapPositionObservation[] ActiveTrapObservations { get; set; } = [];
    public FloorObjectVisibilityTransition[] ObjectVisibilityTransitions { get; set; } = [];
    public FloorRoomVisit[] RoomVisits { get; set; } = [];
    public FloorEvidenceCoverage Coverage { get; set; } = new();
    public int FinalizedRelativeMilliseconds { get; set; }
    public string TerminationReason { get; set; } = string.Empty;
}

public sealed class InheritedIntuitionResolutionObservation
{
    public InheritedIntuitionResolutionSource Source { get; set; }
    public int ElapsedMilliseconds { get; set; }
    public int ResolutionWindowMilliseconds { get; set; }
}

public sealed class ControlledSurveyObservation
{
    public ControlledSurveyFloorRole FloorRole { get; set; }
    public byte[] ScheduledTargetFloors { get; set; } = [];
    public ControlledPtSurveyTargetOutcome Outcome { get; set; }
    public ControlledPtSurveyItemAction CaptureItem { get; set; }
    public ControlledPtIntuitionResolutionSource IntuitionResolutionSource { get; set; }
    public int IntuitionResolutionElapsedMilliseconds { get; set; }
    public int IntuitionResolutionWindowMilliseconds { get; set; }
    public int? AuthoritativeRevealRelativeMilliseconds { get; set; }
}

public sealed class FloorRoomBinding
{
    public int RoomIndex { get; set; }
    public uint ConnectionFlags { get; set; }
    public RawWorldPosition RoomCenter { get; set; }
    public string BindingSource { get; set; } = string.Empty;
}

public sealed class FloorEvidenceSemanticEvent
{
    public uint MessageId { get; set; }
    public int RelativeMilliseconds { get; set; }
    public bool Accepted { get; set; }
}

public sealed class FloorEvidenceEffectTransition
{
    public FloorEvidenceEffect Effect { get; set; }
    public int RelativeMilliseconds { get; set; }
    public bool Active { get; set; }
    public int Stock { get; set; }
    public string Source { get; set; } = string.Empty;
}

public sealed class SightResearchDecisionObservation
{
    public int PolicyVersion { get; set; } = SightResearchPolicy.PolicyVersion;
    public SightResearchDecisionKind Kind { get; set; } = SightResearchDecisionKind.Pending;
    public SightResearchRevealResource RevealResource { get; set; }
    public bool Eligible { get; set; }
    public string Reason { get; set; } = "exact-indicator-unresolved";
    public int DecisionRelativeMilliseconds { get; set; }
    public bool ActionAttempted { get; set; }
    public bool ActionDispatched { get; set; }
    public int? ActionRelativeMilliseconds { get; set; }
    public int SightStockBefore { get; set; }
    public int? SightStockAfter { get; set; }
    public int ResourceStockBefore { get; set; }
    public bool AuthoritativeRevealConfirmed { get; set; }
    public int? AuthoritativeRevealRelativeMilliseconds { get; set; }
    public bool JointScanComplete { get; set; }
}

public sealed class ExactHoardPositionObservation
{
    public RawWorldPosition RawWorldPosition { get; set; }
    public int RoomIndex { get; set; }
    public RawWorldPosition? RoomCenter { get; set; }
    public ExactHoardObservationSource Source { get; set; }
    public uint BaseId { get; set; }
    public ulong GameObjectId { get; set; }
    public int FirstSeenRelativeMilliseconds { get; set; }
    public int LastSeenRelativeMilliseconds { get; set; }
    public int ObservationCount { get; set; }
}

public sealed class ActiveTrapPositionObservation
{
    public RawWorldPosition RawWorldPosition { get; set; }
    public int RoomIndex { get; set; }
    public RawWorldPosition? RoomCenter { get; set; }
    public ActiveTrapObservationSource Source { get; set; }
    public uint BaseId { get; set; }
    public ulong GameObjectId { get; set; }
    public int FirstSeenRelativeMilliseconds { get; set; }
    public int LastSeenRelativeMilliseconds { get; set; }
    public int ObservationCount { get; set; }
}

public sealed class FloorObjectVisibilityTransition
{
    public FloorObjectVisibilityType Type { get; set; }
    public FloorObjectVisibilityTransitionKind Transition { get; set; }
    public uint BaseId { get; set; }
    public ulong GameObjectId { get; set; }
    public string ObjectKind { get; set; } = string.Empty;
    public byte NativeCurrentDistance { get; set; }
    public RawWorldPosition ObjectPosition { get; set; }
    public RawWorldPosition PlayerPosition { get; set; }
    public float DistanceXz { get; set; }
    public float Distance3d { get; set; }
    public int RelativeMilliseconds { get; set; }
    public int LastSeenRelativeMilliseconds { get; set; }
    public int? MillisecondsSinceAuthoritativeReveal { get; set; }
}

public sealed class FloorRoomVisit
{
    public int RoomIndex { get; set; }
    public int FirstSeenRelativeMilliseconds { get; set; }
    public int LastSeenRelativeMilliseconds { get; set; }
}

public sealed class FloorEvidenceCoverage
{
    public int CompletedObjectEvidenceScans { get; set; }
    public int FirstObjectEvidenceRelativeMilliseconds { get; set; }
    public int LastObjectEvidenceRelativeMilliseconds { get; set; }
    public bool ObjectEvidenceUnavailable { get; set; }
}

public readonly record struct RawWorldPosition(float X, float Y, float Z)
{
    public static bool CanonicallyEquals(in RawWorldPosition left, in RawWorldPosition right)
    {
        // One micrometre absorbs float subtraction noise at ordinary world-coordinate magnitudes.
        const float epsilon = 0.100001f;
        float dx = left.X - right.X;
        float dy = left.Y - right.Y;
        float dz = left.Z - right.Z;
        return dx * dx + dy * dy + dz * dz <= epsilon * epsilon;
    }
}
