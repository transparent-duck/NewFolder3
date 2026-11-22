using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

/// <summary>One camera writer for the entire FSD session. Camera framing does
/// not grant or block movement, combat, or chest interaction.</summary>
internal sealed unsafe class FsdCameraController
{
    private DateTime _lastTick;
    private Vector3 _lastPosition;
    private float _yaw, _velocityH, _velocityV;
    internal bool NorthLocked { get; private set; }
    internal float? ObservedHeading { get; private set; }
    internal float? AppliedHeading { get; private set; }

    internal void Update(bool active, Vector3? chest, DateTime now, bool orbiting = false)
    {
        // FSD or the active boss orbit owns framing. Yield to camera input.
        NorthLocked = false;
        var player = Service.LocalPlayer;
        var manager = CameraManager.Instance();
        var camera = manager == null ? null : manager->GetActiveCamera();
        ObservedHeading = camera == null ? null : camera->DirH;
        AppliedHeading = null;
        if (!active || player == null || camera == null || manager->ActiveCameraIndex != 0)
        {
            _lastTick = default;
            _velocityH = _velocityV = 0;
            return;
        }
        var position = player.Position;
        var dt = _lastTick == default ? 0f : Math.Clamp((float)(now - _lastTick).TotalSeconds, 0f, .05f);
        var movement = _lastTick == default ? Vector3.Zero : position - _lastPosition;
        if (_lastTick == default) _yaw = camera->DirH;
        _lastPosition = position;
        _lastTick = now;
        if (GameCamera.GetCameraInputSource() != FFXIVClientStructs.FFXIV.Client.Game.CameraInputSource.None)
        {
            _yaw = camera->DirH;
            _velocityH = _velocityV = 0;
            return;
        }
        // Follow the committed chest throughout approach and interaction.
        // Otherwise follow actual travel (including boss movement); while
        // stationary keep the last heading, just as a path-following camera.
        var direction = chest.HasValue ? chest.Value - position : movement;
        if (orbiting)
        {
            NorthLocked = true;
            _yaw = 0; // Native Camera.DirH: zero is north.
        }
        else if (direction.X * direction.X + direction.Z * direction.Z > .000001f)
            _yaw = Normalize(MathF.Atan2(direction.X, direction.Z) + MathF.PI);
        var desiredH = camera->DirH + Normalize(_yaw - camera->DirH);
        camera->DirH = Normalize(Smooth(camera->DirH, desiredH, ref _velocityH, dt));
        camera->DirV = Math.Clamp(Smooth(camera->DirV, -.45f, ref _velocityV, dt), camera->DirVMin, camera->DirVMax);
        AppliedHeading = camera->DirH;
    }

    internal static float Normalize(float angle) => MathF.IEEERemainder(angle, MathF.Tau);
    // Critically damped approach, bounded angular speed and no overshoot.
    internal static float Smooth(float current, float target, ref float velocity, float dt)
    {
        if (dt <= 0) return current;
        const float time = .25f, omega = 2 / time;
        var x = omega * dt;
        var decay = 1 / (1 + x + .48f * x * x + .235f * x * x * x);
        var change = Math.Clamp(current - target, -MathF.Tau * time, MathF.Tau * time);
        var adjusted = current - change;
        var step = (velocity + omega * change) * dt;
        velocity = (velocity - omega * step) * decay;
        var output = adjusted + (change + step) * decay;
        if ((target - current > 0) == (output > target)) { velocity = 0; return target; }
        return output;
    }
}
