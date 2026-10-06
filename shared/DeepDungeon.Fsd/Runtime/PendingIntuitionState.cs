namespace DeepDungeon.Fsd.Runtime;

public sealed class PendingIntuitionState
{
	private bool _hasPending;
	private byte _sourceFloor;
	private DateTime _lastUsedAtUtc = DateTime.MinValue;
	private long _attemptId;
	private bool _overdueRecorded;

	public void Reset()
	{
		_hasPending = false;
		_sourceFloor = 0;
		_lastUsedAtUtc = DateTime.MinValue;
		_attemptId = 0;
		_overdueRecorded = false;
	}

	public void MarkUsed(byte sourceFloor, DateTime usedAtUtc, long attemptId)
	{
		_hasPending = true;
		_sourceFloor = sourceFloor;
		_lastUsedAtUtc = usedAtUtc;
		_attemptId = attemptId;
		_overdueRecorded = false;
	}

	public void CancelAttempt(long attemptId)
	{
		if (!_hasPending || attemptId == 0 || attemptId != _attemptId)
			return;

		Reset();
	}

	public bool TryMarkResolved(long attemptId)
	{
		if (!_hasPending || attemptId == 0 || attemptId != _attemptId)
			return false;

		Reset();
		return true;
	}

	public bool TryMarkOverdueRecorded()
	{
		if (!_hasPending || _overdueRecorded)
			return false;

		_overdueRecorded = true;
		return true;
	}

	public bool TryGetCurrentFloorUseElapsedMilliseconds(byte floor, DateTime nowUtc, out int elapsedMilliseconds)
	{
		elapsedMilliseconds = 0;
		if (!_hasPending ||
		    _sourceFloor != floor ||
		    _lastUsedAtUtc == DateTime.MinValue)
		{
			return false;
		}

		elapsedMilliseconds = (int)Math.Max(0, (nowUtc - _lastUsedAtUtc).TotalMilliseconds);
		return true;
	}
}
