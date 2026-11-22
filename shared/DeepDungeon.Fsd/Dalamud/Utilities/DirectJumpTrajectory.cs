using System.Numerics;

namespace DeepDungeon.Fsd.Dalamud.Utilities;

/// <summary>Request delay followed by a ballistic vertical flight and horizontal movement.</summary>
public readonly record struct DirectJumpTrajectory(float VerticalSpeed, float Gravity, float RequestDelay)
{
    // Fit to native character positions, 2026-10-07; RMS height error 0.006m over the free flight samples.
    public static DirectJumpTrajectory Observed => new(10.495f, 30.461f, .0992f);
    public float MaximumRise => VerticalSpeed * VerticalSpeed / (2 * Gravity);
    public float ApexTime => RequestDelay + VerticalSpeed / Gravity;
    public float Height(float requestTime)
    {
        float t = Math.Max(0, requestTime - RequestDelay);
        return VerticalSpeed * t - .5f * Gravity * t * t;
    }
    public float LandingTime(float rise)
    {
        float discriminant = VerticalSpeed * VerticalSpeed - 2 * Gravity * rise;
        return discriminant >= 0 ? RequestDelay + (VerticalSpeed + MathF.Sqrt(discriminant)) / Gravity : float.NaN;
    }
    public Vector3 Position(Vector3 origin, Vector2 velocity, float time) =>
        origin + new Vector3(velocity.X * time, Height(time), velocity.Y * time);
}
