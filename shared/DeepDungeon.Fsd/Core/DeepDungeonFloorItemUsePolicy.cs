namespace DeepDungeon.Fsd.Core;

public static class DeepDungeonFloorItemUsePolicy
{
    /// <summary>Next-floor-effect guard: pomanders whose effect carries
    /// INTO the next floor (Affluence/Flight/Alteration) are withheld
    /// when the NEXT floor is not a mob floor — the effect would be wasted.
    /// Fortune affects current-floor enemy drops and is NOT withheld.
    /// Every other slot and every mob floor remains allowed.</summary>
    public static bool AllowsNextFloorEffect(uint slotIndex, bool nextFloorIsMob) =>
        nextFloorIsMob || slotIndex is not (
            FloorInitPlanner.AffluencePomanderSlotIndex or
            FloorInitPlanner.FlightPomanderSlotIndex or
            FloorInitPlanner.AlterationPomanderSlotIndex);

    public const byte PomanderUseProhibitedBanId = 1;
    public const byte PtIncenseUseProhibitedBanId = 7;

    public static bool CanUsePomanders(byte banId) =>
        banId != PomanderUseProhibitedBanId;

    public static bool CanUsePomander(byte banId, uint slot) =>
        CanUsePomanders(banId) || slot == FloorInitPlanner.SerenityPomanderSlotIndex;

    public static bool CanUsePtIncense(byte banId) =>
        banId != PtIncenseUseProhibitedBanId;

    // DeepDungeonBan -> DeepDungeonFloorEffectUI identifies separate bans.
    public static bool CanUseStones(uint dungeonId, byte banId) => dungeonId switch
    {
        2 => banId != 5,
        3 => banId != 6,
        4 => CanUsePtIncense(banId),
        _ => false
    };
}
