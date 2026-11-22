using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using global::Dalamud.Bindings.ImGui;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private static readonly int[] PilgrimsDebugStartFloors = [1, 21, 31, 51, 71];
    private string? _debugStartNotice;

    private void DrawPilgrimsDebugStarts()
    {
        ImGui.Text("���m���c�yԇ�����`�gʿģʽ��");
        ImGui.TextWrapped("��ָ�����½���n����100�����M���δΈ��У���ѭ�h��Ո����� NPC ����ʹ�ã����пմ�n��λ��");
        ImGui.BeginDisabled(_ddHost?.FsdActive == true || DeepDungeonHelper.IsInDeepDungeon());
        foreach (var floor in PilgrimsDebugStartFloors)
        {
            if (floor != 1) ImGui.SameLine();
            if (ImGui.Button($"��{floor}���_ʼ##pt_debug_start_{floor}"))
            {
                // Keep normal challenge runs starting at one; these buttons use
                // the existing run-only diagnostic checkpoint route.
                var request = new FsdFarmingRequest(FarmingMode.DeepProgression, SaveUse.Create,
                    1, 1, false, new FarmingTargets(false, true, true, false),
                    new FsdStartRequest(StopAfterFloor: 100, DiagnosticStartFloor: floor,
                        Scenario: FsdStartScenario.Farming));
                _debugStartNotice = TryStartFarming(request, out var error)
                    ? $"�ц������`�gʿģʽ����{floor}���_ʼ��Ŀ��100�ӡ�"
                    : $"����ʧ����{error}";
            }
        }
        ImGui.EndDisabled();
        if (_debugStartNotice != null) ImGui.TextWrapped(_debugStartNotice);
        ImGui.Separator();
    }
}
