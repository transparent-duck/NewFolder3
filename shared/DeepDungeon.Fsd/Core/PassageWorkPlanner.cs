using System;
using System.Collections.Generic;

namespace DeepDungeon.Fsd.Core
{
    public readonly record struct PassageWorkSnapshot
    {
        public bool PassageOpen { get; init; }
        public bool HoardWorkResolved { get; init; }
        public bool VisibleBandedWork { get; init; }
        public IReadOnlyList<RoomPlanEntry> PlannedRoute { get; init; }
    }

    public readonly record struct PassageWorkDecision
    {
        public IReadOnlyList<RoomPlanEntry> PlannedRoute { get; init; }
        public bool RetainVisibleBandedWork { get; init; }
        public bool ShouldExit { get; init; }
    }

    public static class PassageWorkPlanner
    {
        public static PassageWorkDecision Decide(in PassageWorkSnapshot snapshot)
        {
            var route = snapshot.PlannedRoute ?? Array.Empty<RoomPlanEntry>();
            if (!snapshot.PassageOpen || snapshot.VisibleBandedWork)
            {
                return new PassageWorkDecision
                {
                    PlannedRoute = route,
                    RetainVisibleBandedWork = snapshot.VisibleBandedWork,
                    ShouldExit = false
                };
            }

            var retained = snapshot.HoardWorkResolved
                ? Array.Empty<RoomPlanEntry>()
                : RetainRequiredHoardWork(route);
            bool retainVisibleBanded = snapshot.VisibleBandedWork;
            return new PassageWorkDecision
            {
                PlannedRoute = retained,
                RetainVisibleBandedWork = retainVisibleBanded,
                ShouldExit = snapshot.HoardWorkResolved && retained.Count == 0 && !retainVisibleBanded
            };
        }

        private static IReadOnlyList<RoomPlanEntry> RetainRequiredHoardWork(IReadOnlyList<RoomPlanEntry> route)
        {
            var retained = new List<RoomPlanEntry>(route.Count);
            for (int i = 0; i < route.Count; i++)
            {
                var entry = route[i];
                if (IsRequiredHoardWork(entry))
                {
                    retained.Add(entry);
                }
            }

            return retained;
        }

        internal static bool IsRequiredHoardWork(in RoomPlanEntry entry)
        {
            return entry.HoardEvidenceState switch
            {
                HoardEvidenceState.IntuitionDirect => entry.ShouldProbeHoard,
                HoardEvidenceState.IntuitionWaitingForIndicator => entry.ShouldVisitForIntel,
                HoardEvidenceState.IntuitionActiveUnconfirmed => entry.ShouldVisitForIntel,
                HoardEvidenceState.BlindSearch => entry.ShouldProbeHoard,
                _ => false
            };
        }
    }
}
