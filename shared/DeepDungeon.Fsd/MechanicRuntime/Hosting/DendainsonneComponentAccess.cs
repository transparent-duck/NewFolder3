using BossMod.Heavensward.DeepDungeon.PalaceOfTheDead.DD180Dendainsonne;

namespace MasterPlayer.MechanicRuntime;

public static class DendainsonneComponentAccess
{
    public static void DisableManualBurst(DD180Dendainsonne module)
    {
        if (module.FindComponent<ManualBurst>() != null)
            module.DeactivateComponent<ManualBurst>();
    }
}
