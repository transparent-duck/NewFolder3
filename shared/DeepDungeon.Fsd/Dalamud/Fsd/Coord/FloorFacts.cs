using System;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Runtime;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// The per-tick fact bundle every layer reads. Assembled once from the
/// observation channels so operations never reach into game state directly.
/// </summary>
internal readonly record struct FloorFacts(
    int Floor,
    int RoomIndex,
    Observation<int> RoomIndexObs,
    Vector3 PlayerPosition,
    ActorPose Pose,
    bool InCombat,
    bool PlayerDead,
    bool PassageOpen,
    FloorObjectEvidenceSnapshot? Evidence,
    Vector3? PassageDestination = null,
    Vector3? PassageRoomCenter = null,
    int PassageRoomIndex = -1,
    /// <summary>The RAW passage actor position when loaded — distinct from
    /// <see cref="PassageDestination"/>, which is the projected walking
    /// point. Null when no actor is observed or the caller only supplied
    /// the walking point.</summary>
    Vector3? PassageActorPosition = null);

/// <summary>
/// The normal floor's live activity pair, sampled ONCE per decision tick —
/// passage state and combat fact invalidate route selection immediately.
/// </summary>
internal readonly record struct FloorActivity(bool PassageOpen, bool InCombat);

/// <summary>
/// Owns the observation channels for one floor runtime. Game-facing samplers
/// push raw reads in; consumers only see the assembled <see cref="FloorFacts"/>.
/// </summary>
internal sealed class FloorObservation
{
    internal Sustained<int> RoomIndex { get; } = new(-1, requiredSamples: 3);

    internal FloorFacts Assemble(
        int floor,
        int rawRoomIndex,
        DateTime now,
        Vector3 playerPosition,
        bool inCombat,
        bool playerDead,
        bool passageOpen,
        FloorObjectEvidenceSnapshot? evidence,
        Vector3? passageDestination = null,
        Vector3? passageRoomCenter = null,
        int passageRoomIndex = -1,
        Vector3? passageActorPosition = null)
    {
        RoomIndex.Sample(rawRoomIndex, now);
        return new FloorFacts(
            floor,
            RoomIndex.Current.Value,
            RoomIndex.Current,
            playerPosition,
            ActorPose.Ordinary(playerPosition, playerDead, inCombat),
            inCombat,
            playerDead,
            passageOpen,
            evidence,
            passageDestination,
            passageRoomCenter,
            passageRoomIndex,
            passageActorPosition);
    }
}
