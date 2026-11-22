using System;
using System.Collections.Generic;

namespace DeepDungeon.Fsd.Core
{
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

    }
}
