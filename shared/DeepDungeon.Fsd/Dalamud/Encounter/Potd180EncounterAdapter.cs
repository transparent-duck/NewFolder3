using BossMod;
using BossMod.Heavensward.DeepDungeon.PalaceOfTheDead.DD180Dendainsonne;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Encounter;

internal static class Potd180EncounterAdapter
{
    internal static void BeforeHints(BossModule? module)
    {
        // The boss reactivates ManualBurst at each phase. FSD keeps attacking
        // through its HP window while retaining the other encounter hints.
        if (module is DD180Dendainsonne dendainsonne)
            DendainsonneComponentAccess.DisableManualBurst(dendainsonne);
    }
}
