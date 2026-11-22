using System;
using System.Numerics;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>A sampled fact with provenance: when it was observed and which sample it came from.</summary>
internal readonly record struct Observation<T>(T Value, DateTime AtUtc, long Seq);

/// <summary>
/// Channel that only adopts a new reading after it persists across N consecutive
/// differing samples. Transient flapping (e.g. the game's room index at a
/// boundary) never becomes the established value, so consumers react to
/// sustained truth instead of single-sample noise.
/// </summary>
internal sealed class Sustained<T> where T : notnull
{
    private readonly int _requiredSamples;
    private T? _pending;
    private int _pendingCount;
    private long _seq;

    internal Sustained(T initial, int requiredSamples = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requiredSamples, 1);
        _requiredSamples = requiredSamples;
        Current = new Observation<T>(initial, DateTime.MinValue, 0);
    }

    internal Observation<T> Current { get; private set; }
    internal T? Pending => _pending;

    /// <summary>Returns true when the sample established a new value this call.</summary>
    internal bool Sample(T value, DateTime now)
    {
        if (EqualityComparer<T>.Default.Equals(value, Current.Value))
        {
            _pending = default;
            _pendingCount = 0;
            return false;
        }
        if (!EqualityComparer<T>.Default.Equals(value, _pending))
        {
            _pending = value;
            _pendingCount = 1;
        }
        else if (++_pendingCount < _requiredSamples)
        {
            return false;
        }
        _pending = default;
        _pendingCount = 0;
        Current = new Observation<T>(value, now, ++_seq);
        return true;
    }
}
