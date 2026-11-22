namespace DeepDungeon.Fsd.Core;

/// <summary>Shared retry cadence. Each interaction keeps its own pending state and deadline.</summary>
public static class InteractionTiming
{
    private static readonly TimeSpan NormalRetryInterval = TimeSpan.FromSeconds(1);

    public static TimeSpan GetRetryInterval(bool aggressive = false) =>
        aggressive ? TimeSpan.Zero : NormalRetryInterval;

    public static int GetRetryMilliseconds(bool aggressive = false) =>
        (int)GetRetryInterval(aggressive).TotalMilliseconds;
}
