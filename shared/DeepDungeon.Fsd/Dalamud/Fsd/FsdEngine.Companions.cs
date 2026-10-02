using global::Dalamud.Bindings.ImGui;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Items;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private DateTime _nextCompanionCheck;
    private bool _vnavInstalled;
    private bool _palacePalInstalled;
    private bool _ichingInstalled;
    private bool _bmrInstalled;
    private readonly string[] _recoveryPotionNames = new string[DungeonCatalog.All.Length];
    private readonly int?[] _recoveryPotionCounts = new int?[DungeonCatalog.All.Length];

    public void DrawCompanionSettings()
    {
        if (DateTime.UtcNow >= _nextCompanionCheck)
        {
            _nextCompanionCheck = DateTime.UtcNow.AddSeconds(1);
            _vnavInstalled = _palacePalInstalled = _ichingInstalled = _bmrInstalled = false;
            foreach (var plugin in Service.PluginInterface.InstalledPlugins)
            {
                _vnavInstalled |= Matches(plugin.InternalName, "vnavmesh");
                _palacePalInstalled |= Matches(plugin.InternalName, "PalacePal");
                _ichingInstalled |= Matches(plugin.InternalName, "I-Ching") || Matches(plugin.InternalName, "IChing") || Matches(plugin.Name, "I-Ching");
                _bmrInstalled |= Matches(plugin.InternalName, "BossModReborn");
            }
            for (int i = 0; i < DungeonCatalog.All.Length; i++)
            {
                var dungeon = DungeonCatalog.All[i];
                var item = ItemManager.GetOrRegister(dungeon.RecoveryPotionItemId);
                _recoveryPotionNames[i] = item.IsValid ? item.Name : dungeon.RecoveryPotionName;
                _recoveryPotionCounts[i] = Service.LocalPlayer != null &&
                    DeepDungeonLootTracker.TryGetItemCount(dungeon.RecoveryPotionItemId, out int count, out _)
                    ? count : null;
            }
        }
        var settings = _configuration.BossMechanics;
        bool bmr = settings.Provider == FsdBossMechanicsProvider.Bmr;
        ImGui.Text("探索配套");
        if (ImGui.BeginTable("##FsdCompanions", 3, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("##Name", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableSetupColumn("##Provider", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("##Status", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("未安裝").X);
            DrawCompanionRow("vnavmesh", "", _vnavInstalled);
            DrawCompanionRow("PalacePal", "", _palacePalInstalled);
            DrawCompanionRow("自動輸出", "", null);
            DrawCompanionRow("I-Ching", "", _ichingInstalled);
            DrawCompanionRow("頭目機制", bmr ? "Bossmod Reborn" : "自訂", bmr ? _bmrInstalled : null);
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("頭目機制與走位");
        ImGui.SameLine();
        if (ImGui.RadioButton("BMR##FsdBossProvider", bmr))
        {
            settings.Provider = FsdBossMechanicsProvider.Bmr;
            _configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("自訂##FsdBossProvider", !bmr))
        {
            settings.Provider = FsdBossMechanicsProvider.Custom;
            _configuration.Save();
        }
        bmr = settings.Provider == FsdBossMechanicsProvider.Bmr;
        string on = settings.GetEnableCommand() ?? string.Empty;
        string off = settings.GetDisableCommand() ?? string.Empty;
        ImGui.Spacing();
        bool columns = ImGui.GetContentRegionAvail().X >= ImGui.GetFontSize() * 32 &&
            ImGui.BeginTable("##FsdCompanionCommands", 2, ImGuiTableFlags.SizingStretchSame);
        if (columns)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
        }
        ImGui.Text("進入頭目層 · 啟用");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##FsdBossEnable", ref on, 500, bmr ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None))
        {
            settings.CustomEnableCommand = on;
            _configuration.Save();
        }
        if (columns)
            ImGui.TableSetColumnIndex(1);
        ImGui.Text("進入小怪層 · 關閉");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##FsdBossDisable", ref off, 500, bmr ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None))
        {
            settings.CustomDisableCommand = off;
            _configuration.Save();
        }
        if (columns)
            ImGui.EndTable();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("恢復藥");
        if (ImGui.BeginTable("##FsdRecoveryPotions", 2, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("##Potion", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##Count", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("9999").X);
            for (int i = 0; i < _recoveryPotionCounts.Length; i++)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(_recoveryPotionNames[i]);
                ImGui.TableSetColumnIndex(1);
                if (_recoveryPotionCounts[i] is int count)
                {
                    if (count < 20)
                        ImGui.TextColored(new Vector4(0.95f, 0.45f, 0.45f, 1), count.ToString());
                    else
                        ImGui.TextUnformatted(count.ToString());
                }
                else
                    ImGui.TextUnformatted("-");
            }
            ImGui.EndTable();
        }
    }

    private static bool Matches(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static void DrawCompanionRow(string name, string provider, bool? installed)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(name);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(provider);
        ImGui.TableSetColumnIndex(2);
        if (installed.HasValue)
            ImGui.TextColored(installed.Value ? new Vector4(0.45f, 0.85f, 0.6f, 1) : new Vector4(0.95f, 0.45f, 0.45f, 1), installed.Value ? "已安裝" : "未安裝");
        else
            ImGui.TextUnformatted("-");
    }
}
