namespace DeepDungeon.Fsd.Core;

/// <summary>Gives a completed chase leg time to attack and retains no-progress evidence across moving targets.</summary>
public sealed class EnemyChaseAttackWindow
{
    public static readonly TimeSpan AttackDwell = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan NoProgressLimit = TimeSpan.FromSeconds(15);
    private bool _wasInRange;
    private DateTime _holdUntil;
    public DateTime StartedAt { get; private set; }

    public void Observe(bool inRange, bool legArrived, DateTime nowUtc)
    {
        if (legArrived || inRange && !_wasInRange)
        {
            if (StartedAt == default) StartedAt = nowUtc;
            _holdUntil = nowUtc + AttackDwell;
        }
        _wasInRange = inRange;
    }

    public bool IsHolding(DateTime nowUtc) => nowUtc < _holdUntil;
    public bool NeedsRecovery(DateTime nowUtc) => StartedAt != default && nowUtc - StartedAt >= NoProgressLimit;
    public void Reset()
    {
        StartedAt = _holdUntil = default;
        _wasInRange = false;
    }
}
