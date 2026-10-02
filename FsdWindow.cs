using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Dalamud;

namespace NewFolder3;

internal sealed class FsdWindow : Window
{
    private readonly FsdApplication _application;

    public FsdWindow(FsdApplication application)
        : base("你是壞孩子###NewFolder3")
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##FsdHostTabs"))
            return;

        if (ImGui.BeginTabItem("說明"))
        {
            ImGui.TextWrapped("1. 它也許可以幫助你挖到一些寶藏");
            ImGui.Spacing();
            _application.DrawCompanionSettings();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextWrapped("4. 推薦設定是: 啟用寶藏+金箱. 啟用自動選中. 如果輸出外掛會主動開怪, 選擇召喚/黑魔, 否則啟用主動開怪並選擇機工.");
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("FSD"))
        {
            _application.DrawDeepDungeonFormalPanel();
            ImGui.Separator();
            ImGui.Text("通用輔助");
            _application.DrawGeneralAssistantSettings();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

}
