using global::Dalamud.Interface.Utility;
using global::Dalamud.Interface.Textures;
using global::Dalamud.Interface.Textures.TextureWraps;
using global::Dalamud.Plugin.Services;
using System;
using System.Linq;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.Debug.BgCollision;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Collections.Generic;
using global::Dalamud.Game.ClientState.Objects.Types;
using global::Dalamud.Game.ClientState.Objects.Enums;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using global::Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Dalamud;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Items;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud
{
    internal partial class FsdEngine
    {
        public unsafe void DrawDeepDungeonDebugPanel()
        {
            _showBgCollisionOverlay = false;
            try
            {
                // Always draw the header so user sees it in module top
                if (!ImGui.CollapsingHeader("調試面板", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    return;
                }

                ImGui.Indent();

                DrawPilgrimsDebugStarts();

                // Quick context state
                if (!DeepDungeonHelper.IsInDeepDungeon())
                {
                    ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), "當前不在深宮/獨立副本");
                    ImGui.Unindent();
                    ImGui.Separator();
                    return;
                }

                var eventFramework = EventFramework.Instance();
                if (eventFramework == null)
                {
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "EventFramework 為空");
                    ImGui.Unindent();
                    ImGui.Separator();
                    return;
                }

                var deepDungeon = eventFramework->GetInstanceContentDeepDungeon();
                if (deepDungeon == null)
                {
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), "未檢測到深宮導演 (DeepDungeon director)");
                    ImGui.Unindent();
                    ImGui.Separator();
                    return;
                }

                // Top-line summary
                ImGui.Text($"層數: {deepDungeon->Floor}    回歸: {deepDungeon->ReturnProgress}    通路: {deepDungeon->PassageProgress}");
                ImGui.Text($"武器: {deepDungeon->WeaponLevel}    防具: {deepDungeon->ArmorLevel}    同步裝等: {deepDungeon->SyncedGearLevel}");
                ImGui.Text($"藏寶: {deepDungeon->HoardCount}    機關(當前/下一層): {deepDungeon->DeepDungeonGimmickEffectIdCurrent}/{deepDungeon->DeepDungeonGimmickEffectIdNext}");
                ImGui.Text($"狀態: {deepDungeon->DeepDungeonStatusId}    禁止: {deepDungeon->DeepDungeonBanId}    危險: {deepDungeon->DeepDungeonDangerId}    地城ID: {deepDungeon->DeepDungeonId}");
                ImGui.Text($"版面: {deepDungeon->ActiveLayoutIndex}    佈局初始化: {deepDungeon->LayoutInitializationType}    獎勵物品: {deepDungeon->BonusLootItemId}");

                _bgCollisionDebug.DrawConfigUi();
				_showBgCollisionOverlay = _configuration.NecromancerShowBgCollisionOverlay;
                ImGui.Separator();

				// Chest farm statuses moved to dedicated config panel

                // Timer info (best-effort: may not always be available)
                try
                {
                    bool hasTimer = deepDungeon->HasTimer();
                    if (hasTimer)
                    {
                        var nowTs = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        int remaining = deepDungeon->GetTimeRemaining(nowTs);
                        uint timeMax = deepDungeon->GetContentTimeMax();
                        ImGui.Text($"計時: {remaining}s / 上限 {timeMax}s");
                    }
                }
                catch
                {
                    // Ignore timer read errors
                }

                // Party room info
                if (ImGui.CollapsingHeader("隊伍/房間", ImGuiTreeNodeFlags.None))
                {
                    var partySpan = deepDungeon->Party;
                    for (int i = 0; i < partySpan.Length; i++)
                    {
                        var p = partySpan[i];
                        ImGui.Text($"[{i}] EntityId=0x{p.EntityId:X8} 房間={p.RoomIndex}");
                    }
                }

                // Items / Magicite
                if (ImGui.CollapsingHeader("道具 (風水/魔土) 與 魔石/仿生體", ImGuiTreeNodeFlags.None))
                {
                    var items = deepDungeon->Items;
                    // Resolve human-readable names using DeepDungeon sheet mapping: PomanderSlot -> Item row id
                    Lumina.Excel.Sheets.DeepDungeon? ddRow = null;
                    try
                    {
                        ddRow = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.DeepDungeon>()?.GetRow(deepDungeon->DeepDungeonId);
                    }
                    catch { }
                    for (int i = 0; i < items.Length; i++)
                    {
                        var it = items[i];
                        string displayName = $"魔陶器槽位 {i}";
                        if (ddRow != null && i < ddRow.Value.PomanderSlot.Count)
                        {
                            // Try resolve localized name via Item row, if PomanderSlot is an Item row reference in Lumina build
                            try
                            {
                                var itemRow = ddRow.Value.PomanderSlot[i].ValueNullable;
                                if (itemRow != null)
                                {
                                    displayName = itemRow.Value.Name.ToString();
                                }
                                else
                                {
                                    uint pomanderId = (uint)ddRow.Value.PomanderSlot[i].RowId;
                                    displayName = GetPomanderDisplayName(pomanderId);
                                }
                            }
                            catch
                            {
                                uint pomanderId = (uint)ddRow.Value.PomanderSlot[i].RowId;
                                displayName = GetPomanderDisplayName(pomanderId);
                            }
                        }
                        ImGui.Text($"[{i}] {displayName}  數量={it.Count}  標誌=0x{it.Flags:X2}  可用={it.IsUsable}  啟用={it.IsActive}");
                        ImGui.SameLine();
                        ImGui.BeginDisabled(!(it.IsUsable && it.Count > 0));
                        if (ImGui.Button($"使用##pom{i}"))
                        {
                            Actions.FsdItemExecutor.UsePomander(deepDungeon, (uint)i);
                        }
                        ImGui.EndDisabled();
                    }

                    var magicite = deepDungeon->Magicite;
                    for (int i = 0; i < magicite.Length; i++)
                    {
                        ImGui.Text($"魔石/仿生體 槽 {i}: {magicite[i]}");
                        ImGui.SameLine();
                        ImGui.BeginDisabled(magicite[i] == 0);
                        if (ImGui.Button($"使用##stone{i}"))
                        {
                            Actions.FsdItemExecutor.UseStone(deepDungeon, (uint)i);
                        }
                        ImGui.EndDisabled();
                    }
                }

                // Chests
                if (ImGui.CollapsingHeader("寶箱", ImGuiTreeNodeFlags.None))
                {
                    var chests = deepDungeon->Chests;
                    for (int i = 0; i < chests.Length; i++)
                    {
                        var c = chests[i];
                        ImGui.Text($"[{i}] 類型={c.ChestType} 房間={c.RoomIndex}");
                    }
                }

                // Map room flags
                if (ImGui.CollapsingHeader("地圖", ImGuiTreeNodeFlags.None))
                {
                    DrawDeepDungeonMiniMap(deepDungeon);
                    
                    // ===== AutoPilot 調試 (sequential room path) =====
                    try
                    {
                        var dbg = _ddHost?.FloorController.GetDebugSnapshot();
                        if (dbg != null)
                        {
                            if (ImGui.CollapsingHeader("樓層規劃調試", ImGuiTreeNodeFlags.None))
                            {
                                var player = Service.LocalPlayer;
                                var pos = player?.Position ?? default;
                                
                                // State and status
                                ImGui.Text($"階段: {dbg.Phase} | 任務: {dbg.TaskPhase}");
                                ImGui.Text($"狀態: {dbg.Status}");
                                ImGui.Text($"埋藏的寶藏數量: {dbg.HoardCount}/5");
                                ImGui.Separator();
                                
                                // Room path visualization
                                if (dbg.RoomPath != null && dbg.RoomPath.Count > 0)
                                {
                                    ImGui.TextDisabled("房間訪問順序:");
                                ImGui.Indent();
                                    for (int i = 0; i < dbg.RoomPath.Count; i++)
                                    {
                                        int room = dbg.RoomPath[i];
                                        bool isCompleted = dbg.CompletedRooms.Contains(room);
                                        bool isCurrent = i == dbg.CurrentRoomIdx;
                                        
                                        if (isCompleted)
                                            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), $"✓ 房間 {room}");
                                        else if (isCurrent)
                                            ImGui.TextColored(new Vector4(0.6f, 1.0f, 0.6f, 1f), $"→ 房間 {room}（當前）");
                                        else
                                            ImGui.Text($"  房間 {room}");
                                    }
                                    ImGui.Unindent();
                                }
                                else
                                {
                                    ImGui.TextDisabled("（尚無房間路徑）");
                                }
                                
                                ImGui.Separator();
                                int completedCount = dbg.CompletedRooms != null ? dbg.CompletedRooms.Count : 0;
                                int totalRooms = dbg.RoomPath != null ? dbg.RoomPath.Count : 0;
                                ImGui.Text($"進度: 已完成 {completedCount}/{totalRooms} 個房間");
                            }
                        }
                    }
                    catch { }
                }

				DrawDeepDungeonOverlayToggles();

                ImGui.Unindent();
                ImGui.Separator();
            }
            catch (Exception ex)
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"DeepDungeon Debug 錯誤: {ex.Message}");
            }
        }

        private static readonly string[] FloorMapSources = ["即時構建", "內建地圖"];

		// Formal panel (top): map with Banded controls/status
		public unsafe void DrawDeepDungeonFormalPanel()
		{
			try
			{
				bool inDD = DeepDungeonHelper.IsInDeepDungeon();
				var eventFramework = EventFramework.Instance();
				var deepDungeon = (InstanceContentDeepDungeon*)null;
				if (inDD && eventFramework != null)
				{
					deepDungeon = eventFramework->GetInstanceContentDeepDungeon();
				}

				if (!inDD)
				{
					ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), "當前不在深宮/獨立副本");
				}
				else if (eventFramework == null)
				{
					ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "EventFramework 為空");
				}
				else if (deepDungeon == null)
				{
					ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), "未檢測到深宮導演 (DeepDungeon director)");
				}

				// Draw only the minimap, without debug actions/warnings, if available
                if (deepDungeon != null)
                {
                    DrawDeepDungeonMiniMap(deepDungeon, false);
                    DrawSelectedRoomPresentation(deepDungeon);
                }

				// ===== Full Self Farming (scenario selection + control) =====
				ImGui.Separator();
				ImGui.Text("Necromancer FSD");
				UiHelpers.DrawGrayTipText("Full Self-Delving (Supervised) in Deep Dungeons");
				ImGui.Indent();
                bool hostAssistActive = _ddHost?.AssistModeActive == true;
                DrawFarmingSelection(hostAssistActive || ProgressionPauseLocked);
                var liveMaps = Map.FloorMapSource.Live;
                if (liveMaps != null)
                {
                    int mapSource = _configuration.UseBuiltInFloorMaps ? 1 : 0;
                    ImGui.BeginDisabled(hostAssistActive || ProgressionPauseLocked);
                    ImGui.SetNextItemWidth(260);
                    if (ImGui.Combo("底圖來源##fsdFloorMapSource", ref mapSource, FloorMapSources, FloorMapSources.Length))
                    {
                        _configuration.UseBuiltInFloorMaps = mapSource == 1;
                        _configuration.Save();
                        Map.FloorMapSource.Select(_configuration.UseBuiltInFloorMaps);
                    }
                    ImGui.EndDisabled();
                }

				DrawDetailedMapSettings(hostAssistActive);

				// ===== Controls (moved under scenario selection) =====
				{
					bool fsdRunning = _ddHost?.FsdActive == true;

					ImGui.Spacing();
					ImGui.Indent();
					ImGui.BeginDisabled(fsdRunning || inDD || _bridgeLeaveDutyContext != null || _bridgeDeleteSaveContext != null || _bridgeDeleteSaveFlow != null);
					if (ImGui.Button(ProgressionPauseLocked ? "繼續##fsf_start_top" : "啟動 FSD##fsf_start_top"))
					{
						try
						{
                            var result = ProgressionPauseLocked ? ResumeProgression() : StartSelectedFarming(new FsdStartRequest());
                            _farmingError = result.Ok ? null : result.Error;
                            if (!result.Ok)
                            {
                                Service.Log.Warning($"[Necromancer] FSD start rejected: {result.Error}");
                            }
						}
						catch (Exception ex)
						{
							Service.Log.Error($"[Necromancer] FSF start failed: {ex}");
						}
					}
					ImGui.EndDisabled();
					ImGui.SameLine();
					ImGui.BeginDisabled(!fsdRunning);
					if (ImGui.Button("停止##fsf_stop_top"))
					{
						StopFullSelfDelving();
					}
					ImGui.EndDisabled();
                    if (CanRequestProgressionPause)
                    {
                        ImGui.SameLine();
                        if (ImGui.Button("本組出本後暫停")) RequestProgressionPause();
                    }
					ImGui.SameLine();
					if (ImGui.Button("導出日誌##fsf_export_logs_top"))
					{
						try
						{
							string archivePath =
								DeepDungeonRunLogExporter.ExportRecentToDesktop(_hostIdentity);
							_runLogExportNotice =
								$"日誌置於 {archivePath.Replace('\\', '/')}";
							_runLogExportFailed = false;
						}
						catch (Exception ex)
						{
							_runLogExportNotice = "最近三天沒有可導出的運行日誌";
							_runLogExportFailed = true;
							Service.Log.Error($"[FsdEngine] Run-log export failed: {ex}");
						}
					}
                    if (liveMaps != null)
                    {
                        ImGui.SameLine();
                        liveMaps.UpdateExportStatus();
                        ImGui.BeginDisabled(liveMaps.Exporting);
                        if (ImGui.Button("導出地圖##fsf_export_maps_top")) liveMaps.Export();
                        ImGui.EndDisabled();
                    }
					ImGui.Unindent();
				}

                if (liveMaps is { ExportStatus.Length: > 0 })
                    ImGui.TextWrapped(liveMaps.ExportStatus);

				if (!string.IsNullOrEmpty(_runLogExportNotice))
				{
					if (_runLogExportFailed)
						ImGui.TextColored(new Vector4(1f, 0.55f, 0.55f, 1f), _runLogExportNotice);
					else
						ImGui.TextDisabled(_runLogExportNotice);
				}

				if (_fsdStartDenialNoticeProvider is not null)
				{
					var startDenialNotice = _fsdStartDenialNoticeProvider();
					if (!string.IsNullOrEmpty(startDenialNotice))
						ImGui.TextColored(new Vector4(1f, 0.55f, 0.55f, 1f), startDenialNotice);
				}

                if (_checkpoint is { } checkpoint)
                {
                    var dungeonName = checkpoint.Plan.DungeonId switch { 1 => "死者宮殿", 2 => "天之御柱", _ => "朝聖交錯路" };
                    ImGui.TextWrapped($"死靈術士暫停紀錄 · {dungeonName} · 存檔 {checkpoint.Slot.Index + 1} · 第 {checkpoint.Slot.StartFloor} 層");
                }
                else if (_ddHost?.ActiveFarming?.Session.PersistPause != null)
                    ImGui.TextWrapped("本組通關並回到入口後暫停。");
                if (_checkpointIssue is { } issue)
                    ImGui.TextWrapped(issue.Message);

                DrawFarmingOptions();

                    ImGui.Separator();

                // FSD controls moved under scenario selection

					// Status now displayed under minimap via DrawFsdStatusBlock
				ImGui.Unindent();

				// World overlay: in-room waypoints (always shown when assist is active)
				try
				{
					bool assistActive = _ddHost?.AssistModeActive == true;
					if (liveMaps != null && assistActive && deepDungeon != null)
					{
						var dbg = _ddHost?.FloorController?.GetDebugSnapshot();
						if (dbg?.Work.Count > 0)
						{
							DrawFloorWorkOverlay(dbg.Work);
						}
					}
				}
				catch { }

			}
			catch (Exception ex)
			{
				ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"DeepDungeon Formal 面板錯誤: {ex.Message}");
			}
			
			ImGui.Spacing();
		}

		private void DrawDetailedMapSettings(bool runActive)
		{
			// Only fixed-floorset hoard farming exposes detailed-map controls.
			// Hiding this UI preserves the saved preference and survey runtime behavior.
			if (!IsFixedFloorsetHoard)
				return;

			string? scenarioKey =
				DetailedMapCatalogManager.GetScenarioKey(_fsfScenarioIndex);
			bool supported = scenarioKey != null;
			bool serviceConfigured = _detailedMapHostOptions.HasOnlineCatalogService;
			bool enabled = supported && serviceConfigured && _configuration.UseDetailedMap;
			DetailedMapCatalogStatusSnapshot? status =
				supported && serviceConfigured && _configuration.UseDetailedMap
					? _detailedMapCatalogManager.GetStatus(
						enabled: true,
						scenarioKey)
					: null;

			ImGui.Spacing();
			ImGui.BeginDisabled(!supported || !serviceConfigured || ProgressionPauseLocked);
			if (ImGui.Checkbox("使用詳細地圖##fsdDetailedMap", ref enabled))
			{
				_configuration.UseDetailedMap = enabled;
				_configuration.Save();
				_detailedMapCatalogManager.Update(
					enabled,
					scenarioKey,
					runActive);
			}
			ImGui.EndDisabled();
			ImGui.SameLine();
			ImGui.TextDisabled("(?)");
			if (ImGui.IsItemHovered())
			{
				string detailedMapTooltip = !serviceConfigured
					? DetailedMapHostOptions.NoOnlineCatalogServiceMessage
					: _detailedMapHostOptions.ContributesAnonymousEvidence
					? "深宮寶藏存在某些規律，使用這些經驗以加速探索。啟用該項目會下載該層級的詳細地圖，並上傳深宮信息以共建。上傳內容不含個人標識。"
					: "深宮寶藏存在某些規律，使用這些經驗以加速探索。";
				ImGui.BeginTooltip();
				ImGui.PushTextWrapPos(
					ImGui.GetFontSize() * 32f);
				ImGui.TextUnformatted(detailedMapTooltip);
				if (!supported)
				{
					ImGui.Spacing();
					ImGui.TextDisabled(
						"所選場景尚未發佈詳細地圖資料版本。");
				}
				ImGui.PopTextWrapPos();
				ImGui.EndTooltip();
			}
			if (enabled && status?.RetryAvailable == true)
			{
				ImGui.SameLine();
				if (ImGui.SmallButton("重試下載##fsdDetailedMapRetry"))
				{
					_detailedMapCatalogManager.RetryUpdate(
						enabled: true,
						selectedScenarioKey: scenarioKey,
						runActive: runActive);
				}
			}

			if (!serviceConfigured)
			{
				ImGui.TextWrapped(DetailedMapHostOptions.NoOnlineCatalogServiceMessage);
				return;
			}

			if (!supported || !_configuration.UseDetailedMap)
				return;

			DetailedMapCatalogStatusSnapshot detailedMapStatus = status!.Value;
			ImGui.TextDisabled(
				detailedMapStatus.ReleaseId == null
					? "資料版本: 不可用"
					: $"數據版本: {detailedMapStatus.ReleaseId}");
			if (detailedMapStatus.CandidateCount > 0)
			{
				double coverage =
					(double)detailedMapStatus.KnownSuccessorCount /
					detailedMapStatus.CandidateCount *
					100d;
				ImGui.TextDisabled(
					$"詳細寶藏數據: {detailedMapStatus.KnownSuccessorCount}/{detailedMapStatus.CandidateCount} ({coverage:F1}%)");
			}
			if (!detailedMapStatus.HasValidCatalog ||
			    detailedMapStatus.Checking ||
			    detailedMapStatus.Message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
			    detailedMapStatus.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase))
			{
				ImGui.TextWrapped(detailedMapStatus.Message);
			}
		}

		public void DrawGeneralAssistantSettings()
		{
            ImGui.BeginDisabled(ProgressionPauseLocked);
			ImGui.Text("恢復藥劑設定");
			var autoRecoveryPotion = _configuration.AutoUseRecoveryPotion;
			if (ImGui.Checkbox("自動使用恢復藥劑", ref autoRecoveryPotion))
			{
				_configuration.AutoUseRecoveryPotion = autoRecoveryPotion;
				_configuration.Save();
			}

			var hpThreshold = _configuration.RecoveryPotionHpThresholdPercent;
			if (ImGui.SliderInt("血量閾值 (%)", ref hpThreshold, 1, 99))
			{
				_configuration.RecoveryPotionHpThresholdPercent = hpThreshold;
				_configuration.Save();
			}

			ImGui.TextDisabled($"當血量低於 {hpThreshold}% 時自動使用目前迷宮的恢復藥劑");
            ImGui.EndDisabled();
		}
		
		private void DrawDeepDungeonOverlayToggles()
		{
			ImGui.Spacing();
			if (!ImGui.CollapsingHeader("疊加顯示控制", ImGuiTreeNodeFlags.DefaultOpen))
				return;

			ImGui.Indent();
			DrawOverlayToggleCheckbox(
				"房間中心（世界）",
				_configuration.NecromancerShowRoomCenterOverlay,
				v => _configuration.NecromancerShowRoomCenterOverlay = v,
				"控制是否在 3D 世界中顯示已發現房間中心的綠色十字。");

			DrawOverlayToggleCheckbox(
				"陷阱（世界）",
				_configuration.NecromancerShowTrapOverlay,
				v => _configuration.NecromancerShowTrapOverlay = v,
				"控制是否在世界疊加層中顯示 Palantir 陷阱標記。");

			DrawOverlayToggleCheckbox(
				"自動導航房間路徑（世界）",
				_configuration.NecromancerShowRoomPathOverlay,
				v => _configuration.NecromancerShowRoomPathOverlay = v,
				"控制是否顯示順序自動導航的路徑線和房間指示器。");

			DrawOverlayToggleCheckbox(
				"背景碰撞調試（世界）",
				_configuration.NecromancerShowBgCollisionOverlay,
				v => _configuration.NecromancerShowBgCollisionOverlay = v,
				"控制是否顯示背景碰撞調試圖形。");

			ImGui.Unindent();
		}

		private void DrawOverlayToggleCheckbox(string label, bool currentValue, Action<bool> setter, string hint)
		{
			var value = currentValue;
            ImGui.BeginDisabled(ProgressionPauseLocked);
			if (ImGui.Checkbox(label, ref value))
			{
				setter(value);
				_configuration.Save();
			}
            ImGui.EndDisabled();

			if (!string.IsNullOrEmpty(hint))
			{
				UiHelpers.DrawGrayTipText(hint);
			}
		}

		private unsafe void DrawDeepDungeonMiniMap(InstanceContentDeepDungeon* deepDungeon)
		{
			DrawDeepDungeonMiniMap(deepDungeon, true);
		}

		private unsafe void DrawDeepDungeonMiniMap(InstanceContentDeepDungeon* deepDungeon, bool showActions)
        {
			ValidateRoomPresentationScope(deepDungeon);
            const float tileSize = 36f;
            const float gap = 6f;
            var drawList = ImGui.GetWindowDrawList();
            int hoveredRoom = -1;
            var startCursorPos = ImGui.GetCursorPos();
            var topLeft = ImGui.GetCursorScreenPos();
            var totalSize = new Vector2(5 * tileSize + 4 * gap, 5 * tileSize + 4 * gap);
			int playerRoom = DeepDungeon.Fsd.Dalamud.GameState.RoomGraph.GetLocalPlayerRoomIndex(deepDungeon);

            ImGui.BeginGroup();

			// Build chest map per room
            var chestsByRoom = new List<byte>[25];
            for (int i = 0; i < chestsByRoom.Length; i++) chestsByRoom[i] = new List<byte>(2);
            var chests = deepDungeon->Chests;
            for (int i = 0; i < chests.Length; i++)
            {
                var room = chests[i].RoomIndex;
                if (room >= 0 && room < 25)
                {
                    chestsByRoom[room].Add(chests[i].ChestType);
                }
            }

            // Colors
			var colRoomRevealed = ImGui.GetColorU32(new Vector4(0.34f, 0.36f, 0.40f, 1.0f));
			var colRoomHidden = ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.09f, 0.88f));
			var colNoRoom = ImGui.GetColorU32(new Vector4(0.50f, 0.41f, 0.32f, 1f));
            var colOutline = ImGui.GetColorU32(new Vector4(0.35f, 0.35f, 0.38f, 1.0f));
			var colConn = ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
			var colConnHidden = ImGui.GetColorU32(new Vector4(0.78f, 0.78f, 0.78f, 0.70f));
            var colPlayer = ImGui.GetColorU32(new Vector4(1.0f, 0.95f, 0.2f, 1.0f));
            var colChestBronze = ImGui.GetColorU32(new Vector4(0.80f, 0.50f, 0.20f, 1.0f));
            var colChestSilver = ImGui.GetColorU32(new Vector4(0.75f, 0.75f, 0.80f, 1.0f));
            var colChestGold = ImGui.GetColorU32(new Vector4(1.00f, 0.84f, 0.00f, 1.0f));
			var colHiddenHatch = ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 0.13f));
			var colNeutralBorder = ImGui.GetColorU32(new Vector4(0.64f, 0.66f, 0.70f, 1f));
			var colAssistCompleted = ImGui.GetColorU32(new Vector4(131f / 255f, 192f / 255f, 154f / 255f, 1f));
			var colAssistPending = ImGui.GetColorU32(new Vector4(145f / 255f, 178f / 255f, 197f / 255f, 1f));

			bool assistModeActive = _ddHost?.AssistModeActive == true;
			HashSet<int>? assistCompletedRooms = null;
			IReadOnlyList<int>? assistPlannedRooms = null;
			if (assistModeActive)
			{
				try
				{
					var assistSnapshot = _ddHost?.FloorController.GetDebugSnapshot();
					if (assistSnapshot != null)
					{
						assistCompletedRooms = assistSnapshot.CompletedRooms;
						assistPlannedRooms = assistSnapshot.RoomPath;
					}
				}
				catch { }
			}

            var map = deepDungeon->MapData;
			var structuralCenterAvailability = new bool[25];
			for (int index = 0; index < structuralCenterAvailability.Length; index++)
				structuralCenterAvailability[index] = Map.MapPos.TryGetRoomCenter(
					deepDungeon,
					index,
					out _);
			bool boardFormKnown = DeepDungeonBoardFormResolver.TryResolve(
				structuralCenterAvailability,
				out DeepDungeonBoardForm boardForm);

            // Roads occupy only the gaps between tiles and are drawn first so
            // room fills, landmarks, chest markers, and badges remain readable.
            static void DrawConnectionRoad(
				ImDrawListPtr dl,
				Vector2 center,
				float tileSize,
				float gap,
				uint col,
				InstanceContentDeepDungeon.RoomFlags flags)
            {
				float thick = 3.0f;
				uint shadow = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.55f));
				float shadowThick = thick + 1.0f;
				float halfTile = tileSize * 0.5f;
				void Draw(Vector2 direction)
				{
					Vector2 from = center + direction * halfTile;
					Vector2 to = center + direction * (halfTile + gap);
					dl.AddLine(from, to, shadow, shadowThick);
					dl.AddLine(from, to, col, thick);
				}
				if ((flags & InstanceContentDeepDungeon.RoomFlags.ConnectionN) != 0)
					Draw(new Vector2(0f, -1f));
				if ((flags & InstanceContentDeepDungeon.RoomFlags.ConnectionS) != 0)
					Draw(new Vector2(0f, 1f));
				if ((flags & InstanceContentDeepDungeon.RoomFlags.ConnectionW) != 0)
					Draw(new Vector2(-1f, 0f));
				if ((flags & InstanceContentDeepDungeon.RoomFlags.ConnectionE) != 0)
					Draw(new Vector2(1f, 0f));
            }

			static void DrawHatch(ImDrawListPtr dl, Vector2 topLeft, Vector2 bottomRight, uint col, float spacing, float thickness)
			{
				var size = bottomRight - topLeft;
				for (float o = -size.Y; o <= size.X; o += spacing)
				{
					var start = new Vector2(topLeft.X + MathF.Max(o, 0f), topLeft.Y + MathF.Max(-o, 0f));
					float ex = start.X + size.Y;
					float ey = start.Y + size.Y;
					if (ex > bottomRight.X)
					{
						float dx = bottomRight.X - start.X;
						ex = bottomRight.X;
						ey = start.Y + dx;
					}
					if (ey > bottomRight.Y)
					{
						float dy = bottomRight.Y - start.Y;
						ey = bottomRight.Y;
						ex = start.X + dy;
					}
					dl.AddLine(start, new Vector2(ex, ey), col, thickness);
				}
			}

            // Draw roads before the room tiles so only the inter-tile gaps remain visible.
			for (int i = 0; i < map.Length; i++)
			{
				int row = i / 5;
				int col = i % 5;
				Vector2 tileTopLeft = topLeft + new Vector2(col * (tileSize + gap), row * (tileSize + gap));
				Vector2 tileCenter = tileTopLeft + new Vector2(tileSize * 0.5f, tileSize * 0.5f);
				DrawConnectionRoad(
					drawList,
					tileCenter,
					tileSize,
					gap,
					(map[i] & InstanceContentDeepDungeon.RoomFlags.Revealed) != 0
						? colConn
						: colConnHidden,
					map[i]);
			}

            // Draw tiles
            for (int i = 0; i < map.Length; i++)
            {
                int row = i / 5;
                int col = i % 5;
                var tileTopLeft = topLeft + new Vector2(col * (tileSize + gap), row * (tileSize + gap));
                var tileBottomRight = tileTopLeft + new Vector2(tileSize, tileSize);
                var tileCenter = (tileTopLeft + tileBottomRight) * 0.5f;

                var flags = map[i];
				bool actualRoom = flags != InstanceContentDeepDungeon.RoomFlags.None;
                bool revealed = (flags & InstanceContentDeepDungeon.RoomFlags.Revealed) != 0;
                bool home = (flags & InstanceContentDeepDungeon.RoomFlags.Home) != 0;
				bool passage = (flags & InstanceContentDeepDungeon.RoomFlags.Passage) != 0;
                bool ret = (flags & InstanceContentDeepDungeon.RoomFlags.Return) != 0;
				Vector3 roomCenter;
				bool hasCenterData = Map.MapPos.TryGetRoomCenter(
					deepDungeon,
					i,
					out roomCenter);

                // Room background + outline
				bool structuralBrown = boardFormKnown &&
					DeepDungeonBoardFormResolver.IsBrownStructuralCell(boardForm, i);
				uint tileFill = structuralBrown
					? colNoRoom
					: revealed
						? colRoomRevealed
						: colRoomHidden;
                drawList.AddRectFilled(tileTopLeft, tileBottomRight, tileFill, 4f);
				if (actualRoom && hasCenterData && !revealed)
				{
					DrawHatch(drawList, tileTopLeft, tileBottomRight, colHiddenHatch, 6f, 1.2f);
				}

				uint borderColor = colNeutralBorder;
				float borderThickness = 1.5f;
				if (assistModeActive)
				{
					bool assistFinished = assistCompletedRooms?.Contains(i) == true;
					bool assistTracked = assistFinished || (assistPlannedRooms?.Contains(i) == true);
					if (assistTracked)
					{
						borderColor = assistFinished ? colAssistCompleted : colAssistPending;
						borderThickness = 2.25f;
					}
				}

                drawList.AddRect(tileTopLeft, tileBottomRight, borderColor, 4f, ImDrawFlags.RoundCornersAll, borderThickness);
				if (home)
					DrawDeepDungeonLandmark(
						drawList,
						tileTopLeft,
						1f,
						DeepDungeonLandmarkKind.Home,
						active: false);

				if (passage)
					DrawDeepDungeonLandmark(
						drawList,
						tileTopLeft,
						1f,
						DeepDungeonLandmarkKind.Passage,
						deepDungeon->PassageProgress >= 11);
				else if (ret)
					DrawDeepDungeonLandmark(
						drawList,
						tileTopLeft,
						1f,
						DeepDungeonLandmarkKind.Return,
						deepDungeon->ReturnProgress >= 11);
                // Chests (draw up to 3 small squares at top row)
                var chestList = chestsByRoom[i];
                if (chestList.Count > 0)
                {
                    float s = 8f;
                    for (int k = 0; k < chestList.Count && k < 3; k++)
                    {
                        var cTL = tileTopLeft + new Vector2(2f + k * (s + 2f), 2f);
                        var cBR = cTL + new Vector2(s, s);
                        uint cc = chestList[k] switch
                        {
                            1 => colChestBronze,
                            2 => colChestSilver,
                            3 => colChestGold,
                            _ => colChestBronze
                        };
                        drawList.AddRectFilled(cTL, cBR, cc, 2f);
                        drawList.AddRect(cTL, cBR, colOutline, 2f);
                }
                }

                // Player arrow
                if (i == playerRoom)
                {
                    float rot = 0f;
                    try { rot = Service.LocalPlayer?.Rotation ?? 0f; } catch { }
                    DrawPlayerArrow(drawList, tileCenter, MathF.PI - rot, colPlayer);
                }

				// Clickable overlay for room: invisible button matching tile rect.
				// On click, resolve world position (or passage) and move via VNAV.
                ImGui.SetCursorScreenPos(tileTopLeft);
                ImGui.InvisibleButton($"dd_room_btn_{i}", new Vector2(tileSize, tileSize));
				bool canNavigate = hasCenterData || passage;
                if (ImGui.IsItemHovered())
                {
					// subtle hover highlight (green if we can click/move, gray if missing)
					uint hl = ImGui.GetColorU32(canNavigate
                        ? new Vector4(0.2f, 0.6f, 0.3f, 0.25f)
                        : new Vector4(0.5f, 0.5f, 0.5f, 0.20f));
                    drawList.AddRectFilled(tileTopLeft, tileBottomRight, hl, 4f);
					if (!canNavigate)
                    {
						ImGui.SetTooltip("當前樓層組或地圖佈局沒有可用的房間中心。");
					}
					else if (passage && !hasCenterData)
					{
						ImGui.SetTooltip("點擊以直接前往通路。");
                    }
                    hoveredRoom = i;
				}
				if (actualRoom && ImGui.IsItemClicked(ImGuiMouseButton.Right))
					ToggleRoomPresentation(deepDungeon, i);
				if (canNavigate && ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    try
                    {
						// refuse navigation if room is not passable from current room
						if (playerRoom >= 0 && !IsRoomReachable(deepDungeon, playerRoom, i))
                        {
                            Service.Log.Info($"[Necromancer] 目標房間 {i} 目前不可通行，已拒絕導航。");
                        }
						else if (passage)
						{
							if (PassageLocator.TryResolveRequestedRoom(deepDungeon, i, out var passageDest, out bool usedActor))
							{
								if (DeepDungeon.Fsd.Dalamud.moveHelper.VNav.SimpleMove.PathfindAndMoveTo(passageDest, false))
								{
									if (usedActor)
									{
										ClearPendingPassageFollowup();
									}
									else
									{
										BeginPassageFollowup(deepDungeon, i);
									}
								}
								else
								{
									Service.Log.Warning("[Necromancer] 無法派遣 VNAV 前往通路，請稍後重試。");
								}
							}
							else
							{
								Service.Log.Warning("[Necromancer] 無法解析通路位置，請稍後再試。");
							}
						}
						else if (hasCenterData)
                        {
							ClearPendingPassageFollowup();
							TryNavigateWithRoomFallback(deepDungeon, playerRoom, i, roomCenter);
                        }
                    }
                    catch (Exception ex)
                    {
                        Service.Log.Error($"[Necromancer] Click-to-move failed for room {i}: {ex}");
                    }
                }
            }

			if (showActions)
			{
				// Room indices overlay
				for (int row = 0; row < 5; row++)
				{
					for (int col = 0; col < 5; col++)
					{
						int idx = row * 5 + col;
						var tileTopLeftIdx = topLeft + new Vector2(col * (tileSize + gap), row * (tileSize + gap));
						var tileBottomRightIdx = tileTopLeftIdx + new Vector2(tileSize, tileSize);
						var centerIdx = (tileTopLeftIdx + tileBottomRightIdx) * 0.5f;
						var text = idx.ToString();
						var size = ImGui.CalcTextSize(text);
						var pos = centerIdx - size * 0.5f;
						uint colText = ImGui.GetColorU32(new Vector4(0.9f, 0.9f, 0.9f, 0.9f));
						drawList.AddText(pos, colText, text);
					}
				}
                }

            // Reserve layout space exactly the size of the map, avoiding extra blank area
            ImGui.SetCursorPos(startCursorPos);
            ImGui.Dummy(totalSize);
            ImGui.EndGroup();

            ImGui.SameLine(0f, 16f * ImGuiHelpers.GlobalScale);
            ImGui.BeginGroup();
            if (showActions)
            {
	            DrawRealWorldCenterMapPanel(deepDungeon);
            }
            else
            {
	            DrawPomanderMagicitePanel(deepDungeon);
            }
            ImGui.EndGroup();
            ImGui.NewLine();

			if (showActions)
			{
				// Warnings below map: Palantir fetch status
				var (fetchFailed, hasLocalMarkers) = GetPalantirStatus();
				if (fetchFailed)
				{
					if (hasLocalMarkers)
						ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "警告：無法從 Palantir 取得資料，使用先前讀取的標記。");
					else
						ImGui.TextColored(new Vector4(1.0f, 0.35f, 0.35f, 1.0f), "警告：無法從 Palantir 取得資料，且本機無標記資料。");
				}

			// Action: Navigate to Passage
		ImGui.Spacing();
	// Legacy manual passage navigation removed - FloorPhaseController handles this automatically

			// (Banded farm controls shown in Formal panel)
				// WORLD OVERLAY: draw conspicuous marks at discovered centers (while debug panel is open)
				try
				{
					var player = Service.LocalPlayer;
					float y = player?.Position.Y ?? 0f;
					if (_configuration.NecromancerShowRoomCenterOverlay)
					{
						for (int i = 0; i < 25; i++)
						{
							if (MapPos.TryGetRoomCenter(deepDungeon, i, out var where))
							{
								uint col = ImGui.GetColorU32(new Vector4(0.15f, 0.95f, 0.25f, 1.0f));
								WorldDrawHelper.DrawWorldCircle(where, 2.0f, col);
								WorldDrawHelper.DrawWorldLine(where + new Vector3(-1.5f, 0, 0), where + new Vector3(1.5f, 0, 0), col);
								WorldDrawHelper.DrawWorldLine(where + new Vector3(0, 0, -1.5f), where + new Vector3(0, 0, 1.5f), col);
								if (i == hoveredRoom)
								{
									uint col2 = ImGui.GetColorU32(new Vector4(1.0f, 0.95f, 0.15f, 1.0f));
									WorldDrawHelper.DrawWorldCircle(where, 2.6f, col2);
								}
							}
						}
					}

					if (_configuration.NecromancerShowTrapOverlay)
					{
						try
						{
							var traps = Map.PalantirData.GetTrapPositionsCurrentTerritory();
							if (traps != null && traps.Count > 0)
							{
								uint colTrap = ImGui.GetColorU32(new Vector4(1.0f, 0.25f, 0.25f, 1.0f));
								foreach (var tpos in traps)
								{
									var whereTrap = new Vector3(tpos.X, tpos.Y, tpos.Z);
									WorldDrawHelper.DrawWorldCircle(whereTrap, 1.6f, colTrap);
									WorldDrawHelper.DrawWorldLine(whereTrap + new Vector3(-1.0f, 0, -1.0f), whereTrap + new Vector3(1.0f, 0, 1.0f), colTrap);
									WorldDrawHelper.DrawWorldLine(whereTrap + new Vector3(-1.0f, 0, 1.0f), whereTrap + new Vector3(1.0f, 0, -1.0f), colTrap);
								}
							}
						}
						catch { }
					}

					try
					{
						var dbg = _ddHost?.FloorController.GetDebugSnapshot();
						if (dbg != null)
						{
							if (_configuration.NecromancerShowRoomPathOverlay &&
							    dbg.RoomPath != null && dbg.RoomPath.Count > 0)
							{
								uint colCompleted = ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 0.6f));
								uint colCurrent = ImGui.GetColorU32(new Vector4(0.2f, 1.0f, 0.2f, 1.0f));
								uint colPending = ImGui.GetColorU32(new Vector4(1.0f, 0.9f, 0.2f, 0.8f));
								uint colPath = ImGui.GetColorU32(new Vector4(0.6f, 0.8f, 1.0f, 0.5f));
								
								for (int i = 0; i < dbg.RoomPath.Count - 1; i++)
								{
									int room1 = dbg.RoomPath[i];
									int room2 = dbg.RoomPath[i + 1];
									
									if (MapPos.TryGetRoomCenter(deepDungeon, room1, out var pos1) &&
									    MapPos.TryGetRoomCenter(deepDungeon, room2, out var pos2))
									{
										WorldDrawHelper.DrawWorldLine(pos1, pos2, colPath);
									}
								}
								
								for (int i = 0; i < dbg.RoomPath.Count; i++)
								{
									int room = dbg.RoomPath[i];
									if (MapPos.TryGetRoomCenter(deepDungeon, room, out var center))
									{
										bool isCompleted = dbg.CompletedRooms.Contains(room);
										bool isCurrent = i == dbg.CurrentRoomIdx;
										
										uint col = isCompleted ? colCompleted : (isCurrent ? colCurrent : colPending);
										float radius = isCurrent ? 2.5f : 1.5f;
										
										WorldDrawHelper.DrawWorldCircle(center, radius, col);
									}
								}
							}
						}
					}
					catch { }

					if (_showBgCollisionOverlay && _configuration.NecromancerShowBgCollisionOverlay)
					{
						try
						{
							_bgCollisionDebug.DrawWorldOverlay();
						}
						catch (Exception ex)
						{
							try { Service.Log.Error($"[Necromancer] BG collision overlay error: {ex}"); } catch { }
						}
					}
				}
				catch { }
			}

			if (_selectedRoomIndex >= 0 && _selectedRoomIndex < map.Length)
			{
				int selectedRow = _selectedRoomIndex / 5;
				int selectedColumn = _selectedRoomIndex % 5;
				Vector2 selectedTopLeft = topLeft + new Vector2(
					selectedColumn * (tileSize + gap),
					selectedRow * (tileSize + gap));
				DrawDeepDungeonRoomSelectionBrackets(
					drawList,
					selectedTopLeft,
					selectedTopLeft + new Vector2(tileSize, tileSize),
					7f,
					2f);
			}
        }

        private static void DrawFloorWorkOverlay(IReadOnlyList<DeepDungeon.Fsd.Dalamud.Coord.ObjectiveSite> work)
        {
            Vector3? previous = null;
            for (int index = 0; index < work.Count; index++)
            {
                var site = work[index];
                var position = site.Position;
                if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                    continue;
                uint color = ImGui.GetColorU32(index == 0
                    ? new Vector4(0.2f, 0.85f, 1f, 0.95f)
                    : new Vector4(1f, 0.78f, 0.35f, 0.75f));
                if (previous is { } from)
                    WorldDrawHelper.DrawWorldLine(from, position, color);
                WorldDrawHelper.DrawWorldText(position + new Vector3(0f, 0.35f, 0f), color, $"{index + 1} {site.Kind}");
                previous = position;
            }
        }

		private unsafe bool TryNavigateWithRoomFallback(InstanceContentDeepDungeon* dd, int playerRoom, int targetRoom, Vector3 finalDest)
		{
			if (DeepDungeon.Fsd.Dalamud.moveHelper.VNav.SimpleMove.PathfindAndMoveTo(finalDest, false))
				return true;

			if (dd == null || playerRoom < 0 || targetRoom < 0)
				return false;

			var player = Service.LocalPlayer;
			if (player == null)
				return false;

			var navigator = new ProgressiveRoomNavigator();
			navigator.Configure(dd, playerRoom, targetRoom, finalDest);

			while (navigator.IsActive && navigator.TryHandleFailure(dd, playerRoom, out var stagedDest))
			{
				if (DeepDungeon.Fsd.Dalamud.moveHelper.VNav.SimpleMove.PathfindAndMoveTo(stagedDest, false))
				{
					Service.Log.Info($"[Necromancer] Click navigation fallback via {navigator.StageLabel}");
					return true;
				}
			}

			Service.Log.Warning("[Necromancer] Unable to route to requested room (manual click)");
			return false;
		}

		private unsafe void DrawPomanderMagicitePanel(InstanceContentDeepDungeon* deepDungeon)
		{
			try
			{
				if (deepDungeon == null)
				{
					ImGui.TextDisabled("尚未讀取深層地牢資料。");
					return;
				}

				if (DutyTransitionUtil.IsBetweenAreas())
				{
					ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "樓層切換中，等待資料更新…");
					return;
				}

				const int pomanderRows = 3;
				const int pomanderColumns = 6;
				float scale = ImGuiHelpers.GlobalScale;
				float circleDiameter = 30f * scale;
				float cellSpacing = 4f * scale;
				float cellSize = circleDiameter + cellSpacing;
				float circlePadding = 2f * scale;

				var visuals = GetPomanderVisuals(deepDungeon->DeepDungeonId);

				DrawPomanderGrid(deepDungeon, visuals, pomanderRows, pomanderColumns, cellSize, circlePadding);

				DrawMagiciteRow(deepDungeon, cellSize, circlePadding);
			}
			catch (Exception ex)
			{
				ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"道具面板錯誤: {ex.Message}");
			}
		}

		private unsafe void DrawPomanderGrid(
			InstanceContentDeepDungeon* deepDungeon,
			PomanderVisual[] visuals,
			int rows,
			int columns,
			float cellSize,
			float circlePadding)
		{
			var drawList = ImGui.GetWindowDrawList();
			var items = deepDungeon->Items;
			var baseCursor = ImGui.GetCursorPos();

			ImGui.PushID("pomander-grid");
			for (int row = 0; row < rows; row++)
			{
				for (int col = 0; col < columns; col++)
				{
					int slotIndex = row * columns + col;
					var cursor = new Vector2(baseCursor.X + col * cellSize, baseCursor.Y + row * cellSize);
					ImGui.SetCursorPos(cursor);

					bool hasVisual = slotIndex < visuals.Length && visuals[slotIndex].IsValid;
					if (!hasVisual)
					{
						ImGui.Dummy(new Vector2(cellSize, cellSize));
						continue;
					}

					ImGui.InvisibleButton($"slot_{slotIndex}", new Vector2(cellSize, cellSize));
					var cellMin = ImGui.GetItemRectMin();
					var cellMax = ImGui.GetItemRectMax();
					var center = (cellMin + cellMax) * 0.5f;
					float radius = (MathF.Min(cellMax.X - cellMin.X, cellMax.Y - cellMin.Y) - circlePadding * 2f) * 0.5f;

					bool slotValid = slotIndex < items.Length;
					var slot = slotValid ? items[slotIndex] : default;
					byte count = slotValid ? slot.Count : (byte)0;
					bool hasCount = count > 0;
					bool isActive = slotValid && slot.IsActive;
					bool isUsable = slotValid && slot.IsUsable;
					bool canUse = isUsable && hasCount;
					var visual = visuals[slotIndex];

					uint fillColor = ImGui.GetColorU32(isActive
						? new Vector4(0.26f, 0.34f, 0.48f, 1f)
						: (hasCount ? new Vector4(0.2f, 0.24f, 0.30f, 1f) : new Vector4(0.1f, 0.11f, 0.13f, 1f)));
					uint borderColor = ImGui.GetColorU32(isActive
						? new Vector4(0.95f, 0.85f, 0.45f, 1f)
						: new Vector4(0.45f, 0.48f, 0.54f, 1f));

					drawList.AddCircleFilled(center, radius, fillColor, 64);
					drawList.AddCircle(center, radius, borderColor, 64, 2f * ImGuiHelpers.GlobalScale);

					uint iconId = visual.IconId;
					var iconTexture = GetIconTexture(iconId);
					if (iconTexture != null && iconTexture.TryGetWrap(out var wrap, out _) && wrap != null)
					{
						var iconSize = new Vector2(radius * 1.7f);
						var iconMin = center - iconSize * 0.5f;
						var iconMax = center + iconSize * 0.5f;

						var dimTint = new Vector4(0.55f, 0.55f, 0.55f, 0.75f);
						var brightTint = Vector4.One;

						Vector4 iconTintVec;
						if (hasCount)
						{
							iconTintVec = brightTint;
						}
						else if (isActive)
						{
							float t = (float)ImGui.GetTime();
							float cycle = t % 4.5f;
							float pulse = cycle < 1.5f
								? 1.0f
								: cycle < 3.0f
									? 1.0f - ((cycle - 1.5f) / 1.5f)
									: (cycle - 3.0f) / 1.5f;
							iconTintVec = Vector4.Lerp(dimTint, brightTint, pulse);
						}
						else
						{
							iconTintVec = dimTint;
						}

						uint tint = ImGui.GetColorU32(iconTintVec);
						drawList.AddImage(wrap.Handle, iconMin, iconMax, Vector2.Zero, Vector2.One, tint);
					}
					else
					{
						var abbr = BuildPomanderAbbreviation(visual.Name);
						var textSize = ImGui.CalcTextSize(abbr);
						var textPos = center - textSize * 0.5f;
						uint textColor = ImGui.GetColorU32(hasCount ? new Vector4(0.95f, 0.95f, 0.95f, 1f) : new Vector4(0.6f, 0.6f, 0.6f, 1f));
						drawList.AddText(textPos, textColor, abbr);
					}

					if (!hasCount && !isActive)
					{
						uint maskColor = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.45f));
						drawList.AddCircleFilled(center, radius, maskColor, 64);
					}

					string countText = slotValid ? count.ToString() : "0";
					var countSize = ImGui.CalcTextSize(countText);
					// var countPos = new Vector2(cellMax.X - countSize.X + 2.6f * ImGuiHelpers.GlobalScale, cellMin.Y - 2.6f * ImGuiHelpers.GlobalScale);
					var countPos = new Vector2(cellMax.X - countSize.X, cellMin.Y);
					uint countColor = ImGui.GetColorU32(hasCount ? new Vector4(1f, 0.95f, 0.75f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f));
					drawList.AddText(countPos, countColor, countText);

					if (slotValid && ImGui.IsItemClicked(ImGuiMouseButton.Left))
					{
						if (canUse)
						{
							try
							{
								Actions.FsdItemExecutor.UsePomander(deepDungeon, (uint)slotIndex);
							}
							catch (Exception ex)
							{
								Service.Log.Error($"[Necromancer] Failed to use pomander slot {slotIndex}: {ex}");
							}
						}
					}

					if (ImGui.IsItemHovered())
					{
						ImGui.BeginTooltip();
						string name = visual.Name;
						ImGui.Text(name);
						if (slotValid)
						{
							ImGui.Text($"數量: {count}");
							ImGui.Text($"可用: {(slot.IsUsable ? "是" : "否")}");
							ImGui.Text($"啟用: {(slot.IsActive ? "是" : "否")}");
							if (canUse)
							{
								ImGui.TextColored(new Vector4(0.6f, 1.0f, 0.6f, 1f), "點擊以使用");
							}
							else if (hasCount && !isUsable)
							{
								ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.4f, 1f), "目前不可使用");
							}
						}
						else
						{
							ImGui.Text("未啟用");
						}
						ImGui.EndTooltip();
					}
				}
			}
			ImGui.PopID();

			ImGui.SetCursorPos(new Vector2(baseCursor.X, baseCursor.Y + rows * cellSize));
		}

		private unsafe void DrawMagiciteRow(InstanceContentDeepDungeon* deepDungeon, float cellSize, float circlePadding)
		{
			var drawList = ImGui.GetWindowDrawList();
			var baseCursor = ImGui.GetCursorPos();
			var magicite = deepDungeon->Magicite;
			var visuals = GetMagiciteVisuals(deepDungeon->DeepDungeonId);

			ImGui.PushID("magicite-row");
			for (int i = 0; i < magicite.Length; i++)
			{
				var cursor = new Vector2(baseCursor.X + i * cellSize, baseCursor.Y);
				ImGui.SetCursorPos(cursor);
				ImGui.InvisibleButton($"slot_{i}", new Vector2(cellSize, cellSize));
				var cellMin = ImGui.GetItemRectMin();
				var cellMax = ImGui.GetItemRectMax();
				var center = (cellMin + cellMax) * 0.5f;
				float radius = (MathF.Min(cellMax.X - cellMin.X, cellMax.Y - cellMin.Y) - circlePadding * 2f) * 0.5f;

				byte typeId = magicite[i];
				bool occupied = typeId != 0;
				MagiciteVisual visual = MagiciteVisual.Unresolved(
					i,
					0,
					"No DeepDungeon.MagiciteSlot definition is available for this item.");
				if (occupied)
				{
					if (MagiciteSlotMapping.TryGetDefinitionIndex(
							typeId,
							visuals.Length,
							out int definitionIndex))
					{
						visual = visuals[definitionIndex];
					}
					else
					{
						visual = MagiciteVisual.Unresolved(
							i,
							0,
							$"Runtime magicite type {typeId} has no corresponding DeepDungeon.MagiciteSlot definition.");
					}
				}
				bool iconDrawn = false;

				uint background = ImGui.GetColorU32(new Vector4(0.08f, 0.1f, 0.14f, 1f));
				uint border = ImGui.GetColorU32(occupied ? new Vector4(0.3f, 0.85f, 0.95f, 1f) : new Vector4(0.45f, 0.45f, 0.48f, 1f));

				drawList.AddCircleFilled(center, radius, background, 64);
				drawList.AddCircle(center, radius, border, 64, 2f * ImGuiHelpers.GlobalScale);

				if (occupied)
				{
					ISharedImmediateTexture? iconTexture = visual.IsResolved
						? GetIconTexture(visual.IconId)
						: null;
					if (visual.IsResolved && iconTexture != null &&
						iconTexture.TryGetWrap(out var wrap, out _) &&
						wrap != null)
					{
						Vector2 iconSize = new(radius * 1.7f);
						Vector2 iconMin = center - iconSize * 0.5f;
						Vector2 iconMax = center + iconSize * 0.5f;
						drawList.AddImage(
							wrap.Handle,
							iconMin,
							iconMax,
							Vector2.Zero,
							Vector2.One,
							0xFFFFFFFF);
						iconDrawn = true;
					}
					else
					{
						string label = visual.IsResolved ? "!" : "?";
						var textSize = ImGui.CalcTextSize(label);
						var textPos = center - textSize * 0.5f;
						uint textColor = ImGui.GetColorU32(visual.IsResolved
							? new Vector4(1f, 0.78f, 0.35f, 1f)
							: new Vector4(1f, 0.45f, 0.35f, 1f));
						drawList.AddText(textPos, textColor, label);
					}

					if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
					{
						try
						{
							Actions.FsdItemExecutor.UseStone(deepDungeon, (uint)i);
						}
						catch (Exception ex)
						{
							Service.Log.Error($"[Necromancer] Failed to use magicite slot {i}: {ex}");
						}
					}
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.BeginTooltip();
					ImGui.Text($"槽位 {i + 1}");
					if (occupied)
					{
						if (visual.IsResolved)
						{
							ImGui.Text(visual.Name);
							if (!iconDrawn)
								ImGui.TextDisabled($"官方圖示無法載入 (icon {visual.IconId})");
						}
						else
						{
							ImGui.TextColored(
								new Vector4(1f, 0.45f, 0.35f, 1f),
								"官方魔石資料無法解析");
							ImGui.TextDisabled(visual.ResolutionError);
						}
						ImGui.Text($"類型 ID: {typeId}");
						if (visual.RowId != 0)
							ImGui.Text($"資料列: {visual.RowId}");
					}
					else
					{
						ImGui.Text("無持有");
					}
					ImGui.EndTooltip();
				}
			}
			ImGui.PopID();

			ImGui.SetCursorPos(new Vector2(baseCursor.X, baseCursor.Y + cellSize));
		}

		private static void DrawPlayerArrow(
			ImDrawListPtr drawList,
			Vector2 center,
			float rotationRadians,
			uint fillColor)
		{
			// Keep the same 10/6-pixel arrow used by the minimap.
			Vector2 p0 = new(0f, -10f);
			Vector2 p1 = new(-6f, 6f);
			Vector2 p2 = new(6f, 6f);
			float cos = MathF.Cos(rotationRadians);
			float sin = MathF.Sin(rotationRadians);
			Vector2 Rotate(Vector2 value) => new(
				center.X + value.X * cos - value.Y * sin,
				center.Y + value.X * sin + value.Y * cos);
			drawList.AddTriangleFilled(
				Rotate(p0),
				Rotate(p1),
				Rotate(p2),
				fillColor);
			drawList.AddTriangle(
				Rotate(p0),
				Rotate(p1),
				Rotate(p2),
				ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 1f)),
				1.0f);
		}

        private Coord.NavBitmap? _displayedMap;
        private int _displayedMapFloor;
        private readonly List<(Vector2 Start, Vector2 End)> _floorMapStrips = [];

        private unsafe void DrawRealWorldCenterMapPanel(InstanceContentDeepDungeon* dd)
        {
            if (FloorMapKey.For(dd) is not { } key)
            {
                ImGui.TextUnformatted("Boss 場地使用 BMR 機制地圖");
                return;
            }
            var template = FloorMap.Get(key);
            if (template == null)
            {
                ImGui.TextWrapped(FloorMap.Error);
                return;
            }
            if (_displayedMapFloor != dd->Floor || _displayedMap?.Matches(key, dd) != true)
            {
                _displayedMapFloor = dd->Floor;
                _displayedMap = Coord.NavBitmap.Create(template, dd);
                _floorMapStrips.Clear();
                int step = Math.Max(1, Math.Max(template.Width, template.Height) / 240);
                for (int y = 0; y < template.Height; y += step)
                {
                    int start = -1;
                    for (int x = 0; x < template.Width + step; x += step)
                    {
                        if (_displayedMap.IsFree(x, y))
                        {
                            if (start < 0) start = x;
                        }
                        else if (start >= 0)
                        {
                            _floorMapStrips.Add((new(start, y), new(Math.Min(x, template.Width), Math.Min(y + step, template.Height))));
                            start = -1;
                        }
                    }
                }
            }

            var origin = ImGui.GetCursorScreenPos();
            float scale = 240f * ImGuiHelpers.GlobalScale / Math.Max(template.Width, template.Height);
            var size = scale * new Vector2(template.Width, template.Height);
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(origin, origin + size, 0xff342919);
            foreach (var strip in _floorMapStrips)
                draw.AddRectFilled(origin + strip.Start * scale, origin + strip.End * scale, 0xffbcd37d);
            if (Service.LocalPlayer is { } player && _displayedMap.ContainsWorld(player.Position))
            {
                var (x, y) = _displayedMap.WorldToGrid(player.Position);
                draw.AddCircleFilled(origin + new Vector2(x + 0.5f, y + 0.5f) * scale, 3 * ImGuiHelpers.GlobalScale, 0xff5060ff);
            }
            ImGui.Dummy(size);
            ImGui.TextDisabled($"第 {dd->Floor} 層 · 封閉碰撞盒 {_displayedMap.ClosedBoxes}");
        }

        private static unsafe int GetLocalPlayerRoomIndex(InstanceContentDeepDungeon* deepDungeon)
        {
            return DeepDungeon.Fsd.Dalamud.GameState.RoomGraph.GetLocalPlayerRoomIndex(deepDungeon);
        }

        private static unsafe bool IsRoomReachable(InstanceContentDeepDungeon* deepDungeon, int fromRoom, int toRoom)
        {
            return DeepDungeon.Fsd.Dalamud.GameState.RoomGraph.IsRoomReachable(deepDungeon, fromRoom, toRoom);
        }
		private unsafe void BeginPassageFollowup(InstanceContentDeepDungeon* deepDungeon, int passageRoomIndex)
		{
			if (deepDungeon == null)
				return;

			_pendingPassageFollowup = true;
			_pendingPassageDungeonId = deepDungeon->DeepDungeonId;
			_pendingPassageFloor = deepDungeon->Floor;
			_pendingPassageRoomIndex = passageRoomIndex;
			_pendingPassageRetryAt = DateTime.UtcNow.AddMilliseconds(500);
			_pendingPassageTimeout = DateTime.UtcNow.AddSeconds(12);
		}

		private void ClearPendingPassageFollowup()
		{
			_pendingPassageFollowup = false;
			_pendingPassageRoomIndex = -1;
			_pendingPassageRetryAt = DateTime.MinValue;
			_pendingPassageTimeout = DateTime.MinValue;
		}

		private unsafe void ProcessPendingPassageFollowup()
		{
			if (!_pendingPassageFollowup)
				return;

			if (!_currentInDeepDungeon)
			{
				ClearPendingPassageFollowup();
				return;
			}

			var now = DateTime.UtcNow;
			if (_pendingPassageTimeout != DateTime.MinValue && now >= _pendingPassageTimeout)
			{
				ClearPendingPassageFollowup();
				return;
			}

			if (now < _pendingPassageRetryAt)
				return;

			var efw = EventFramework.Instance();
			var dd = efw != null ? efw->GetInstanceContentDeepDungeon() : null;
			if (dd == null || dd->DeepDungeonId != _pendingPassageDungeonId || dd->Floor != _pendingPassageFloor)
			{
				ClearPendingPassageFollowup();
				return;
			}

			_pendingPassageRetryAt = now.AddSeconds(0.75);

			if (!PassageLocator.TryResolveRequestedRoom(dd, _pendingPassageRoomIndex, out var dest, out bool usedActor))
				return;

			if (!usedActor)
				return;

			if (DeepDungeon.Fsd.Dalamud.moveHelper.VNav.SimpleMove.PathfindAndMoveTo(dest, false))
			{
				Service.Log.Info("[Necromancer] Passage已顯示，已更新導航至出口。");
				ClearPendingPassageFollowup();
			}
		}

        private static void TryInvokePalantirFetch()
        {
            try
            {
                // Direct call instead of reflection (obfuscation-safe)
                Map.PalantirData.OnEnterDeepDungeon();
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[FsdEngine] Palantir fetch failed: {ex}");
            }
        }

        private static (bool failed, bool hasLocal) GetPalantirStatus()
        {
            try
            {
                // Direct property access instead of reflection (obfuscation-safe)
                bool failed = Map.PalantirData.LastFetchFailedForCurrentTerritory;
                bool hasLocal = Map.PalantirData.HasMarkersForCurrentTerritory;
                return (failed, hasLocal);
            }
            catch
            {
                return (false, false);
            }
        }

        // ===== FullSelfDelving public controls (to be hooked from UI/commands) =====

        public void StartFullSelfDelvingPTChest()
        {
            try
            {
                var request = new FsdFarmingRequest(FarmingMode.Hoard, SaveUse.Create, 31, 1, false,
                    new FarmingTargets(_configuration.NecromancerAutoBandedFarmEnabled, _configuration.NecromancerAutoOpenGoldChest,
                        _configuration.NecromancerAutoOpenSilverChest, _configuration.NecromancerAutoOpenBronzeChest));
                if (TryStartFarming(request, out var error))
                    Service.Log.Info($"[Necromancer] FullSelfDelving started: PT chest (banded only)");
                else
                    Service.Log.Warning($"[Necromancer] FullSelfDelving start rejected: {error}");
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[Necromancer] StartFullSelfDelvingPTChest failed: {ex}");
            }
        }

        public void StopFullSelfDelving()
        {
            try
            {
                _ddHost?.StopFsd();
                
                // Dispose the host now that FSD is stopped (if not in dungeon)
                if (!_currentInDeepDungeon)
                {
                    try { _ddHost?.Dispose(); } catch { }
                    _ddHost = null;
                }
                
                Service.Log.Info("[Necromancer] FullSelfDelving stopped");
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[Necromancer] StopFullSelfDelving failed: {ex}");
            }
        }

        private static string GetPomanderDisplayName(uint pomanderId)
        {
            return pomanderId switch
            {
				1 => "安全",
				2 => "視野",
				3 => "強化自身",
				4 => "鋼鐵",
				5 => "寶箱增多",
				6 => "敵人減少",
				7 => "敵人變化",
				8 => "解咒",
				9 => "運氣",
				10 => "變形",
				11 => "解除",
				12 => "憤怒",
				13 => "夢魔",
				14 => "感知寶藏",
				15 => "重生",
				16 => "解除石化",
				17 => "弱化",
				18 => "隱身",
				19 => "石化",
				20 => "遲緩原型魔陶器",
				21 => "風暴原型魔陶器",
				22 => "恐懼原型魔陶器",
				23 => "安全原型魔陶器",
				24 => "視野原型魔陶器",
				25 => "強化自身原型魔陶器",
				26 => "鋼鐵原型魔陶器",
				27 => "寶箱增多原型魔陶器",
				28 => "敵人減少原型魔陶器",
				29 => "敵人變化原型魔陶器",
				30 => "解咒原型魔陶器",
				31 => "運氣原型魔陶器",
				32 => "變形原型魔陶器",
				33 => "解除原型魔陶器",
				34 => "感知寶藏原型魔陶器",
				35 => "重生原型魔陶器",
				36 => "敏捷",
				37 => "淨化",
				38 => "奉獻",
				_ => $"魔陶器 #{pomanderId}"
            };
        }

		private PomanderVisual[] GetPomanderVisuals(uint deepDungeonId)
		{
			if (deepDungeonId != 0 && _pomanderVisualCache.TryGetValue(deepDungeonId, out var cached) && cached.Length > 0)
				return cached;

			PomanderVisual[] visuals;
			try
			{
				if (deepDungeonId == 0)
					return CreateFallbackPomanderVisuals();

				var ddSheet = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.DeepDungeon>();
				var row = ddSheet?.GetRow(deepDungeonId);
				if (row != null)
				{
					var rowValue = row.Value;
					int count = rowValue.PomanderSlot.Count;
					visuals = new PomanderVisual[count];
					for (int i = 0; i < count; i++)
					{
						uint pomanderRowId = (uint)rowValue.PomanderSlot[i].RowId;
						string name = $"魔陶器槽位 {i + 1}";
						uint icon = 0;
						try
						{
							var itemRow = rowValue.PomanderSlot[i].ValueNullable;
							if (itemRow != null)
							{
								name = itemRow.Value.Name.ToString();
								icon = itemRow.Value.Icon;
							}
							else
							{
								var info = ItemManager.GetOrRegister(pomanderRowId);
								if (info.IsValid)
								{
									name = info.Name;
									icon = info.Icon;
								}
								else
								{
									name = GetPomanderDisplayName(pomanderRowId);
								}
							}
						}
						catch
						{
							name = GetPomanderDisplayName(pomanderRowId);
						}
						bool isValid = pomanderRowId != 0;
						visuals[i] = new PomanderVisual(pomanderRowId, name, icon, isValid);
					}
				}
				else
				{
					visuals = CreateFallbackPomanderVisuals();
				}
			}
			catch (ArgumentOutOfRangeException)
			{
				visuals = CreateFallbackPomanderVisuals();
			}
			catch (Exception ex)
			{
				Service.Log.Error($"[Necromancer] Failed to fetch pomander metadata: {ex}");
				visuals = CreateFallbackPomanderVisuals();
			}

			if (deepDungeonId != 0)
			{
				_pomanderVisualCache[deepDungeonId] = visuals;
			}

			return visuals;
		}

		private MagiciteVisual[] GetMagiciteVisuals(uint deepDungeonId)
		{
			if (deepDungeonId != 0 &&
				_magiciteVisualCache.TryGetValue(deepDungeonId, out MagiciteVisual[]? cached))
			{
				return cached;
			}

			MagiciteVisual[] visuals = Array.Empty<MagiciteVisual>();
			try
			{
				if (deepDungeonId == 0)
					return visuals;

				var ddSheet = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.DeepDungeon>();
				var row = ddSheet?.GetRow(deepDungeonId);
				if (row == null)
					throw new InvalidDataException($"DeepDungeon row {deepDungeonId} is unavailable.");

				// Native Magicite bytes are per-slot type IDs; MagiciteSlot definitions are indexed by type ID minus one.
				byte deepDungeonType = row.Value.DeepDungeonType;
				var slots = row.Value.MagiciteSlot;
				visuals = new MagiciteVisual[slots.Count];
				var stoneSheet = Service.DataManager
					.GetExcelSheet<Lumina.Excel.Sheets.DeepDungeonMagicStone>();
				var demicloneSheet = Service.DataManager
					.GetExcelSheet<Lumina.Excel.Sheets.DeepDungeonDemiclone>();

				for (int definitionIndex = 0; definitionIndex < slots.Count; definitionIndex++)
				{
					var slot = slots[definitionIndex];
					uint rowId = slot.RowId;
					if (rowId == 0)
					{
						visuals[definitionIndex] = MagiciteVisual.Unresolved(
							definitionIndex,
							rowId,
							$"DeepDungeon.MagiciteSlot definition {definitionIndex + 1} is empty.");
						continue;
					}

					if (!MagiciteSlotMapping.TryGetRowKind(
							deepDungeonType,
							definitionIndex,
							out MagiciteRowKind rowKind))
					{
						string error =
							$"DeepDungeon type {deepDungeonType} has no established magicite row kind for definition {definitionIndex + 1}.";
						visuals[definitionIndex] = MagiciteVisual.Unresolved(
							definitionIndex,
							rowId,
							error);
						Service.Log.Warning(
							$"[Necromancer] Cannot resolve {error} RowId {rowId}.");
						continue;
					}

					switch (rowKind)
					{
						case MagiciteRowKind.MagicStone:
						{
							var stoneRow = stoneSheet?.GetRow(rowId);
							if (stoneRow == null || stoneRow.Value.RowId != rowId)
							{
								visuals[definitionIndex] = MagiciteVisual.Unresolved(
									definitionIndex,
									rowId,
									$"DeepDungeonMagicStone row {rowId} is unavailable.");
								continue;
							}

							string name = stoneRow.Value.Name.ToString();
							uint iconId = stoneRow.Value.Icon;
							visuals[definitionIndex] = string.IsNullOrWhiteSpace(name) || iconId == 0
								? MagiciteVisual.Unresolved(definitionIndex, rowId, $"DeepDungeonMagicStone row {rowId} has no official name/icon.")
								: new MagiciteVisual(definitionIndex, rowId, name, iconId, true, string.Empty);
							continue;
						}

						case MagiciteRowKind.Demiclone:
						{
							var demicloneRow = demicloneSheet?.GetRow(rowId);
							if (demicloneRow == null || demicloneRow.Value.RowId != rowId)
							{
								visuals[definitionIndex] = MagiciteVisual.Unresolved(
									definitionIndex,
									rowId,
									$"DeepDungeonDemiclone row {rowId} is unavailable.");
								continue;
							}

							string name = demicloneRow.Value.TitleCase.ToString();
							uint iconId = demicloneRow.Value.Icon;
							visuals[definitionIndex] = string.IsNullOrWhiteSpace(name) || iconId == 0
								? MagiciteVisual.Unresolved(definitionIndex, rowId, $"DeepDungeonDemiclone row {rowId} has no official name/icon.")
								: new MagiciteVisual(definitionIndex, rowId, name, iconId, true, string.Empty);
							continue;
						}
					}
				}
			}
			catch (Exception ex)
			{
				Service.Log.Error($"[Necromancer] Failed to fetch magicite metadata: {ex}");
			}

			if (deepDungeonId != 0)
				_magiciteVisualCache[deepDungeonId] = visuals;
			return visuals;
		}

		private static PomanderVisual[] CreateFallbackPomanderVisuals()
		{
			var visuals = new PomanderVisual[16];
			for (int i = 0; i < visuals.Length; i++)
			{
				visuals[i] = new PomanderVisual((uint)(i + 1), $"魔陶器槽位 {i + 1}", 0, true);
			}
			return visuals;
		}

		private static string BuildPomanderAbbreviation(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return "?";

			name = name.Trim();
			if (name.Length <= 2)
				return name;

			var parts = name.Split(new[] { ' ', '　', '/', '／', '-' }, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0)
				return name.Substring(0, Math.Min(2, name.Length));

			if (parts.Length == 1)
			{
				var token = parts[0];
				return token.Length <= 2 ? token : token.Substring(0, Math.Min(2, token.Length));
			}

			var buffer = new char[Math.Min(2, parts.Length)];
			int idx = 0;
			foreach (var part in parts)
			{
				if (string.IsNullOrEmpty(part))
					continue;
				buffer[idx++] = part[0];
				if (idx >= buffer.Length)
					break;
			}

			if (idx == 0)
				return name.Substring(0, Math.Min(2, name.Length));

			return new string(buffer, 0, idx);
		}

		private ISharedImmediateTexture? GetIconTexture(uint iconId)
		{
			if (iconId == 0 || Service.TextureProvider == null)
				return null;

			if (_iconTextureCache.TryGetValue(iconId, out var cached))
				return cached;

			try
			{
				var texture = Service.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId, hiRes: true));
				_iconTextureCache[iconId] = texture;
				return texture;
			}
			catch (Exception ex)
			{
				Service.Log.Debug($"[Necromancer] 無法載入圖示 {iconId}: {ex.Message}");
				_iconTextureCache[iconId] = null;
				return null;
			}
		}

		private sealed record PomanderVisual(uint PomanderId, string Name, uint IconId, bool IsValid);
		private sealed record MagiciteVisual(
			int SlotIndex,
			uint RowId,
			string Name,
			uint IconId,
			bool IsResolved,
			string ResolutionError)
		{
			public static MagiciteVisual Unresolved(int slotIndex, uint rowId, string resolutionError) =>
				new(slotIndex, rowId, string.Empty, 0, false, resolutionError);
		}
    }
}
