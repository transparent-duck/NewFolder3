using System.Collections.Generic;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// Fact → site production. Translates the evidence snapshot into
/// ObjectiveSite entries; the ledger consumes this per evidence version.
/// Mode/config filters belong in the pool builder (which sites get
/// produced), not in scoring — a disabled chest type simply isn't
/// produced, which is exactly "removed from the queue".
/// </summary>
internal static class ObjectiveSiteSource
{
    /// <summary>Which site kinds this run's mode produces.</summary>
    internal readonly record struct ObjectiveSiteFilter(
        bool OpenBronze,
        bool OpenSilver,
        bool OpenGold,
        bool Banded,
        bool HuntMobs,      // kill-credit mobs (passage quota)
        bool ProbeRooms,    // unexplored-room prior probes
        bool Exit);         // passage/exit site

    /// <summary>Base rewards by site kind — yield units, tuned later.</summary>
    internal static float RewardOf(ObjectiveSiteKind kind, FloorChestKind chest) =>
        kind switch
        {
            ObjectiveSiteKind.ConfirmedChest => chest switch
            {
                FloorChestKind.Banded => 10f,
                FloorChestKind.Gold => 5f,
                FloorChestKind.Silver => 2f,
                _ => 1f,
            },
            ObjectiveSiteKind.HoardIndicator => 6f,
            // Hoard-class reward: the candidate's p scales it down to
            // 0.35×6≈2.1 — above chests, so probing isn't sorted below
            // empty-room probes (that was the "never probes" inversion).
            ObjectiveSiteKind.Candidate => 6f,
            ObjectiveSiteKind.Hoard => 10f,
            ObjectiveSiteKind.Passage => 50f,
            ObjectiveSiteKind.Quota => 10f,
            ObjectiveSiteKind.Roam => 0.5f,
            ObjectiveSiteKind.RoomProbe => 0.4f,
            _ => 1f,
        };

    /// <summary>
    /// Produce the site set from a live evidence snapshot plus floor
    /// context. Sites get sequential ids in a stable order so the
    /// deterministic tie-break (score desc, id asc) is reproducible;
    /// identity across rebuilds is carried by Key — evidence objects by
    /// entity id, probes/candidates by semantic position, so a moving
    /// mob keeps its commit and status.
    /// </summary>
    internal static List<ObjectiveSite> Produce(
        FloorObjectEvidenceSnapshot evidence,
        ObjectiveSiteFilter filter,
        int playerRoom,
        int spawnRoom,
        IReadOnlyList<(int Room, Vector3 Pos)> roomAnchors,
        IReadOnlyList<(int Room, string Key, Vector3 Pos, float P)> candidates,
        IReadOnlyList<(int Room, Vector3 Pos, string Purpose)> intelProbes)
    {
        var sites = new List<ObjectiveSite>();
        var id = 0;

        foreach (var chest in evidence.Chests)
        {
            var kind = chest.Kind;
            var allow = kind switch
            {
                FloorChestKind.Banded => filter.Banded,
                FloorChestKind.Gold => filter.OpenGold,
                FloorChestKind.Silver => filter.OpenSilver,
                _ => filter.OpenBronze,
            };
            if (!allow)
                continue;
            // A physical banded coffer is a chest: it must execute the
            // chest interaction protocol, not the walk-over probe path.
            sites.Add(new ObjectiveSite(
                id++, ObjectiveSiteKind.ConfirmedChest, -1, chest.Object.Position,
                RewardOf(ObjectiveSiteKind.ConfirmedChest, kind), 3f,
                ObjectiveSiteStatus.Open, Chest: kind,
                Key: $"chest:{chest.Object.EntityId}"));
        }

        foreach (var ind in evidence.HoardIndicators)
        {
            if (!filter.Banded)
                continue;
            sites.Add(new ObjectiveSite(
                id++, ObjectiveSiteKind.HoardIndicator, -1,
                ind.Object.Position, RewardOf(ObjectiveSiteKind.HoardIndicator, 0),
                2f, ObjectiveSiteStatus.Open,
                Key: $"ind:{ind.Object.GameObjectId}"));
        }

        // Passage production moved to the driver: it is a
        // production-gated terminal (open && !inCombat && pool dry),
        // decided after all productive sites are known.

        // Candidate value = calibrated probability — one treasure reward
        // unit, not a weighted 6. Identity is the canonical position key
        // supplied by the producer, stable across pool rebuilds.
        foreach (var (room, key, pos, p) in candidates)
        {
            if (room == spawnRoom)
                continue;   // the spawn room never holds the hoard
            sites.Add(new ObjectiveSite(
                id++, ObjectiveSiteKind.Candidate, room, pos,
                (float)p, 4f,
                ObjectiveSiteStatus.Open,
                Key: key));
        }

        // Room intel work is purpose-scoped: produced only for currently
        // required, not-yet-completed purposes — never every room.
        foreach (var (room, pos, purpose) in intelProbes)
        {
            sites.Add(new ObjectiveSite(
                id++, ObjectiveSiteKind.RoomProbe, room, pos,
                RewardOf(ObjectiveSiteKind.RoomProbe, 0), 6f,
                ObjectiveSiteStatus.Open,
                Key: $"probe:{room}:{purpose}"));
        }

        // Attribute each site to its nearest room anchor — room-scoped
        // queries (searched-state, context room) need ownership even for
        // evidence-born sites produced with RoomIndex = -1.
        for (var i = 0; i < sites.Count; i++)
        {
            var s = sites[i];
            if (s.RoomIndex >= 0)
                continue;
            var best = -1;
            var bestD = 28f * 28f;
            foreach (var (room, pos) in roomAnchors)
            {
                var d = Vector3.DistanceSquared(s.Position, pos);
                if (d < bestD)
                { best = room; bestD = d; }
            }
            if (best >= 0)
                sites[i] = s with { RoomIndex = best };
        }

        return sites;
    }
}
