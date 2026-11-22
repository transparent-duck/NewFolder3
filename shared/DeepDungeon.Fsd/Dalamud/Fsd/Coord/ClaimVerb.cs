using System.Numerics;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// A movement-intent verb: the claim's payload IS the kind — the arbiter
/// never inspects it, the winning boundary type-switches it into execution.
/// Shared verbs are interpreted by every boundary (GoTo/Hold plus common
/// primitives); profile-owned verbs (e.g. FG's RouteExecution.Command)
/// implement this interface in their own assembly and only their own
/// boundary understands them — unknown verbs fail loudly, not silently.
/// </summary>
public interface IClaimVerb
{
    /// <summary>Short diagnostic text for coord-tick/shadow records.</summary>
    string Describe();
}

internal readonly record struct AttackTargetVerb(ulong TargetId, Vector3 Position) : IClaimVerb
{
    public string Describe() => $"attack({TargetId:X})";
}

internal readonly record struct ChestTargetVerb(ulong TargetId, Vector3 Position, DeepDungeon.Fsd.Core.NavigationRange Range) : IClaimVerb
{
    public string Describe() => $"chest({TargetId:X})";
}

public readonly record struct GoToVerb(Vector3 Dest, float Radius) : IClaimVerb
{
    public NavigationPath? Path { get; init; }
    internal ulong EncounterId { get; init; }
    public string Describe() => $"goto({Dest.X:F1},{Dest.Z:F1}) r{Radius:F1}";
}

/// <summary>Assert staying stopped; Until is diagnostic metadata only —
/// the arbiter does not enforce it (waiting is a choice, not a lock).</summary>
public readonly record struct HoldVerb(double? Until = null) : IClaimVerb
{
    public string Describe() => Until is { } u ? $"hold(until {u:F2})" : "hold";
}

// Shared primitive verbs — interpreted by the shared interpreter in every
// boundary. Present so profiles can opt in without redefining payloads;
// FSD has no consumer today, FG/raids will.
public readonly record struct DirectionalVerb(Vector2 Direction, Vector3? Target, float? Radius, Vector2? Force) : IClaimVerb
{
    public string Describe() => $"walk({Direction.X:F2},{Direction.Y:F2})";
}

public readonly record struct JumpVerb(Vector2 Direction, Vector3? Takeoff) : IClaimVerb
{
    public string Describe() => $"jump({Direction.X:F2},{Direction.Y:F2})";
}

public readonly record struct FaceVerb(float Heading) : IClaimVerb
{
    public string Describe() => $"face({Heading:F2})";
}
