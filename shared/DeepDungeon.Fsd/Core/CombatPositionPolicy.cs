using System.Numerics;

namespace DeepDungeon.Fsd.Core;

/// <summary>Conservative bounds for a local ground-position recovery, not ordinary long-distance pursuit.</summary>
public static class CombatPositionPolicy
{
    public const float StandingMargin = 0.7f;
    public const float RangeMargin = 1.0f;
    public const float MaximumSearchDistance = 15f;
    public const float MaximumPathLength = 35f;
    public const float ArrivalRadius = 0.3f;
    public const float MaximumEndpointHorizontalError = 0.35f;
    public const float MaximumEndpointVerticalError = 0.5f;
    public static readonly TimeSpan AttemptLimit = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan UnknownNoProgressLimit = TimeSpan.FromSeconds(15);
    public static readonly Vector2[] Directions =
    [
        new(1, 0), new(0.70710677f, 0.70710677f), new(0, 1), new(-0.70710677f, 0.70710677f),
        new(-1, 0), new(-0.70710677f, -0.70710677f), new(0, -1), new(0.70710677f, -0.70710677f)
    ];

    public static bool IsFinite(Vector3 point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);

    public static bool WithinRange(Vector3 position, Vector3 target, float actionRange,
        float playerRadius, float targetRadius, float yOffset, float margin)
    {
        if (!IsFinite(position) || !IsFinite(target) || !float.IsFinite(actionRange) || actionRange <= 0)
            return false;
        float horizontal = actionRange + Math.Max(0, playerRadius) + Math.Max(0, targetRadius) - margin;
        float vertical = Math.Max(actionRange, targetRadius) - margin;
        float dx = position.X - target.X, dz = position.Z - target.Z;
        return horizontal > 0 && vertical > 0 && dx * dx + dz * dz <= horizontal * horizontal &&
               Math.Abs(position.Y + yOffset - target.Y) <= vertical;
    }

    public static bool ValidatePathEndpoint(Vector3 start, Vector3 requested,
        IReadOnlyList<Vector3>? path, out float length, out string reason)
    {
        length = 0;
        if (!IsFinite(start) || !IsFinite(requested) || path == null || path.Count == 0)
        { reason = "invalid-path"; return false; }
        var actual = path[^1];
        if (!IsFinite(actual)) { reason = "non-finite-path"; return false; }
        float dx = actual.X - requested.X, dz = actual.Z - requested.Z;
        if (dx * dx + dz * dz > MaximumEndpointHorizontalError * MaximumEndpointHorizontalError ||
            Math.Abs(actual.Y - requested.Y) > MaximumEndpointVerticalError)
        { reason = "path-endpoint-mismatch"; return false; }
        var previous = start;
        foreach (var point in path)
        {
            if (!IsFinite(point)) { reason = "non-finite-path"; return false; }
            length += Vector3.Distance(previous, point);
            previous = point;
        }
        if (length > MaximumPathLength) { reason = "path-too-long"; return false; }
        reason = "endpoint-verified";
        return true;
    }
}

/// <summary>Floor-scoped failure accounting survives target and objective replacement.</summary>
public sealed class CombatRecoveryBudget
{
    public const int MaximumFailuresPerTarget = 2;
    public const int MaximumFailuresPerFloor = 8;
    private readonly Dictionary<ulong, int> _failures = new();
    public int TotalFailures { get; private set; }
    public bool Exhausted => TotalFailures >= MaximumFailuresPerFloor;
    public bool HasExhaustedTargets => _failures.Values.Any(count => count >= MaximumFailuresPerTarget);
    public bool AllRecordedTargetsExhausted => _failures.Count > 0 && _failures.Values.All(count => count >= MaximumFailuresPerTarget);
    public bool IsTargetExhausted(ulong targetId) =>
        _failures.TryGetValue(targetId, out int count) && count >= MaximumFailuresPerTarget;
    public void RecordFailure(ulong targetId)
    {
        _failures.TryGetValue(targetId, out int count);
        _failures[targetId] = count + 1;
        TotalFailures++;
    }
}
