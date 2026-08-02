using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Dalamud;

namespace NewFolder3;

internal sealed class FsdWindow : Window
{
    private readonly FsdApplication _application;
    private readonly INewFolder3AccessGate _accessGate;

    public FsdWindow(
        FsdApplication application,
        INewFolder3AccessGate accessGate)
        : base($"{ProductIdentity.DisplayName}")
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _accessGate = accessGate ?? throw new ArgumentNullException(nameof(accessGate));
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##FsdHostTabs"))
            return;

        var canShowFsdPage = NewFolder3FsdPageAccess.CanShowFsdPage(_accessGate.Current);

        if (ImGui.BeginTabItem("說明"))
        {
            ImGui.TextWrapped("1. 它也許可以幫助你挖到一些寶藏");
            ImGui.Spacing();
            ImGui.TextWrapped("2. 你需要一些其他外掛來讓它運作: vnav, bossmod, 自動輸出外掛, 以及讓你活下來的 I-Ching.");
            ImGui.Spacing();
            ImGui.TextWrapped("3. 記得帶藥, 頭目有普攻");
            ImGui.Spacing();
            ImGui.TextWrapped("4. 推獎設定是: 啟用寶藏+金箱. 啟用自動選中. 如果輸出外掛會主動開怪, 選擇召喚/黑魔, 否則啟用主動開怪並選擇機工.");
            if (!canShowFsdPage && !string.IsNullOrEmpty(_accessGate.DenialInstruction))
            {
                ImGui.Spacing();
                ImGui.TextWrapped(_accessGate.DenialInstruction);
            }

            ImGui.EndTabItem();
        }

        if (canShowFsdPage && ImGui.BeginTabItem("FSD"))
        {
            _application.DrawDeepDungeonFormalPanel();
            ImGui.Separator();
            ImGui.Text("General assistant");
            _application.DrawGeneralAssistantSettings();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

}
