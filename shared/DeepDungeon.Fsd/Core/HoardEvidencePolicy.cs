namespace DeepDungeon.Fsd.Core;

public enum HoardEvidenceState
{
    Disabled,
    AlreadyOpened,
    FloorsetMaxed,
    FloorsetDistributionExcluded,
    BlindSearch,
    IntuitionPending,
    IntuitionNoHoard,
    IntuitionWaitingForIndicator,
    IntuitionActiveUnconfirmed,
    IntuitionDirect
}

public readonly record struct HoardEvidenceSnapshot
{
    public bool BandedEnabled { get; init; }
    public bool HoardOpenedThisFloor { get; init; }
    public FloorsetHoardOpportunity FloorsetHoardOpportunity { get; init; }
    public int? CachedHoardIndicatorRoomIndex { get; init; }
    public bool IntuitionActive { get; init; }
    public bool ChatSaysHoard { get; init; }
    public bool ChatSaysNoHoard { get; init; }
    public bool InheritedNoHoardInferred { get; init; }
    public bool AwaitingIntuitionResult { get; init; }
    public bool UsedIntuitionThisFloor { get; init; }
    public bool ExactHoardObserved { get; init; }
}

public static class HoardEvidencePolicy
{
    public static HoardEvidenceState Resolve(in HoardEvidenceSnapshot snapshot)
    {
        if (!snapshot.BandedEnabled)
        {
            return HoardEvidenceState.Disabled;
        }

        if (snapshot.HoardOpenedThisFloor)
        {
            return HoardEvidenceState.AlreadyOpened;
        }

        if (snapshot.ExactHoardObserved || snapshot.CachedHoardIndicatorRoomIndex.HasValue)
        {
            return HoardEvidenceState.IntuitionDirect;
        }

        if (snapshot.ChatSaysHoard)
        {
            return HoardEvidenceState.IntuitionWaitingForIndicator;
        }

        if (snapshot.ChatSaysNoHoard || snapshot.InheritedNoHoardInferred)
        {
            return HoardEvidenceState.IntuitionNoHoard;
        }

        if (snapshot.AwaitingIntuitionResult)
        {
            return HoardEvidenceState.IntuitionPending;
        }

        if (snapshot.FloorsetHoardOpportunity == FloorsetHoardOpportunity.Maxed)
        {
            return HoardEvidenceState.FloorsetMaxed;
        }

        if (snapshot.FloorsetHoardOpportunity == FloorsetHoardOpportunity.ExcludedByDistribution)
        {
            return HoardEvidenceState.FloorsetDistributionExcluded;
        }

			if (snapshot.IntuitionActive || snapshot.UsedIntuitionThisFloor)
			{
				return HoardEvidenceState.IntuitionActiveUnconfirmed;
			}

        return HoardEvidenceState.BlindSearch;
    }
}
