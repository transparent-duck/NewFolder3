namespace DeepDungeon.Fsd.Core
{
    public readonly record struct FloorObjectEvidenceRefreshSnapshot(
        long NowMs,
        long LastRefreshMs,
        long InvalidationVersion,
        long ConsumedInvalidationVersion,
        bool HasSnapshot,
        long RefreshIntervalMs);

    public readonly record struct FloorObjectEvidenceRefreshDecision(
        bool ShouldRefresh,
        bool WasInvalidated);

    public static class FloorObjectEvidenceRefreshPlanner
    {
        public static FloorObjectEvidenceRefreshDecision Decide(in FloorObjectEvidenceRefreshSnapshot snapshot)
        {
            bool invalidated = snapshot.InvalidationVersion > snapshot.ConsumedInvalidationVersion;
            bool due = !snapshot.HasSnapshot ||
                       snapshot.LastRefreshMs == 0 ||
                       snapshot.NowMs - snapshot.LastRefreshMs >= snapshot.RefreshIntervalMs;
            return new FloorObjectEvidenceRefreshDecision(due || invalidated, invalidated);
        }

        public static long NextMaterialVersion(long currentVersion, bool hasSnapshot, bool materialChanged)
        {
            return !hasSnapshot || materialChanged ? currentVersion + 1 : currentVersion;
        }

        public static long NextCompletedScanCount(long currentCount, bool scanCompleted)
        {
            return scanCompleted ? currentCount + 1 : currentCount;
        }
    }
}
