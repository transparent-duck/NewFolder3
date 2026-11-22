using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

namespace DeepDungeon.Fsd.Dalamud.Runtime;

/// <summary>
/// What this run's mode is allowed and required to do, derived once per
/// decision from the farming plan and live run options.
/// Not a framework — just the mode rules in one place.
/// </summary>
internal readonly record struct FloorWorkPolicy(
    bool SearchHoard,
    bool DiscoveryOnly,
    bool HarvestChests,
    bool OpenGold,
    bool OpenSilver,
    bool OpenBronze);

internal static class FloorWorkPolicyBuilder
{
    internal static FloorWorkPolicy Build(FarmingPlan? plan, RunOptions options)
    {
        var mode = plan?.Mode ?? FarmingMode.Hoard;
        var harvest = mode == FarmingMode.Aetherpool || options.HarvestChestsRequired;
        var discoveryOnly = mode == FarmingMode.HoardDiscovery || options.DiscoveryOnly;
        // Hoard/null-normal-with-Banded: eligible hoard search. Deep
        // progression climbs; aetherpool harvests. Discovery SEARCHES for
        // the coffer — only its opening stays off.
        var searchHoard = options.BandedEnabled && !harvest &&
            mode != FarmingMode.DeepProgression;
        // Progression opens configured chests along the route. Missing
        // pomanders do not create room visits or mandatory chest detours.
        var openGold = !discoveryOnly && options.OpenGold;
        var openSilver = !discoveryOnly && options.OpenSilver;
        var openBronze = !discoveryOnly && options.OpenBronze;
        return new FloorWorkPolicy(
            searchHoard, discoveryOnly, harvest,
            openGold, openSilver, openBronze);
    }
}
