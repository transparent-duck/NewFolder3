using System.Text.Json.Serialization;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

public enum FsdStartScenario
{
    Farming,
    ControlledSurvey
}

public sealed record FsdStartRequest(
    int? StopAfterFloor = null,
    int? DiagnosticStartFloor = null,
    int? ResumeSlot = null,
    bool HoldOnFailure = false,
    FsdStartScenario? Scenario = null);

public sealed record FsdFarmingRequest(
    FarmingMode Mode,
    SaveUse SaveUse,
    int StartFloor,
    int Cycles,
    bool Infinite,
    FarmingTargets Targets,
    FsdStartRequest? Checkpoint = null);

/// <summary>Typed control outcome; diagnostic payloads retain their existing bridge schema.</summary>
public sealed record FsdControlResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null,
    [property: JsonPropertyName("scenario"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scenario = null,
    [property: JsonPropertyName("snapshot"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Snapshot = null);
