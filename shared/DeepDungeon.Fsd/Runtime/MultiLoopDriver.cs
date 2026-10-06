namespace DeepDungeon.Fsd.Runtime;

/// <summary>Loop progress that survives the replacement of an individual run.</summary>
public sealed class MultiLoopDriver
{
    public int TargetLoops { get; private set; }
    public int CompletedLoops { get; private set; }
    public bool InfiniteLoop { get; private set; }
    public string LastStopReason { get; private set; } = string.Empty;
    public bool LastStopWasError => false;

    public MultiLoopDriver(int targetLoops, bool infinite) => SetTargets(targetLoops, infinite);

    public void SetTargets(int cycles, bool infinite)
    {
        TargetLoops = Math.Max(1, cycles);
        InfiniteLoop = infinite;
    }

    public void IncrementLoop() => CompletedLoops++;

    /// <summary>The current run has completed but has not yet been added to CompletedLoops.</summary>
    public bool ShouldStopAfterCurrentRun()
    {
        if (InfiniteLoop || CompletedLoops + 1 < TargetLoops)
        {
            LastStopReason = string.Empty;
            return false;
        }

        LastStopReason = $"Loop target reached ({CompletedLoops + 1}/{TargetLoops})";
        return true;
    }
}
