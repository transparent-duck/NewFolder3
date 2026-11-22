using System.Numerics;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// C1 channel: the player's pose as a fact. FSD fills the ordinary subset
/// (position + combat/dead); profiles with native-motion episodes (FG's
/// ice/arrow/forced march) fill Motion and Statuses so ops and diagnostics
/// can observe "the game owns the body right now" without guessing.
/// </summary>
public enum PoseMotion
{
    Ordinary,
    Jump,
    Ice,
    Forced,
    Steering,
    Arrow,
    Transport,
    Locked,
    Wet
}

public readonly record struct ActorPose(
    Vector3 Feet,
    float Facing,
    float Speed,
    PoseMotion Motion,
    ushort[]? Statuses,
    bool Dead,
    bool InCombat)
{
    internal static ActorPose Ordinary(Vector3 feet, bool dead, bool inCombat) =>
        new(feet, 0f, 0f, PoseMotion.Ordinary, null, dead, inCombat);

    /// <summary>Whether native motion owns the body — used for diagnostics
    /// and for "replan for landing" decisions in ops, never as a claim lock.</summary>
    internal bool NativeOwned => Motion is not PoseMotion.Ordinary;
}
