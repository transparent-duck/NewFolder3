using System;
using System.Collections.Generic;

namespace DeepDungeon.Fsd.Core
{
    public readonly record struct LateHoardEvidenceSnapshot
    {
        public long PendingVersion { get; init; }
        public long ReconciledVersion { get; init; }
        public bool StableNormalFloor { get; init; }
        public bool EvidenceMatchesCurrentFloor { get; init; }
        public bool FloorActiveAllowsReplan { get; init; }
        public bool MandatoryHoardWorkResolved { get; init; }
        public bool PendingOrVisibleBandedWork { get; init; }
        public IReadOnlyList<RoomPlanEntry> RefreshedPlan { get; init; }
    }

    public readonly record struct LateHoardEvidenceDecision
    {
        public bool ShouldRegeneratePlan { get; init; }
        public bool HasRequiredHoardWork { get; init; }
        public bool ShouldResumeHoardWork { get; init; }
        public bool BlockUnroutableMandatoryWork { get; init; }
    }

    public readonly record struct EvidenceVersionAcknowledgement(
        long ReconciledVersion,
        bool RefreshPending);

    public static class LateHoardEvidencePlanner
    {
        public static EvidenceVersionAcknowledgement AcknowledgeVersion(
            long pendingVersion,
            long reconciledVersion,
            long consumedVersion)
        {
            long acknowledged = Math.Max(reconciledVersion, Math.Min(consumedVersion, pendingVersion));
            return new EvidenceVersionAcknowledgement(
                acknowledged,
                pendingVersion > acknowledged);
        }

        public static LateHoardEvidenceDecision Decide(in LateHoardEvidenceSnapshot snapshot)
        {
            bool shouldRegenerate =
                snapshot.PendingVersion > snapshot.ReconciledVersion &&
                snapshot.StableNormalFloor &&
                snapshot.EvidenceMatchesCurrentFloor &&
                snapshot.FloorActiveAllowsReplan;
            if (!shouldRegenerate)
            {
                return default;
            }

            var plan = snapshot.RefreshedPlan ?? Array.Empty<RoomPlanEntry>();
            for (int i = 0; i < plan.Count; i++)
            {
                if (PassageWorkPlanner.IsRequiredHoardWork(plan[i]))
                {
                    return new LateHoardEvidenceDecision
                    {
                        ShouldRegeneratePlan = true,
                        HasRequiredHoardWork = true,
                        ShouldResumeHoardWork = true
                    };
                }
            }

            if (snapshot.PendingOrVisibleBandedWork)
            {
                return new LateHoardEvidenceDecision
                {
                    ShouldRegeneratePlan = true,
                    ShouldResumeHoardWork = true
                };
            }

            return new LateHoardEvidenceDecision
            {
                ShouldRegeneratePlan = true,
                BlockUnroutableMandatoryWork = !snapshot.MandatoryHoardWorkResolved
            };
        }
    }
}
