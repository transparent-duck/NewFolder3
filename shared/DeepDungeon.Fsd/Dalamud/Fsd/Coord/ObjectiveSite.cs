using System;
using System.Collections.Generic;
using System.Numerics;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// A schedulable point of interest — the commitment unit of the site-pool
/// planner. Value lives at point granularity (v = p · r); commitment lives
/// at site granularity (a room's candidate cluster sweeps as one stop,
/// because the second candidate inside a room costs ~0 to check).
/// </summary>
internal enum ObjectiveSiteKind
{
    ConfirmedChest,   // seen chest (gold/silver/bronze) — p = 1
    Candidate,        // catalog/prior spawn point — p = posterior
    HoardIndicator,   // probe whose reward is information
    Hoard,            // confirmed hoard position
    Passage,          // floor exit
    Interactable,     // gated by a predicate (e.g. boss dead)
    Quota,            // synthetic kill-deficit site — position delegated
                      // to the combat chase supply; done = quota cleared
    Roam,             // pseudo-site: patrol stale rooms when the pool is dry
    RoomProbe,        // unexplored-room prior probe
    PassageSearch,    // visit an area to observe the currently unloaded exit
}

internal enum ObjectiveSiteStatus
{
    Open,
    Committed,
    Done,
    Falsified,
    Unreachable,
    Retryable,   // not terminal — may become viable again
}

internal readonly record struct ObjectiveSite(
    int Id,
    ObjectiveSiteKind Kind,
    int RoomIndex,
    Vector3 Position,
    float Value,            // p · r — expected yield
    float TaskCost,         // seconds on-site (kill loot, interact, probe)
    ObjectiveSiteStatus Status,
    string? Reason = null,
    Runtime.Floor.FloorChestKind Chest = Runtime.Floor.FloorChestKind.Bronze,
    string Key = "",        // stable identity across pool rebuilds
    ulong ActorId = 0);     // exact actor owned by entity-bound work
