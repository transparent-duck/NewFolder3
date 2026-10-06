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
            _application.DrawCompanionSettings();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextWrapped("它也許可以幫助你挖到一些寶藏");
            ImGui.TextWrapped("推薦設定: 啟用寶藏+金銀, 如果陶片不足啟用銅.\n啟用 NewFolder3 自動選中 + 你所用輸出外掛的主動攻擊\n死靈術士模式需要較高輸出的職業, 如黑魔.");
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
