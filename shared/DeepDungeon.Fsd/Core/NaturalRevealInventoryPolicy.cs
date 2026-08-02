namespace DeepDungeon.Fsd.Core;

public enum NaturalRevealInventoryDecisionKind
{
    EstablishBaseline,
    NoChange,
    AdoptExternalPending
}

public readonly record struct NaturalRevealInventorySnapshot(
    bool BaselineEstablished,
    int PreviousSightCount,
    int PreviousMazerootCount,
    int CurrentSightCount,
    int CurrentMazerootCount,
    bool FsdRevealAlreadyOwned,
    bool MazerootSupported);

public readonly record struct NaturalRevealInventoryDecision(
    NaturalRevealInventoryDecisionKind Kind,
    SightResearchRevealResource Resource);

public static class NaturalRevealInventoryPolicy
{
    public static NaturalRevealInventoryDecision Decide(
        in NaturalRevealInventorySnapshot snapshot)
    {
        if (!snapshot.BaselineEstablished)
        {
            return new NaturalRevealInventoryDecision(
                NaturalRevealInventoryDecisionKind.EstablishBaseline,
                SightResearchRevealResource.None);
        }

        if (snapshot.FsdRevealAlreadyOwned)
        {
            return new NaturalRevealInventoryDecision(
                NaturalRevealInventoryDecisionKind.NoChange,
                SightResearchRevealResource.None);
        }

        bool sightDecreased =
            snapshot.CurrentSightCount < snapshot.PreviousSightCount;
        bool mazerootDecreased =
            snapshot.MazerootSupported &&
            snapshot.CurrentMazerootCount < snapshot.PreviousMazerootCount;
        if (sightDecreased == mazerootDecreased)
        {
            return new NaturalRevealInventoryDecision(
                NaturalRevealInventoryDecisionKind.NoChange,
                SightResearchRevealResource.None);
        }

        return new NaturalRevealInventoryDecision(
            NaturalRevealInventoryDecisionKind.AdoptExternalPending,
            sightDecreased
                ? SightResearchRevealResource.Sight
                : SightResearchRevealResource.Mazeroot);
    }

    public static bool IsAuthoritativeConfirmation(
        SightResearchRevealResource resource,
        bool postBaselineSightLog,
        bool postBaselineMazerootLog,
        bool sightTrapObserved,
        bool mazerootSupported)
    {
        if (resource == SightResearchRevealResource.None)
            return false;
        if (resource == SightResearchRevealResource.Mazeroot &&
            !mazerootSupported)
        {
            return false;
        }
        if (sightTrapObserved)
            return true;
        return resource switch
        {
            SightResearchRevealResource.Sight => postBaselineSightLog,
            SightResearchRevealResource.Mazeroot => postBaselineMazerootLog,
            _ => false
        };
    }
}
