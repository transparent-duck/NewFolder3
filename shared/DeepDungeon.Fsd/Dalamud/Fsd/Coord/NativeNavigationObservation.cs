using System.Numerics;
using DeepDungeon.Fsd.Dalamud.Utilities;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Coord;

public sealed class NativeNavigationObservation
{
    private readonly NavigationMotionObserver _motion = new();
    public NavigationObservation Capture(Vector3 position, DateTime now, bool movementLocked = false, bool pendingKnockback = false)
    {
        var velocity = Vector2.Zero;
        var input = false; var target = 0f; var grounded = true;
        if (DirectMovementController.TryReadNativeMotion(out var native))
        {
            var direction = new Vector2(native.Direction.X, native.Direction.Z);
            if (direction.LengthSquared() > 1e-6f) velocity = Vector2.Normalize(direction) * native.Speed;
            input = native.HasInput; target = native.TargetSpeed;
            grounded = !native.Jumping && Math.Abs(native.VerticalSpeed) < .01f;
        }
        else grounded = false;
        var allowed = DirectMovementController.ObservedInputAllowed;
        NativeDisplacementObservation? displacement;
        unsafe
        {
            displacement = Service.LocalPlayer is { } player
                ? NativeDisplacementReader.Capture((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address, now) : null;
        }
        var observation = _motion.Observe(new(position.X, position.Z), velocity, input, target, now,
            grounded && allowed == true && displacement == null, pendingKnockback, movementLocked || allowed == false);
        return observation with { Displacement = displacement, ForcedMovement = displacement != null };
    }
    public void CorrectPosition(Vector2 position) => _motion.CorrectPosition(position);
    public void Reset() => _motion.Reset();
}

public static class NativeNavigationOutput
{
    public static unsafe void Apply(DirectMovementController controller, NavigationCommand command, Vector3 position)
    {
        if (command.Correction is { } correction && Service.LocalPlayer is { } player)
        {
            // The follower owns one correction at a walking endpoint. Preserve observed height.
            ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address)
                ->SetPosition(correction.X, position.Y, correction.Y);
        }
        if (command.Kind != NavigationCommandKind.Move) { controller.Release(); return; }
        controller.Enabled = true;
        controller.DesiredPosition = new(command.Target.X, position.Y, command.Target.Y);
        controller.InputScale = 1;
        controller.ClampFinalStep = false;
    }
}
