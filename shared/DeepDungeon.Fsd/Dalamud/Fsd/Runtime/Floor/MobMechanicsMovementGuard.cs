namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

/// <summary>Give the selected BMR AI exclusive movement during a mob cast and its resolution.</summary>
public sealed class MobMechanicsMovementGuard
{
    private DateTime _until;
    public bool Update(bool enabled, bool hostileCasting, int forbiddenZones, DateTime now)
    {
        if (!enabled) _until = default;
        else if (hostileCasting && forbiddenZones > 0) _until = now.AddSeconds(0.75);
        return enabled && now < _until;
    }
}
