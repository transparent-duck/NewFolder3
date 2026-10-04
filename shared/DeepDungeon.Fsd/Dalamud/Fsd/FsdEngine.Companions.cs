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
    private bool _wrathInstalled, _rsrInstalled, _promeInstalled;
    private static readonly string[] RotationProviderLabels = ["Rotation Solver Reborn", "Wrath Combo", "Prome Rotation", "自訂"];
    private static readonly string[] BossMechanicsProviderLabels = ["Bossmod Reborn", "自訂"];
    private readonly string[] _recoveryPotionNames = new string[DungeonCatalog.All.Length];
    private readonly int?[] _recoveryPotionCounts = new int?[DungeonCatalog.All.Length];

    public void DrawCompanionSettings()
    {
        if (DateTime.UtcNow >= _nextCompanionCheck)
        {
            _nextCompanionCheck = DateTime.UtcNow.AddSeconds(1);
            _vnavInstalled = _palacePalInstalled = _ichingInstalled = _bmrInstalled = false;
            _wrathInstalled = _rsrInstalled = _promeInstalled = false;
            foreach (var plugin in Service.PluginInterface.InstalledPlugins)
            {
                _vnavInstalled |= Matches(plugin.InternalName, "vnavmesh");
                _palacePalInstalled |= Matches(plugin.InternalName, "PalacePal");
                _ichingInstalled |= Matches(plugin.InternalName, "I-Ching") || Matches(plugin.InternalName, "IChing") || Matches(plugin.InternalName, "I-Ching-GL") || Matches(plugin.Name, "I-Ching");
                _bmrInstalled |= Matches(plugin.InternalName, "BossModReborn");
                _wrathInstalled |= Matches(plugin.InternalName, "WrathCombo");
                _rsrInstalled |= Matches(plugin.InternalName, "RotationSolver") || Matches(plugin.InternalName, "RotationSolverReborn");
                _promeInstalled |= Matches(plugin.InternalName, "PromeRotation");
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
            DrawCompanionRow("自動輸出", RotationProviderLabels[(int)_configuration.Rotation.Provider - 1], _configuration.Rotation.Provider switch
            {
                FsdRotationProvider.RotationSolverReborn => _rsrInstalled,
                FsdRotationProvider.WrathCombo => _wrathInstalled,
                FsdRotationProvider.PromeRotation => _promeInstalled,
                _ => (bool?)null
            });
            DrawCompanionRow("I-Ching", "", _ichingInstalled);
            DrawCompanionRow("機制走位", bmr ? "Bossmod Reborn" : "自訂", bmr ? _bmrInstalled : null);
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("機制與走位");
        int mechanicsProvider = (int)settings.Provider;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 18);
        if (ImGui.Combo("##FsdBossProvider", ref mechanicsProvider, BossMechanicsProviderLabels, BossMechanicsProviderLabels.Length))
        {
            settings.Provider = (FsdBossMechanicsProvider)mechanicsProvider;
            _configuration.Save();
        }
        bmr = settings.Provider == FsdBossMechanicsProvider.Bmr;
        if (!bmr)
        {
            string on = settings.CustomEnableCommand ?? string.Empty;
            string off = settings.CustomDisableCommand ?? string.Empty;
            if (DrawCustomCompanionCommands("FsdBoss", ref on, ref off))
            {
                settings.CustomEnableCommand = on;
                settings.CustomDisableCommand = off;
                _configuration.Save();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawRotationSettings();
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

    private void DrawRotationSettings()
    {
        var settings = _configuration.Rotation;
        ImGui.Text("自動輸出");
        int provider = (int)settings.Provider - 1;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 18);
        if (ImGui.Combo("##FsdRotationProvider", ref provider, RotationProviderLabels, RotationProviderLabels.Length))
        {
            settings.Provider = (FsdRotationProvider)(provider + 1);
            _configuration.Save();
        }
        if (settings.Provider == FsdRotationProvider.Custom)
        {
            string on = settings.CustomEnableCommand ?? string.Empty;
            string off = settings.CustomDisableCommand ?? string.Empty;
            if (DrawCustomCompanionCommands("FsdRotation", ref on, ref off))
            {
                settings.CustomEnableCommand = on;
                settings.CustomDisableCommand = off;
                _configuration.Save();
            }
        }
        if (_rotationControl.FailedProvider is { } failed)
            ImGui.TextColored(new Vector4(0.95f, 0.45f, 0.45f, 1),
                $"無法{(_rotationControl.DesiredEnabled ? "啟用" : "關閉")}輸出：{RotationProviderLabels[(int)failed - 1]}");
    }

    private static bool DrawCustomCompanionCommands(string id, ref string on, ref string off)
    {
        bool columns = ImGui.GetContentRegionAvail().X >= ImGui.GetFontSize() * 32 &&
            ImGui.BeginTable($"##{id}Commands", 2, ImGuiTableFlags.SizingStretchSame);
        if (columns)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
        }
        ImGui.Text("啟用指令");
        ImGui.SetNextItemWidth(-1);
        bool changed = ImGui.InputText($"##{id}Enable", ref on, 500);
        if (columns) ImGui.TableSetColumnIndex(1);
        ImGui.Text("關閉指令");
        ImGui.SetNextItemWidth(-1);
        changed |= ImGui.InputText($"##{id}Disable", ref off, 500);
        if (columns) ImGui.EndTable();
        return changed;
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
