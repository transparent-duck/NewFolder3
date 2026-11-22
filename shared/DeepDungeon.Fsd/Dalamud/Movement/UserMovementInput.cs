using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DeepDungeon.Fsd.Dalamud.Movement;

internal static unsafe class UserMovementInput
{
    // Same UIInputData inputs and stick deadzone as the source FSD host.
    internal static bool IsActive
    {
        get
        {
            var input = UIInputData.Instance();
            if (input == null) return false;
            return input->IsInputIdDown(InputId.MOVE_FORE)
                || input->IsInputIdDown(InputId.MOVE_BACK)
                || input->IsInputIdDown(InputId.MOVE_LEFT)
                || input->IsInputIdDown(InputId.MOVE_RIGHT)
                || input->IsInputIdDown(InputId.MOVE_STRIFE_L)
                || input->IsInputIdDown(InputId.MOVE_STRIFE_R)
                || Math.Abs(input->GamepadInputs.LeftStickX) > 10
                || Math.Abs(input->GamepadInputs.LeftStickY) > 10;
        }
    }
}
