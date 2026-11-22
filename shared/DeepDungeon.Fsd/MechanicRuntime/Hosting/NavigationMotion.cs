using System.Numerics;

namespace MasterPlayer.MechanicRuntime;

/// <summary>Observed response to ordinary input. Infinity represents an unmeasured, instantaneous response.</summary>
public readonly record struct NavigationMotion(float Acceleration)
{
    public static NavigationMotion Instant => new(float.PositiveInfinity);
    public bool HasInertia => float.IsFinite(Acceleration);

    // Ordinary walking changes heading directly; acceleration applies to speed.
    // Forced movement is supplied by the observation layer as a movement lock.
    public Vector2 Advance(ref Vector2 velocity, Vector2 command, double seconds)
    {
        var before = velocity.Length(); var target = command.Length();
        var direction = target > 1e-6f ? command / target : before > 1e-6f ? velocity / before : Vector2.Zero;
        var rate = Acceleration;
        if (target <= before || !float.IsFinite(rate) || rate <= 0)
        { velocity = command; return command * (float)seconds; }
        var ramp = Math.Min(seconds, Math.Abs(target - before) / rate);
        var after = before + Math.Sign(target - before) * rate * (float)ramp;
        var distance = (before + after) * .5 * ramp + target * (seconds - ramp);
        velocity = direction * after;
        return direction * (float)distance;
    }

    public double TravelSeconds(float distance, float initialSpeed, float speed)
    {
        if (!float.IsFinite(Acceleration) || initialSpeed >= speed) return distance / speed;
        var ramp = (speed - initialSpeed) / Acceleration;
        var rampDistance = (initialSpeed + speed) * .5f * ramp;
        return distance <= rampDistance
            ? (Math.Sqrt(initialSpeed * initialSpeed + 2 * Acceleration * distance) - initialSpeed) / Acceleration
            : ramp + (distance - rampDistance) / speed;
    }
}

public readonly record struct NavigationObservation(Vector2 Position, Vector2 Velocity, DateTime At,
    NavigationMotion Motion, bool CanCorrect = true)
{
    public Vector2 DisplacementVelocity { get; init; }
    public bool MovementLocked { get; init; }
    public bool PendingDisplacement { get; init; }
    public bool ForcedMovement { get; init; }
    public NativeDisplacementObservation? Displacement { get; init; }
}

/// <summary>One observation owns walking response and externally imposed displacement.</summary>
public sealed class NavigationMotionObserver
{
    private NavigationObservation _previous;
    private float _acceleration = float.PositiveInfinity;
    private bool _hadInput;
    public NavigationObservation Current { get; private set; }
    public int AccelerationSamples { get; private set; }
    public void Reset()
    {
        _previous = Current = default;
        _hadInput = false;
        _acceleration = float.PositiveInfinity; AccelerationSamples = 0;
    }
    // Endpoint corrections are our own writes, not a forced movement sample.
    public void CorrectPosition(Vector2 position) => _previous = _previous with { Position = position };
    public NavigationObservation Observe(Vector2 position, Vector2 nativeVelocity, bool hasInput,
        float targetSpeed, DateTime at, bool canCorrect = true, bool pendingKnockback = false, bool movementLocked = false)
    {
        var dt = (at - _previous.At).TotalSeconds;
        var sampled = _previous.At != default && dt is > .001 and < .1;
        var displacement = sampled ? position - _previous.Position : Vector2.Zero;
        var actualVelocity = sampled ? displacement / (float)dt : Vector2.Zero;
        var before = _previous.Velocity.Length(); var speed = nativeVelocity.Length();
        // Position deltas are telemetry only. They do not identify a mechanic.
        // Pending removal releases the pending hold, not a claimed landing state.
        var locked = movementLocked;
        if (sampled && canCorrect && !locked && !pendingKnockback && !_previous.MovementLocked && !_previous.PendingDisplacement)
        {
            var rate = (speed - before) / (float)dt;
            if (hasInput && _hadInput && targetSpeed > speed + .01f && rate > .1f)
            { _acceleration = Blend(_acceleration, rate); ++AccelerationSamples; }
        }
        _hadInput = hasInput;
        return Current = _previous = new(position, nativeVelocity, at, new(_acceleration), canCorrect && !locked && !pendingKnockback)
        { DisplacementVelocity = actualVelocity, MovementLocked = locked, PendingDisplacement = pendingKnockback, ForcedMovement = false };
    }
    private static float Blend(float previous, float sample) => float.IsFinite(previous) ? previous * .8f + sample * .2f : sample;
}
