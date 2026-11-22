namespace DeepDungeon.Fsd.Core;

/// <summary>Current HoH beacon availability, not a permanent true/false classification.</summary>
public static class NativePassageAvailability
{
    public static bool IsAvailable(uint baseId, ushort sharedTimelineState) =>
        baseId != 2009507 || sharedTimelineState == 4;

    // Unknown transient states do not retire an already owned transition wait.
    public static bool IsRemoved(uint baseId, ushort sharedTimelineState) =>
        baseId == 2009507 && sharedTimelineState == 0x400;
}
