using global::Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private string? _farmingError;
    private static readonly string[] FarmingModeLabels =
        ["挖寶藏", "強化／陶片", "寶藏發現成就", "死靈術士(beta)"];
    private static readonly string[] FarmingModeWithSurveyLabels =
        ["挖寶藏", "強化／陶片", "寶藏發現成就", "死靈術士(beta)", "受控採集（研究）"];
    private static readonly string[] HoardFloorsetLabels = ["21–30 層", "31–40 層"];

    private bool IsFixedFloorsetHoard => _fsfScenarioIndex != 2 &&
        _configuration.Farming.Mode == FarmingMode.Hoard &&
        _configuration.Farming.GetPreferences(_configuration).BandedEnabled == true;

    private string? GetSelectedDetailedMapScenarioKey() => IsFixedFloorsetHoard || _fsfScenarioIndex == 2
        ? DetailedMapCatalogManager.GetScenarioKey(_fsfScenarioIndex) : null;

    private void DrawFarmingSelection(bool running)
    {
        ImGui.TextDisabled("地宮：朝聖交錯路");
        ImGui.BeginDisabled(running);
        var settings = _configuration.Farming;
        int mode = _fsfScenarioIndex == 2 ? 4 : (int)settings.Mode;
        var labels = _detailedMapHostOptions.SupportsControlledPtSurvey ? FarmingModeWithSurveyLabels : FarmingModeLabels;
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("模式##fsdMode", ref mode, labels, labels.Length))
        {
            if (mode == 4) _fsfScenarioIndex = 2;
            else
            {
                settings.Mode = (FarmingMode)mode;
                if (_fsfScenarioIndex == 2) _fsfScenarioIndex = 1;
            }
            _configuration.NecromancerFsdScenarioIndex = _fsfScenarioIndex;
            _configuration.Save();
            _farmingError = null;
        }
        if (_fsfScenarioIndex != 2)
        {
            if (settings.Mode == FarmingMode.Hoard)
            {
                int floor = _fsfScenarioIndex;
                ImGui.SetNextItemWidth(180);
                if (ImGui.Combo("起始層段##fsdFloorset", ref floor, HoardFloorsetLabels, 2))
                {
                    _fsfScenarioIndex = floor;
                    _configuration.NecromancerFsdScenarioIndex = floor;
                    _configuration.Save();
                }
            }
            else if (settings.Mode == FarmingMode.Aetherpool)
                ImGui.TextWrapped("準備1份較多跳層松杜香的任意起點層存檔");
            else if (settings.Mode == FarmingMode.HoardDiscovery)
                ImGui.TextWrapped("準備1份有至少1個感知寶藏的任意起點層存檔");
        }
        ImGui.EndDisabled();
    }

    private void DrawFarmingOptions()
    {
        ImGui.Separator();
        ImGui.Text("FSD 選項");
        ImGui.Indent();
        bool running = _ddHost?.FsdActive == true;
        bool research = _fsfScenarioIndex == 2;
        var mode = _configuration.Farming.Mode;
        var preferences = research ? null : _configuration.Farming.GetPreferences(_configuration);
        var options = running ? _ddHost?.RunOptionsProvider?.Current : null;
        bool hoard = options?.BandedEnabled ?? preferences?.BandedEnabled == true;
        bool gold = options?.OpenGold ?? preferences?.OpenGold ?? false;
        bool silver = options?.OpenSilver ?? preferences?.OpenSilver ?? false;
        bool bronze = options?.OpenBronze ?? preferences?.OpenBronze ?? false;
        bool changed;
        ImGui.BeginDisabled(research || FarmingTargetPolicy.LocksHoard(mode));
        changed = ImGui.Checkbox("探索埋藏的寶藏", ref hoard);
        ImGui.EndDisabled();
        ImGui.BeginDisabled(research || FarmingTargetPolicy.LocksGold(mode));
        changed |= ImGui.Checkbox("開啟金寶箱", ref gold);
        ImGui.EndDisabled();
        ImGui.BeginDisabled(research || FarmingTargetPolicy.LocksSilverBronze(mode) ||
            mode == FarmingMode.Aetherpool && silver && !bronze);
        changed |= ImGui.Checkbox("開啟銀寶箱", ref silver);
        ImGui.EndDisabled();
        ImGui.BeginDisabled(research || FarmingTargetPolicy.LocksSilverBronze(mode) ||
            mode == FarmingMode.Aetherpool && bronze && !silver);
        changed |= ImGui.Checkbox("開啟銅寶箱", ref bronze);
        ImGui.EndDisabled();
        if (changed && preferences != null)
        {
            preferences.BandedEnabled = hoard;
            preferences.OpenGold = gold; preferences.OpenSilver = silver; preferences.OpenBronze = bronze;
            _ddHost?.SetFarmingTargets(hoard, gold, silver, bronze);
            _configuration.Save();
            _farmingError = null;
        }
        ImGui.BeginDisabled(research || mode == FarmingMode.HoardDiscovery);
        bool aggressiveChestInteraction = _configuration.AggressiveChestInteraction;
        if (ImGui.Checkbox("我開箱會卡住很久##aggressiveChestInteraction", ref aggressiveChestInteraction))
        {
            _configuration.AggressiveChestInteraction = aggressiveChestInteraction;
            _configuration.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32f);
            ImGui.TextUnformatted("啟用該項目會增加開箱的力度, 如幀數較低或戰鬥時很難成功開箱, 請啟用以增加對輸出或其他外掛搶佔成功率. 若沒有遇到這個問題, 請不要啟用");
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        ImGui.EndDisabled();
        ImGui.Spacing();
        DrawCycleOptions(preferences);
        if (running)
        {
            ImGui.TextDisabled($"完成循環：{_ddHost!.CompletedLoops}/{(_ddHost.Infinite ? "∞" : _ddHost.TargetLoops)}");
            if (mode == FarmingMode.DeepProgression && !research)
                ImGui.TextDisabled($"失敗重試：{_ddHost.FarmingFailures}");
            if (mode == FarmingMode.HoardDiscovery && !research)
                ImGui.TextDisabled($"寶藏發現：{_ddHost.HoardDiscoveries}");
            if (_ddHost.Context?.FarmingPlan?.SaveUse == SaveUse.Prepared)
                ImGui.TextDisabled(_ddHost.PreparedSaveDescription);
        }
        ImGui.Unindent();
        string? error = _farmingError ?? (_ddHost is { FsdActive: false, LastStatusIsError: true }
            ? _ddHost.LastStatus : null);
        if (!string.IsNullOrEmpty(error))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.55f, 0.55f, 1f));
            ImGui.TextWrapped(error);
            ImGui.PopStyleColor();
        }
    }

    private void DrawCycleOptions(FarmingPreferences? preferences)
    {
        ImGui.Text("結束 FSD...");
        bool infinite = _ddHost?.FsdActive == true ? _ddHost.Infinite : preferences?.Infinite ?? _configuration.NecromancerFsdLoopInfinite;
        int cycles = _ddHost?.FsdActive == true ? _ddHost.TargetLoops : preferences?.Cycles ?? _configuration.NecromancerFsdLoopCount;
        bool changed = ImGui.Checkbox("無限循環##fsf_inf", ref infinite);
        ImGui.SameLine();
        ImGui.BeginDisabled(infinite);
        ImGui.SetNextItemWidth(90);
        changed |= ImGui.InputInt("循環次數##fsf_loops", ref cycles);
        ImGui.EndDisabled();
        if (changed)
        {
            cycles = Math.Max(1, cycles);
            if (preferences != null) { preferences.Cycles = cycles; preferences.Infinite = infinite; }
            else { _configuration.NecromancerFsdLoopCount = cycles; _configuration.NecromancerFsdLoopInfinite = infinite; }
            _configuration.Save();
            _ddHost?.SetCycleTargets(cycles, infinite);
        }
    }

    public bool StartSelectedFarming(out string error, int? stopAfterFloor = null)
    {
        if (stopAfterFloor.HasValue && (_configuration.Farming.Mode != FarmingMode.DeepProgression ||
            stopAfterFloor.Value is < 10 or > 100 || stopAfterFloor.Value % 10 != 0 || _fsfScenarioIndex == 2))
        {
            error = "停止層數只支援死靈術士模式，且必須為 10 至 100 的十層邊界。";
            return false;
        }
        if (_fsfScenarioIndex == 2)
        {
            var survey = new ControlledPtSurveySession();
            return TryStartOutsideDutyFsd(() => new ControlledPt21To30Scenario(survey),
                Math.Max(1, _configuration.NecromancerFsdLoopCount), _configuration.NecromancerFsdLoopInfinite,
                DetailedMapEvidenceContract.PilgrimsTraverse21To30ScenarioKey, out error);
        }
        var settings = _configuration.Farming;
        var p = settings.GetPreferences(_configuration);
        bool prepared = settings.Mode is FarmingMode.Aetherpool or FarmingMode.HoardDiscovery;
        return TryStartFarming(settings.Mode, prepared ? SaveUse.Prepared : SaveUse.Create,
            settings.Mode == FarmingMode.DeepProgression ? 1 : _fsfScenarioIndex == 0 ? 21 : 31,
            p.Cycles, p.Infinite, p.BandedEnabled == true, p.OpenGold, p.OpenSilver, p.OpenBronze, out error, stopAfterFloor);
    }

    public bool TryStartFarming(FarmingMode mode, SaveUse saveUse, int startFloor, int cycles, bool infinite,
        bool hoard, bool gold, bool silver, bool bronze, out string error, int? stopAfterFloor = null)
    {
        if (stopAfterFloor.HasValue && (mode != FarmingMode.DeepProgression ||
            stopAfterFloor.Value is < 10 or > 100 || stopAfterFloor.Value % 10 != 0))
        {
            error = "停止層數只支援死靈術士模式，且必須為 10 至 100 的十層邊界。";
            return false;
        }
        if (!FarmingPlan.TryCreate(mode, saveUse, startFloor, cycles, infinite, hoard, gold, silver, bronze,
                out var plan, out error)) return false;
        plan = plan! with { StopAfterFloor = stopAfterFloor };
        var session = new FarmingSession(plan);
        string? key = plan!.ShowsDetailedMap ? startFloor == 21
            ? DetailedMapScenarioCatalog.PilgrimsTraverse21To30.Key
            : DetailedMapScenarioCatalog.PilgrimsTraverse31To40.Key : null;
        bool started = TryStartOutsideDutyFsd(() => new FarmingScenario(session), cycles, infinite, key, out error);
        if (started)
        {
            _farmingError = null;
            _configuration.Farming.Mode = mode;
            _fsfScenarioIndex = mode == FarmingMode.Hoard && startFloor == 21 ? 0 : 1;
            _configuration.NecromancerFsdScenarioIndex = _fsfScenarioIndex;
            var p = _configuration.Farming.GetPreferences(_configuration);
            p.BandedEnabled = hoard; p.OpenGold = gold; p.OpenSilver = silver; p.OpenBronze = bronze;
            p.Cycles = cycles; p.Infinite = infinite;
            _configuration.Save();
        }
        return started;
    }
}
