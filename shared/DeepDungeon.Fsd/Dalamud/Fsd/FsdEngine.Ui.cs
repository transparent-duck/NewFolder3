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
                if (!ImGui.CollapsingHeader("Debug panel", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    return;
                }

                ImGui.Indent();

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
                        string displayName = $"Pomander Slot {i}";
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
                            deepDungeon->UsePomander((uint)i);
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
                            deepDungeon->UseStone((uint)i);
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
                            if (ImGui.CollapsingHeader("AutoPilot 調試 (Sequential)", ImGuiTreeNodeFlags.None))
                            {
                                var player = Service.LocalPlayer;
                                var pos = player?.Position ?? default;
                                
                                // State and status
                                ImGui.Text($"Phase: {dbg.Phase} | Task: {dbg.TaskPhase}");
                                ImGui.Text($"Status: {dbg.Status}");
                                ImGui.Text($"Hoard Count: {dbg.HoardCount}/5");
                                ImGui.Separator();
                                
                                // Room path visualization
                                if (dbg.RoomPath != null && dbg.RoomPath.Count > 0)
                                {
                                    ImGui.TextDisabled("Room Visit Order:");
                                ImGui.Indent();
                                    for (int i = 0; i < dbg.RoomPath.Count; i++)
                                    {
                                        int room = dbg.RoomPath[i];
                                        bool isCompleted = dbg.CompletedRooms.Contains(room);
                                        bool isCurrent = i == dbg.CurrentRoomIdx;
                                        
                                        if (isCompleted)
                                            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), $"✓ Room {room}");
                                        else if (isCurrent)
                                            ImGui.TextColored(new Vector4(0.6f, 1.0f, 0.6f, 1f), $"→ Room {room} (current)");
                                        else
                                            ImGui.Text($"  Room {room}");
                                    }
                                    ImGui.Unindent();
                                }
                                else
                                {
                                    ImGui.TextDisabled("(No room path yet)");
                                }
                                
                                ImGui.Separator();
                                int completedCount = dbg.CompletedRooms != null ? dbg.CompletedRooms.Count : 0;
                                int totalRooms = dbg.RoomPath != null ? dbg.RoomPath.Count : 0;
                                ImGui.Text($"Progress: {completedCount}/{totalRooms} rooms completed");
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
					// Scenario selector
					string[] scenarios = _detailedMapHostOptions.SupportsControlledPtSurvey
						? new[]
						{
							"Pilgrim's Traverse 21-30 (better)",
							"Pilgrim's Traverse 31-40",
							"PT 21-30 controlled capture (reusable save)"
						}
						: new[]
						{
							"Pilgrim's Traverse 21-30 (better)",
							"Pilgrim's Traverse 31-40"
						};
				int idx = Math.Clamp(_fsfScenarioIndex, 0, scenarios.Length - 1);
				ImGui.SetNextItemWidth(260f);
				bool hostAssistActive = _ddHost?.AssistModeActive == true;
				ImGui.BeginDisabled(hostAssistActive);
					if (ImGui.Combo("Scenario##fsf_scn", ref idx, scenarios, scenarios.Length))
					{
						_fsfScenarioIndex = idx;
						_configuration.NecromancerFsdScenarioIndex = idx;
						_configuration.Save();
					}
					ImGui.EndDisabled();

				DrawDetailedMapSettings(hostAssistActive);

				// ===== Controls (moved under scenario selection) =====
				{
					bool fsdRunning = _ddHost?.FsdActive == true;

					ImGui.Spacing();
					ImGui.Indent();
					ImGui.BeginDisabled(fsdRunning || inDD || _bridgeLeaveDutyContext != null || _bridgeDeleteSaveContext != null || _bridgeDeleteSaveFlow != null);
					if (ImGui.Button("FSD Start##fsf_start_top"))
					{
						try
						{
							var controlledSession = _fsfScenarioIndex == 2
								? new ControlledPtSurveySession()
								: null;
							Func<IScenario> factory = _fsfScenarioIndex switch
							{
								0 => () => new PTChestScenario(21),
								1 => () => new PTChestScenario(31),
								2 => () => new ControlledPt21To30Scenario(controlledSession!),
								_ => () => new PTChestScenario()
							};
							if (!TryStartOutsideDutyFsd(
								    factory,
								    Math.Max(1, _fsfLoopCount),
								    _fsfLoopInfinite,
								    DetailedMapCatalogManager.GetScenarioKey(_fsfScenarioIndex),
								    out var error))
								Service.Log.Warning($"[Necromancer] FSF start rejected: {error}");
						}
						catch (Exception ex)
						{
							Service.Log.Error($"[Necromancer] FSF start failed: {ex}");
						}
					}
					ImGui.EndDisabled();
					ImGui.SameLine();
					ImGui.BeginDisabled(!fsdRunning);
					if (ImGui.Button("Stop##fsf_stop_top"))
					{
						StopFullSelfDelving();
					}
					ImGui.EndDisabled();
					ImGui.Unindent();
				}

				// Runtime/default options (always visible)
				try
				{
					var provider = _ddHost?.RunOptionsProvider;
					ImGui.Separator();
						ImGui.Text("Full Self Farming Options");
						ImGui.Indent();
						// Source values: provider if running, otherwise configuration defaults
						bool controlledCapture = _detailedMapHostOptions.SupportsControlledPtSurvey &&
							(_fsfScenarioIndex == 2 ||
							 string.Equals(_ddHost?.CurrentScenarioName, "PT 21-30 controlled capture", StringComparison.Ordinal));
						ImGui.BeginDisabled(controlledCapture);
						bool banded = provider != null ? provider.Current.BandedEnabled : _configuration.NecromancerAutoBandedFarmEnabled;
						if (ImGui.Checkbox("Banded (search+open)", ref banded))
						{
							_configuration.NecromancerAutoBandedFarmEnabled = banded; _configuration.Save();
							if (provider != null) provider.Update(o => o.BandedEnabled = banded);
						}
						bool og = provider != null ? provider.Current.OpenGold : _configuration.NecromancerAutoOpenGoldChest;
						if (ImGui.Checkbox("Open Gold", ref og))
						{
							_configuration.NecromancerAutoOpenGoldChest = og; _configuration.Save();
							if (provider != null) provider.Update(o => o.OpenGold = og);
						}
						bool os = provider != null ? provider.Current.OpenSilver : _configuration.NecromancerAutoOpenSilverChest;
						if (ImGui.Checkbox("Open Silver (HP>85%)", ref os))
						{
							_configuration.NecromancerAutoOpenSilverChest = os; _configuration.Save();
							if (provider != null) provider.Update(o => o.OpenSilver = os);
						}
						bool ob = provider != null ? provider.Current.OpenBronze : _configuration.NecromancerAutoOpenBronzeChest;
						if (ImGui.Checkbox("Open Bronze", ref ob))
						{
							_configuration.NecromancerAutoOpenBronzeChest = ob; _configuration.Save();
							if (provider != null) provider.Update(o => o.OpenBronze = ob);
						}

						ImGui.Separator();
						ImGui.Text("退出深宮...");
						// UI indices (modeIdx): 0=完成任務後, 1=獲取1個寶藏後, 2=立即(debug)
						string[] leaveModes = new[] { "完成任務後", "獲取1個寶藏後", "立即(debug)" };
						int currentModeIdx = provider != null ? LeaveModeUiMapping.ToUiIndex(provider.Current.LeaveMode) : Math.Clamp(_configuration.NecromancerAutoLeaveMode, 0, 4);
						leaveModes = new[] { "After finish dungeon", "After getting 1 hoard", "Immediate (debug)", "On boss floor entry", "After N minutes" };
						ImGui.SetNextItemWidth(180f);
						if (ImGui.Combo("##leaveMode", ref currentModeIdx, leaveModes, leaveModes.Length))
						{
							_configuration.NecromancerAutoLeaveMode = currentModeIdx; _configuration.Save();
							if (provider != null) provider.Update(o => o.LeaveMode = LeaveModeUiMapping.FromUiIndex(currentModeIdx));
						}
						ImGui.EndDisabled();
						if (LeaveModeUiMapping.FromUiIndex(currentModeIdx) == LeaveMode.AfterNMinutes)
						{
							int leaveAfterMinutes = provider != null ? provider.Current.LeaveAfterMinutes : _configuration.NecromancerAutoLeaveAfterMinutes;
							if (ImGui.SliderInt("Leave after minutes##leaveAfterMinutes", ref leaveAfterMinutes, 1, 180))
							{
								leaveAfterMinutes = Math.Clamp(leaveAfterMinutes, 1, 180);
								_configuration.NecromancerAutoLeaveAfterMinutes = leaveAfterMinutes; _configuration.Save();
								if (provider != null) provider.Update(o => o.LeaveAfterMinutes = leaveAfterMinutes);
							}
						}
						// ==== FSD end mode & targets + debug item count tester ====
						ImGui.Spacing();
						ImGui.Text("結束 FSD...");
						// TODO: Deprecated item-count end modes; FSD should only stop by loop count.
						if (_configuration.NecromancerFsdEndMode != (int)FsdEndMode.Loops)
						{
							_configuration.NecromancerFsdEndMode = (int)FsdEndMode.Loops;
							_configuration.Save();
						}
						int endMode = (int)FsdEndMode.Loops;

						// Loops mode
						if (endMode == 0)
						{
							bool infinite = _configuration.NecromancerFsdLoopInfinite;
							if (ImGui.Checkbox("Infinite Loop##fsf_inf", ref infinite))
							{
								_configuration.NecromancerFsdLoopInfinite = infinite;
								_configuration.Save();
								_fsfLoopInfinite = infinite;
							}
							ImGui.SameLine();
							ImGui.BeginDisabled(infinite);
							int loops = Math.Max(1, _configuration.NecromancerFsdLoopCount);
							ImGui.SetNextItemWidth(90f);
							if (ImGui.InputInt("Loops##fsf_loops", ref loops))
							{
								loops = Math.Max(1, loops);
								_configuration.NecromancerFsdLoopCount = loops;
								_configuration.Save();
								_fsfLoopCount = loops;
							}
							ImGui.EndDisabled();
						}
						else
						{
							// Potsherd / Hoard modes show dungeon-specific targets
							ImGui.Spacing();
							uint ddId = 0;
							try
							{
								var efw = EventFramework.Instance();
								var dd = efw != null ? efw->GetInstanceContentDeepDungeon() : null;
								ddId = dd != null ? dd->DeepDungeonId : 0u;
							}
							catch { }

							// if not in duty, infer from selected scenario
							if (ddId == 0)
							{
								ddId = 4u;
							}

							if (endMode == 1)
							{
								// Potsherd targets
								uint itemId = ddId switch
								{
									1u => DeepDungeonItems.PotdPotsherd,
									2u => DeepDungeonItems.HohPotsherd,
									3u => DeepDungeonItems.EoPotsherd,
									4u => DeepDungeonItems.PtPotsherd,
									_ => 0u
								};

								string label = "Potsherd";
								if (itemId != 0)
								{
									try
									{
										var info = ItemManager.GetOrRegister(itemId);
										if (info.IsValid && !string.IsNullOrEmpty(info.Name))
											label = info.Name;
									}
									catch { }
								}

								int current = itemId != 0 ? DeepDungeonLootTracker.GetItemCount(itemId) : 0;
								int target = ddId switch
								{
									1u => _configuration.NecromancerFsdPotdPotsherdTarget,
									2u => _configuration.NecromancerFsdHoHPotsherdTarget,
									3u => _configuration.NecromancerFsdEOPotsherdTarget,
									4u => _configuration.NecromancerFsdPTPotsherdTarget,
									_ => 0
								};

								ImGui.Text($"{label}: {current} /");
								ImGui.SameLine();
								ImGui.SetNextItemWidth(80f);
								if (ImGui.InputInt("##potsherd_target", ref target))
								{
									target = Math.Max(0, target);
									switch (ddId)
									{
										case 1u: _configuration.NecromancerFsdPotdPotsherdTarget = target; break;
										case 2u: _configuration.NecromancerFsdHoHPotsherdTarget = target; break;
										case 3u: _configuration.NecromancerFsdEOPotsherdTarget = target; break;
										case 4u: _configuration.NecromancerFsdPTPotsherdTarget = target; break;
									}
								}
							}
							else if (endMode == 2)
							{
								// Hoard targets per dungeon
								void DrawHoardRow(uint itemId, ref int target, string fallbackName)
								{
								string name = fallbackName;
									try
									{
										var sheet = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
										if (sheet != null)
										{
											var row = sheet.GetRow(itemId);
											if (row.RowId == itemId)
												name = row.Name.ToString();
										}
									}
									catch { }
									int current = DeepDungeonLootTracker.GetItemCount(itemId);
									ImGui.Text($"{name}: {current} /");
									ImGui.SameLine();
									ImGui.SetNextItemWidth(80f);
									if (ImGui.InputInt($"##hoard_{itemId}", ref target))
									{
										target = Math.Max(0, target);
									}
								}

								switch (ddId)
								{
									case 1u: // POTD
										{
											int t1 = _configuration.NecromancerFsdPotdHoard16170Target;
											int t2 = _configuration.NecromancerFsdPotdHoard16171Target;
											int t3 = _configuration.NecromancerFsdPotdHoard16172Target;
											int t4 = _configuration.NecromancerFsdPotdHoard16173Target;
											DrawHoardRow(16170u, ref t1, "BuriedTreasureG1");
											DrawHoardRow(16171u, ref t2, "BuriedTreasureG2");
											DrawHoardRow(16172u, ref t3, "BuriedTreasureG3");
											DrawHoardRow(16173u, ref t4, "BuriedTreasureG4");
											_configuration.NecromancerFsdPotdHoard16170Target = t1;
											_configuration.NecromancerFsdPotdHoard16171Target = t2;
											_configuration.NecromancerFsdPotdHoard16172Target = t3;
											_configuration.NecromancerFsdPotdHoard16173Target = t4;
											_configuration.Save();
										}
										break;
									case 2u: // HoH
										{
											int t1 = _configuration.NecromancerFsdHoHHoard23223Target;
											int t2 = _configuration.NecromancerFsdHoHHoard23224Target;
											int t3 = _configuration.NecromancerFsdHoHHoard23225Target;
											DrawHoardRow(23223u, ref t1, "BuriedTreasureH1");
											DrawHoardRow(23224u, ref t2, "BuriedTreasureH2");
											DrawHoardRow(23225u, ref t3, "BuriedTreasureH3");
											_configuration.NecromancerFsdHoHHoard23223Target = t1;
											_configuration.NecromancerFsdHoHHoard23224Target = t2;
											_configuration.NecromancerFsdHoHHoard23225Target = t3;
											_configuration.Save();
										}
										break;
									case 3u: // EO
										{
											int t1 = _configuration.NecromancerFsdEOHoard38945Target;
											int t2 = _configuration.NecromancerFsdEOHoard38946Target;
											int t3 = _configuration.NecromancerFsdEOHoard38947Target;
											DrawHoardRow(38945u, ref t1, "BuriedTreasureI");
											DrawHoardRow(38946u, ref t2, "BuriedTreasureII");
											DrawHoardRow(38947u, ref t3, "BuriedTreasureIII");
											_configuration.NecromancerFsdEOHoard38945Target = t1;
											_configuration.NecromancerFsdEOHoard38946Target = t2;
											_configuration.NecromancerFsdEOHoard38947Target = t3;
											_configuration.Save();
										}
										break;
									case 4u: // PT
										{
											int t1 = _configuration.NecromancerFsdPTHoard47104Target;
											int t2 = _configuration.NecromancerFsdPTHoard47105Target;
											int t3 = _configuration.NecromancerFsdPTHoard47106Target;
											DrawHoardRow(47104u, ref t1, "BuriedTreasureL1");
											DrawHoardRow(47105u, ref t2, "BuriedTreasureL2");
											DrawHoardRow(47106u, ref t3, "BuriedTreasureL3");
											_configuration.NecromancerFsdPTHoard47104Target = t1;
											_configuration.NecromancerFsdPTHoard47105Target = t2;
											_configuration.NecromancerFsdPTHoard47106Target = t3;
											_configuration.Save();
										}
										break;
								}
							}
						}


						ImGui.Unindent();
					}
					catch { }

					// Battle assist (Banded chest farming settings)
					ImGui.Separator();
					ImGui.Text("Battle assist");
					UiHelpers.DrawGrayTipText("如果你的自動輸出插件不會自動選中或主動攻擊");
					// Scan radius and stand seconds are hardcoded (30m, ~3s) and not configurable
					var autoSel = _configuration.NecromancerBandedAutoSelect;
					if (ImGui.Checkbox("自動選中", ref autoSel))
					{
						_configuration.NecromancerBandedAutoSelect = autoSel;
						_configuration.Save();
					}
					ImGui.SameLine();
					var autoAtt = _configuration.NecromancerBandedAutoAttract;
					if (ImGui.Checkbox("主動進戰", ref autoAtt))
					{
						_configuration.NecromancerBandedAutoAttract = autoAtt;
						_configuration.Save();
					}

					int sid = (int)_configuration.NecromancerBandedAttractSkillId;
					ImGui.Text("拉怪技能ID");
					ImGui.SameLine();
					ImGui.SetNextItemWidth(90f);
					if (ImGui.InputInt("##banded_atk_skill_fsf", ref sid))
					{
						_configuration.NecromancerBandedAttractSkillId = (uint)Math.Max(0, sid);
						_configuration.Save();
					}
					ImGui.SameLine();
					{
						var info = FsdSkillCatalog.GetOrRegister(_configuration.NecromancerBandedAttractSkillId);
						var nm = info.IsValid ? info.Name : "未設定/未知";
						ImGui.TextDisabled(nm);
						// When skill metadata is unavailable, fall back to a conservative fixed range (5m).
						float computed = info.IsValid ? info.Range : 5f;
						ImGui.SameLine();
						ImGui.Text($"選中範圍: {computed:F1} m");
					}

				// FSD controls moved under scenario selection

					// Status now displayed under minimap via DrawFsdStatusBlock
				ImGui.Unindent();

				// World overlay: in-room waypoints (always shown when assist is active)
				try
				{
					bool assistActive = _ddHost?.AssistModeActive == true;
					if (assistActive && deepDungeon != null)
					{
						var dbg = _ddHost?.FloorController?.GetDebugSnapshot();
						if (dbg?.RoomContext != null && dbg.RoomContext.Waypoints.Count > 0)
						{
							DrawRoomWaypointDebugOverlay(deepDungeon, dbg.RoomContext);
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
			ImGui.Separator();
		}

		private void DrawDetailedMapSettings(bool runActive)
		{
			string? scenarioKey =
				DetailedMapCatalogManager.GetScenarioKey(_fsfScenarioIndex);
			bool supported = scenarioKey != null;
			bool serviceConfigured = _detailedMapHostOptions.HasOnlineCatalogService;
			bool enabled = supported && serviceConfigured && _configuration.UseDetailedMap;

			ImGui.Spacing();
			ImGui.BeginDisabled(!supported || !serviceConfigured);
			if (ImGui.Checkbox("Use detailed map##fsdDetailedMap", ref enabled))
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
						"No detailed-map data version is published for the selected scenario.");
				}
				ImGui.PopTextWrapPos();
				ImGui.EndTooltip();
			}

			if (!serviceConfigured)
			{
				ImGui.TextWrapped(DetailedMapHostOptions.NoOnlineCatalogServiceMessage);
				return;
			}

			if (!supported || !_configuration.UseDetailedMap)
				return;

			DetailedMapCatalogStatusSnapshot status =
				_detailedMapCatalogManager.GetStatus(
					enabled: true,
					scenarioKey);
			ImGui.TextDisabled(
				status.ReleaseId == null
					? "數據版本: unavailable"
					: $"數據版本: {status.ReleaseId}");
			if (status.CandidateCount > 0)
			{
				double coverage =
					(double)status.KnownSuccessorCount /
					status.CandidateCount *
					100d;
				ImGui.TextDisabled(
					$"詳細寶藏數據: {status.KnownSuccessorCount}/{status.CandidateCount} ({coverage:F1}%)");
			}
			if (!status.HasValidCatalog ||
			    status.Checking ||
			    status.Message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
			    status.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase))
			{
				ImGui.TextWrapped(status.Message);
			}
		}

		public void DrawGeneralAssistantSettings()
		{
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
			ImGui.Separator();
		}
		
		private void DrawDeepDungeonOverlayToggles()
		{
			ImGui.Spacing();
			if (!ImGui.CollapsingHeader("Overlay Controls", ImGuiTreeNodeFlags.DefaultOpen))
				return;

			ImGui.Indent();
			DrawOverlayToggleCheckbox(
				"Room centers (world)",
				_configuration.NecromancerShowRoomCenterOverlay,
				v => _configuration.NecromancerShowRoomCenterOverlay = v,
				"Disable green crosses for discovered centers in the 3D world.");

			DrawOverlayToggleCheckbox(
				"Traps (world)",
				_configuration.NecromancerShowTrapOverlay,
				v => _configuration.NecromancerShowTrapOverlay = v,
				"Disable PalacePal trap markers in the world overlay.");

			DrawOverlayToggleCheckbox(
				"AutoPilot room path (world)",
				_configuration.NecromancerShowRoomPathOverlay,
				v => _configuration.NecromancerShowRoomPathOverlay = v,
				"Disable Sequential AutoPilot path lines and room indicators.");

			DrawOverlayToggleCheckbox(
				"BG collision debug (world)",
				_configuration.NecromancerShowBgCollisionOverlay,
				v => _configuration.NecromancerShowBgCollisionOverlay = v,
				"Suppress background collision debug primitives even when enabled.");

			ImGui.Unindent();
		}

		private void DrawOverlayToggleCheckbox(string label, bool currentValue, Action<bool> setter, string hint)
		{
			var value = currentValue;
			if (ImGui.Checkbox(label, ref value))
			{
				setter(value);
				_configuration.Save();
			}

			if (!string.IsNullOrEmpty(hint))
			{
				UiHelpers.DrawGrayTipText(hint);
			}
		}

		private void DrawAxisHints(RoomCenterGenerator.DebugSnapshot snapshot, float yLevel, uint color)
		{
			if (snapshot == null)
				return;
			if (!snapshot.PlayerRoomCenter.HasValue)
				return;

			var basePos2 = snapshot.PlayerRoomCenter.Value;
			var basePos3 = new Vector3(basePos2.X, yLevel, basePos2.Y);

			float columnStep = EstimateAxisStep(snapshot.ColumnCoords);
			if (columnStep > 0.1f && snapshot.ColumnBasis.LengthSquared() > 1e-4f)
			{
				var dir = Vector2.Normalize(snapshot.ColumnBasis) * columnStep;
				var dest = basePos3 + new Vector3(dir.X, 0f, dir.Y);
				DrawLabelledArrow(basePos3, dest, color, "+Col");
			}

			float rowStep = EstimateAxisStep(snapshot.RowCoords);
			if (rowStep > 0.1f && snapshot.RowBasis.LengthSquared() > 1e-4f)
			{
				var dir = Vector2.Normalize(snapshot.RowBasis) * rowStep;
				var dest = basePos3 + new Vector3(dir.X, 0f, dir.Y);
				DrawLabelledArrow(basePos3, dest, color, "+Row");
			}
		}

		private static float EstimateAxisStep(float[]? coords)
		{
			if (coords == null || coords.Length < 2)
				return 0f;
			float sum = 0f;
			int count = 0;
			for (int i = 0; i < coords.Length - 1; i++)
			{
				float diff = MathF.Abs(coords[i + 1] - coords[i]);
				if (diff > 1e-3f)
				{
					sum += diff;
					count++;
				}
			}
			return count > 0 ? sum / count : 0f;
		}

		private void DrawLabelledArrow(Vector3 from, Vector3 to, uint color, string label)
		{
			WorldDrawHelper.DrawWorldLine(from, to, color);
			var mid = (from + to) * 0.5f + new Vector3(0f, 0.25f, 0f);
			WorldDrawHelper.DrawWorldText(mid, color, label);
		}

		// Back-compat wrapper (debug panel expects actions/overlays)
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
			List<int>? assistPlannedRooms = null;
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
						ImGui.SetTooltip("No room center available for this floorset/tileset.");
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
							if (TryGetPassageDestination(deepDungeon, out var passageDest, out bool usedActor, out var passageRoomIndex))
							{
								if (DeepDungeon.Fsd.Dalamud.moveHelper.VNav.SimpleMove.PathfindAndMoveTo(passageDest, false))
								{
									if (usedActor)
									{
										ClearPendingPassageFollowup();
									}
									else
									{
										BeginPassageFollowup(deepDungeon, passageRoomIndex >= 0 ? passageRoomIndex : i);
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
            if (showActions)
            {
	            DrawRoomCenterControlsUnderMap(deepDungeon, playerRoom);
            }
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
				// Warnings below map: PalacePal fetch status
				var (ppFailed, ppHasLocal) = GetPalacePalStatus();
				if (ppFailed)
				{
					if (ppHasLocal)
						ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "警告：無法從 PalacePal 取得資料，使用本機快取標記。");
					else
						ImGui.TextColored(new Vector4(1.0f, 0.35f, 0.35f, 1.0f), "警告：無法從 PalacePal 取得資料，且本機無標記資料。");
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
							if (DeepDungeon.Fsd.Dalamud.Map.MapPosGeneration.TryGetRuntimeRoomCenter(deepDungeon, i, out var where))
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
							var traps = Map.PalacePalData.GetTrapPositionsCurrentTerritory();
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
							var snapshot = RoomCenterGenerator.GetDebugSnapshot();
							if (snapshot != null)
							{
								DrawRoomCenterDebugOverlay(deepDungeon, snapshot);
							}
						}
						catch (Exception ex)
						{
							try { Service.Log.Error($"[Necromancer] Room center debug overlay error: {ex}"); } catch { }
						}

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

		private unsafe void DrawRoomWaypointDebugOverlay(
			InstanceContentDeepDungeon* deepDungeon,
			RoomContextSnapshot snapshot)
		{
			if (deepDungeon == null || snapshot == null)
				return;
			var waypoints = snapshot.Waypoints;
			if (waypoints == null || waypoints.Count == 0)
				return;

			int total = waypoints.Count;
			int currentIdx = Math.Clamp(snapshot.CurrentWaypointIndex, 0, total);

			uint colSegmentCompleted = ImGui.GetColorU32(new Vector4(0.35f, 0.75f, 0.35f, 0.85f));
			uint colSegmentActive = ImGui.GetColorU32(new Vector4(0.2f, 0.85f, 1.0f, 0.95f));
			uint colSegmentPending = ImGui.GetColorU32(new Vector4(1.0f, 0.78f, 0.35f, 0.75f));

			Vector3? prev = null;

			for (int i = 0; i < total; i++)
			{
				var wp = waypoints[i];
				var pos = wp.Position;
				if (float.IsNaN(pos.X) || float.IsNaN(pos.Y) || float.IsNaN(pos.Z) ||
				    float.IsInfinity(pos.X) || float.IsInfinity(pos.Y) || float.IsInfinity(pos.Z))
				{
					continue;
				}

			

				if (prev.HasValue)
				{
					uint segCol = i < currentIdx
						? colSegmentCompleted
						: (i == currentIdx ? colSegmentActive : colSegmentPending);
					WorldDrawHelper.DrawWorldLine(prev.Value, pos, segCol);
				}

				bool isCompleted = i < currentIdx;
				bool isCurrent = i == currentIdx;

				uint labelColor = ImGui.GetColorU32(isCompleted
					? new Vector4(0.85f, 0.9f, 0.85f, 0.55f)
					: (isCurrent ? new Vector4(1.0f, 1.0f, 1.0f, 1.0f) : new Vector4(1.0f, 1.0f, 1.0f, 0.8f)));
				WorldDrawHelper.DrawWorldText(pos + new Vector3(0f, 0.35f, 0f), labelColor, $"{i + 1}");

				prev = pos;
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
								deepDungeon->UsePomander((uint)slotIndex);
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
							deepDungeon->UseStone((uint)i);
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

		private unsafe void DrawRealWorldCenterMapPanel(InstanceContentDeepDungeon* deepDungeon)
		{
			float uiScale = ImGuiHelpers.GlobalScale;
			const float baseSide = 240f;
			Vector2 mapSize = new(baseSide * uiScale, baseSide * uiScale);
			float padding = 12f * uiScale;

			var drawList = ImGui.GetWindowDrawList();
			ImGui.InvisibleButton("##dd_real_world_map", mapSize);
			var rectMin = ImGui.GetItemRectMin();
			var rectMax = ImGui.GetItemRectMax();

			uint bgCol = ImGui.GetColorU32(new Vector4(0.07f, 0.08f, 0.12f, 0.97f));
			uint borderCol = ImGui.GetColorU32(new Vector4(0.68f, 0.78f, 0.98f, 1.0f));
			drawList.AddRectFilled(rectMin, rectMax, bgCol, 6f);
			drawList.AddRect(rectMin, rectMax, borderCol, 6f);

			void DrawPlaceholder(string text)
			{
				var textSize = ImGui.CalcTextSize(text);
				var pos = rectMin + (mapSize - textSize) * 0.5f;
				drawList.AddText(pos, ImGui.GetColorU32(new Vector4(0.8f, 0.82f, 0.86f, 0.9f)), text);
			}

			bool IsFiniteScalar(float value) => !(float.IsNaN(value) || float.IsInfinity(value));
			bool IsFiniteVec2(Vector2 value) => IsFiniteScalar(value.X) && IsFiniteScalar(value.Y);
			bool IsFiniteVec3(Vector3 value) => IsFiniteScalar(value.X) && IsFiniteScalar(value.Z);

			var snapshot = RoomCenterGenerator.GetDebugSnapshot();
			if (snapshot == null && deepDungeon != null)
			{
				RoomCenterGenerator.TryGenerate(deepDungeon, out _, out _, out _);
				snapshot = RoomCenterGenerator.GetDebugSnapshot();
			}
			if (snapshot == null)
			{
				DrawPlaceholder("Room center generator data unavailable.");
				return;
			}

			var activeWalls = snapshot.ActiveLayoutWalls;
			var rawWalls = snapshot.RawRespawnWalls;
			var currentWalls = activeWalls.Count > 0 ? activeWalls : rawWalls;
			var realPoints = new List<Vector2>(currentWalls.Count);
			for (int i = 0; i < currentWalls.Count; i++)
			{
				var v = currentWalls[i];
				if (!IsFiniteVec3(v))
					continue;
				realPoints.Add(new Vector2(v.X, v.Z));
			}
			bool usingActiveWalls = activeWalls.Count > 0 && currentWalls == activeWalls;

			var predictedPoints = new List<(Vector2 pos, int index)>();
			if (snapshot.PredictedCenters != null)
			{
				for (int i = 0; i < snapshot.PredictedCenters.Length; i++)
				{
					var value = snapshot.PredictedCenters[i];
					if (value.HasValue && IsFiniteVec2(value.Value))
					{
						predictedPoints.Add((value.Value, i));
					}
				}
			}

			Vector2? playerAnchor = null;
			if (snapshot.PlayerRoomCenter.HasValue && IsFiniteVec2(snapshot.PlayerRoomCenter.Value))
			{
				playerAnchor = snapshot.PlayerRoomCenter.Value;
			}
			else if (IsFiniteVec2(snapshot.PlayerXZ))
			{
				playerAnchor = snapshot.PlayerXZ;
			}

			if (realPoints.Count == 0 && predictedPoints.Count == 0 && !playerAnchor.HasValue)
			{
				DrawPlaceholder("No respawn-wall data captured yet.");
				return;
			}

			Vector2 min = new(float.MaxValue, float.MaxValue);
			Vector2 max = new(float.MinValue, float.MinValue);
			void Extend(Vector2 point)
			{
				if (point.X < min.X) min.X = point.X;
				if (point.Y < min.Y) min.Y = point.Y;
				if (point.X > max.X) max.X = point.X;
				if (point.Y > max.Y) max.Y = point.Y;
			}

			for (int i = 0; i < realPoints.Count; i++)
				Extend(realPoints[i]);
			for (int i = 0; i < predictedPoints.Count; i++)
				Extend(predictedPoints[i].pos);
			if (playerAnchor.HasValue)
				Extend(playerAnchor.Value);

			if (min.X == float.MaxValue || min.Y == float.MaxValue)
			{
				min = new Vector2(-10f, -10f);
				max = new Vector2(10f, 10f);
			}

			float width = MathF.Max(max.X - min.X, 1f);
			float height = MathF.Max(max.Y - min.Y, 1f);
			var contentMin = rectMin + new Vector2(padding, padding);
			var contentMax = rectMax - new Vector2(padding, padding);
			var contentSize = contentMax - contentMin;
			float plotScale = MathF.Min(contentSize.X / width, contentSize.Y / height);
			float usedWidth = width * plotScale;
			float usedHeight = height * plotScale;
			var plotOrigin = contentMin + (contentSize - new Vector2(usedWidth, usedHeight)) * 0.5f;
			var plotMax = plotOrigin + new Vector2(usedWidth, usedHeight);

			Vector2 Project(Vector2 point)
			{
				float x = plotOrigin.X + (point.X - min.X) * plotScale;
				float y = plotOrigin.Y + (point.Y - min.Y) * plotScale;
				return new Vector2(x, y);
			}

			drawList.AddRect(plotOrigin, plotMax, borderCol, 0f);

			uint gridCol = ImGui.GetColorU32(new Vector4(0.55f, 0.65f, 0.85f, 0.5f));
			var centerX = (plotOrigin.X + plotMax.X) * 0.5f;
			var centerY = (plotOrigin.Y + plotMax.Y) * 0.5f;
			drawList.AddLine(new Vector2(plotOrigin.X, centerY), new Vector2(plotMax.X, centerY), gridCol, 1f);
			drawList.AddLine(new Vector2(centerX, plotOrigin.Y), new Vector2(centerX, plotMax.Y), gridCol, 1f);

			uint axisLabelCol = ImGui.GetColorU32(new Vector4(0.82f, 0.86f, 0.92f, 0.75f));
			drawList.AddText(new Vector2(centerX - 4f * uiScale, plotOrigin.Y + 2f * uiScale), axisLabelCol, "N");
			drawList.AddText(new Vector2(centerX - 4f * uiScale, plotMax.Y - 14f * uiScale), axisLabelCol, "S");
			drawList.AddText(new Vector2(plotMax.X - 14f * uiScale, centerY - 6f * uiScale), axisLabelCol, "E");

			uint realCol = ImGui.GetColorU32(new Vector4(1.0f, 0.55f, 0.28f, 0.9f));
			uint predictedFillCol = ImGui.GetColorU32(new Vector4(0.2f, 0.85f, 1.0f, 0.95f));
			uint predictedOutlineCol = ImGui.GetColorU32(new Vector4(0.55f, 0.75f, 1.0f, 1.0f));
			uint predictedTextCol = ImGui.GetColorU32(new Vector4(0.95f, 0.99f, 1.0f, 1.0f));
			uint playerCol = ImGui.GetColorU32(new Vector4(1.0f, 0.9f, 0.35f, 1.0f));

			float realRadius = MathF.Max(2f, 2.2f * uiScale);
			float predictedRadius = MathF.Max(3f, 2.8f * uiScale);
			float playerRadius = MathF.Max(4f, 3.2f * uiScale);

			for (int i = 0; i < realPoints.Count; i++)
			{
				var pt = Project(realPoints[i]);
				drawList.AddCircleFilled(pt, realRadius, realCol, 12);
			}

			for (int i = 0; i < predictedPoints.Count; i++)
			{
				var (pos, index) = predictedPoints[i];
				var pt = Project(pos);
				drawList.AddCircleFilled(pt, predictedRadius, predictedFillCol, 24);
				drawList.AddCircle(pt, predictedRadius + 0.5f * uiScale, predictedOutlineCol, 24, 1.25f);
				string label = index.ToString();
				var textSize = ImGui.CalcTextSize(label);
				var offset = new Vector2(predictedRadius + 6f * uiScale, -(predictedRadius + 4f * uiScale));
				var textPos = pt - textSize * 0.5f + offset;
				drawList.AddText(textPos, predictedTextCol, label);
			}

			if (playerAnchor.HasValue)
			{
				var anchor = Project(playerAnchor.Value);
				drawList.AddCircle(anchor, playerRadius, playerCol, 24, 2f);
				drawList.AddLine(anchor + new Vector2(-playerRadius, 0f), anchor + new Vector2(playerRadius, 0f), playerCol, 1.5f);
				drawList.AddLine(anchor + new Vector2(0f, -playerRadius), anchor + new Vector2(0f, playerRadius), playerCol, 1.5f);
			}

			ImGui.Spacing();
			var lightPrimary = new Vector4(0.92f, 0.96f, 1.0f, 1.0f);
			var lightSecondary = new Vector4(0.78f, 0.84f, 0.94f, 1.0f);
			var lightWarning = new Vector4(1.0f, 0.78f, 0.65f, 1.0f);
			var currentLabel = usingActiveWalls ? "Respawn walls (current layout)" : "Respawn walls (raw)";
			ImGui.TextColored(lightPrimary, $"{currentLabel}: {realPoints.Count}");
			if (usingActiveWalls)
			{
				ImGui.TextColored(lightSecondary, $"Source: clustered layer ({activeWalls.Count} points)");
			}
			else if (activeWalls.Count > 0)
			{
				ImGui.TextColored(lightWarning, "Active layout unavailable; showing raw walls.");
			}
			ImGui.TextColored(lightPrimary, $"Predicted centers: {predictedPoints.Count}");
			if (playerAnchor.HasValue)
			{
				ImGui.TextColored(lightPrimary, "Player anchor");
			}
			ImGui.TextColored(lightSecondary, $"Span ~ {width:F1}m x {height:F1}m");

			if (!string.IsNullOrEmpty(snapshot.Error))
			{
				ImGui.TextColored(lightWarning, snapshot.Error);
			}
			else if (predictedPoints.Count == 0)
			{
				ImGui.TextColored(lightWarning, "Predicted centers unavailable for this floor.");
			}

			if (deepDungeon != null)
			{
				ImGui.TextColored(lightSecondary, $"Floor {deepDungeon->Floor} - Layout {deepDungeon->ActiveLayoutIndex}");
			}

			// Layout separation and grid detection debug info
			ImGui.TextColored(lightSecondary, $"Layout sep: {snapshot.RawRespawnWallCount} walls -> K={snapshot.LayoutSeparationK} -> {snapshot.ActiveLayoutWallCount} active");
			ImGui.TextColored(lightSecondary, $"Grid: {snapshot.DetectedGridCols}x{snapshot.DetectedGridRows} (K={snapshot.DetectedGridCols},{snapshot.DetectedGridRows})");
			ImGui.TextColored(lightSecondary, $"Room centers detected: {snapshot.DetectedRoomCenterCount}");
		}

		private unsafe void DrawRoomCenterControlsUnderMap(InstanceContentDeepDungeon* deepDungeon, int playerRoom)
		{
			ImGui.Spacing();
			ImGui.BeginGroup();

			bool canSetCenter = playerRoom >= 0;
			bool positionStable = _dutyState?.IsPlayerPositionStable() ?? true;
			double waitTime = _dutyState?.GetStabilizationTimeRemaining() ?? 0.0;

			ImGui.BeginDisabled(!canSetCenter || !positionStable);
			if (ImGui.Button("將當前位置設為中心"))
			{
				try
				{
					var player = Service.LocalPlayer;
					if (player != null)
					{
						var pos = player.Position;
						DeepDungeon.Fsd.Dalamud.Map.MapPosGeneration.OverrideCenter(deepDungeon, playerRoom, pos);
						Service.Log.Info($"[Necromancer] Center for room {playerRoom} overridden to ({pos.X:f3}, {pos.Y:f3}, {pos.Z:f3})");
					}
				}
				catch (Exception ex)
				{
					Service.Log.Error($"[Necromancer] Override center failed: {ex}");
				}
			}
			if (ImGui.Button("自動產生本樓層中心"))
			{
				AutoGenerateRoomCenters(deepDungeon);
			}
			ImGui.EndDisabled();
			if (ImGui.Button("清除所有樓層中心資料"))
			{
				try
				{
					Map.MapPosGeneration.ClearAllCenters();
					Service.Log.Info("[Necromancer] 已清除所有樓層的房間中心資料。");
				}
				catch (Exception ex)
				{
					Service.Log.Error($"[Necromancer] Clear all centers failed: {ex}");
				}
			}
			bool canClearFloorCenters = deepDungeon != null;
			ImGui.BeginDisabled(!canClearFloorCenters);
			if (ImGui.Button("清除當前樓層中心資料"))
			{
				try
				{
					if (DeepDungeon.Fsd.Dalamud.Map.MapPosGeneration.ClearFloorCenters(deepDungeon))
					{
						Service.Log.Info($"[Necromancer] 已清除樓層 {deepDungeon->Floor} (Tileset {deepDungeon->ActiveLayoutIndex}) 的房間中心資料。");
					}
					else
					{
						Service.Log.Info("[Necromancer] 目前樓層沒有可清除的房間中心資料。");
					}
				}
				catch (Exception ex)
				{
					Service.Log.Error($"[Necromancer] Clear floor centers failed: {ex}");
				}
			}
			ImGui.EndDisabled();
			if (!canSetCenter)
			{
				ImGui.TextDisabled("房間未知");
			}
			else if (!positionStable)
			{
				ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f), $"房間 {playerRoom} - 等待位置穩定 ({waitTime:F1}s)");
			}
			else
			{
				ImGui.TextDisabled($"房間 {playerRoom}");
			}
			ImGui.EndGroup();
		}

        private static unsafe int GetLocalPlayerRoomIndex(InstanceContentDeepDungeon* deepDungeon)
        {
            return DeepDungeon.Fsd.Dalamud.GameState.RoomGraph.GetLocalPlayerRoomIndex(deepDungeon);
        }

        private static unsafe int GetPassageRoomIndex(InstanceContentDeepDungeon* deepDungeon)
        {
            return DeepDungeon.Fsd.Dalamud.GameState.RoomGraph.GetPassageRoomIndex(deepDungeon);
        }

        private static unsafe bool IsRoomReachable(InstanceContentDeepDungeon* deepDungeon, int fromRoom, int toRoom)
        {
            return DeepDungeon.Fsd.Dalamud.GameState.RoomGraph.IsRoomReachable(deepDungeon, fromRoom, toRoom);
        }
        private unsafe bool TryGetPassageActorPosition(InstanceContentDeepDungeon* deepDungeon, out Vector3 dest)
        {
            dest = default;
            try
            {
                ReadOnlySpan<uint> passageBaseIds = stackalloc uint[]
                {
                    0x1EA094, // CairnPalace (POTD)
                    0x1EA9A3, // BeaconHoH   (HOH)
                    0x1EB867, // PylonEO     (EO)
                    0x1EBE24, // PylonPT     (PT)
                };

                IGameObject? best = null;
                float bestDistSq = float.MaxValue;
                var player = Service.LocalPlayer;

                foreach (var obj in Service.GameObjects)
                {
                    if (obj == null) continue;
                    uint id = obj.BaseId;
                    bool isPassage = false;
                    for (int i = 0; i < passageBaseIds.Length; i++)
                    {
                        if (id == passageBaseIds[i])
                        {
                            isPassage = true;
                            break;
                        }
                    }
                    if (!isPassage) continue;

                    float d2;
                    if (player != null)
                    {
                        var dx = obj.Position.X - player.Position.X;
                        var dz = obj.Position.Z - player.Position.Z;
                        d2 = dx * dx + dz * dz;
                    }
                    else
                    {
                        d2 = 0f;
                    }

                    if (best == null || d2 < bestDistSq)
                    {
                        best = obj;
                        bestDistSq = d2;
                    }
                }

                if (best != null)
                {
                    dest = best.Position;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[Necromancer] TryGetPassageActorPosition 失敗：{ex}");
            }
            return false;
        }

        private unsafe bool TryGetPassageDestination(InstanceContentDeepDungeon* deepDungeon, out Vector3 dest, out bool usedActorPosition, out int passageRoomIndex)
        {
            return PassageLocator.TryResolvePassageDestination(deepDungeon, out dest, out usedActorPosition, out passageRoomIndex);
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

			if (!TryGetPassageDestination(dd, out var dest, out bool usedActor, out _))
				return;

			if (!usedActor)
				return;

			if (DeepDungeon.Fsd.Dalamud.moveHelper.VNav.SimpleMove.PathfindAndMoveTo(dest, false))
			{
				Service.Log.Info("[Necromancer] Passage已顯示，已更新導航至出口。");
				ClearPendingPassageFollowup();
			}
		}

        private unsafe void AutoGenerateRoomCenters(InstanceContentDeepDungeon* deepDungeon)
        {
            if (deepDungeon == null)
                return;

            try
            {
                if (!RoomCenterGenerator.TryGenerate(deepDungeon, out var centers, out var stats, out var error))
                {
                    Service.Log.Error($"[Necromancer] Room center auto-generation failed: {error}");
                    return;
                }

                if (!Map.MapPosGeneration.ApplyGeneratedCenters(deepDungeon, centers))
                {
                    Service.Log.Error("[Necromancer] Failed to persist generated room centers.");
                    return;
                }

                Service.Log.Info($"[Necromancer] Generated {stats.AppliedRooms} centers ({stats.FallbackRooms} fallback, {stats.RespawnPoints} respawn walls, {stats.RoomClusters} clusters).");
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[Necromancer] Auto-generate room centers error: {ex}");
            }
        }

        private static void TryInvokePalacePalFetch()
        {
            try
            {
                // Direct call instead of reflection (obfuscation-safe)
                Map.PalacePalData.OnEnterDeepDungeon();
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[FsdEngine] PalacePal fetch failed: {ex}");
            }
        }

        private static (bool failed, bool hasLocal) GetPalacePalStatus()
        {
            try
            {
                // Direct property access instead of reflection (obfuscation-safe)
                bool failed = Map.PalacePalData.LastFetchFailedForCurrentTerritory;
                bool hasLocal = Map.PalacePalData.HasLocalCacheForCurrentTerritory;
                return (failed, hasLocal);
            }
            catch
            {
                return (false, false);
            }
        }

		private unsafe void DrawRoomCenterDebugOverlay(InstanceContentDeepDungeon* deepDungeon, RoomCenterGenerator.DebugSnapshot snapshot)
		{
			if (snapshot == null)
				return;

			uint colRaw = ImGui.GetColorU32(new Vector4(1.0f, 0.35f, 0.35f, 0.8f));
			uint colActive = ImGui.GetColorU32(new Vector4(0.4f, 0.7f, 1.0f, 0.9f));
			uint colCluster = ImGui.GetColorU32(new Vector4(0.3f, 1.0f, 0.9f, 0.9f));
			uint colFinalActual = ImGui.GetColorU32(new Vector4(0.3f, 1.0f, 0.3f, 1.0f));
			uint colFinalFallback = ImGui.GetColorU32(new Vector4(1.0f, 0.85f, 0.3f, 1.0f));
			uint colPlayer = ImGui.GetColorU32(new Vector4(1.0f, 0.5f, 0.1f, 1.0f));
			uint colArrow = ImGui.GetColorU32(new Vector4(0.9f, 0.9f, 0.2f, 1.0f));
			uint colPredicted = ImGui.GetColorU32(new Vector4(0.85f, 0.85f, 0.95f, 0.9f));
			uint colFailure = ImGui.GetColorU32(new Vector4(1.0f, 0.35f, 0.35f, 1.0f));

			foreach (var raw in snapshot.RawRespawnWalls)
			{
				WorldDrawHelper.DrawWorldCircle(raw, 0.5f, colRaw);
			}

			foreach (var filtered in snapshot.ActiveLayoutWalls)
			{
				WorldDrawHelper.DrawWorldCircle(filtered, 0.7f, colActive);
			}

			foreach (var center in snapshot.RoomCenters)
			{
				WorldDrawHelper.DrawWorldCircle(center, 1.0f, colCluster);
			}

			var finals = snapshot.FinalCenters;
			var predicted = snapshot.PredictedCenters;
			var maskActual = snapshot.ActualCentersMask;
			float yLevel = snapshot.PlayerY;

			if (predicted != null)
			{
				for (int i = 0; i < predicted.Length; i++)
				{
					if (!predicted[i].HasValue)
						continue;
					var pos2 = predicted[i]!.Value;
					var pos = new Vector3(pos2.X, yLevel, pos2.Y);
					WorldDrawHelper.DrawWorldCircle(pos, 0.9f, colPredicted);
					WorldDrawHelper.DrawWorldText(pos + new Vector3(0f, 0.25f, 0f), colPredicted, $"#{i}");
				}
			}

			if (finals != null)
			{
				for (int i = 0; i < finals.Length; i++)
				{
					if (!finals[i].HasValue)
						continue;
					var pos = finals[i]!.Value;
					bool actual = maskActual != null && i < maskActual.Length && maskActual[i];
					var col = actual ? colFinalActual : colFinalFallback;
					float radius = actual ? 1.6f : 1.2f;
					WorldDrawHelper.DrawWorldCircle(pos, radius, col);
					bool labelShownByPred = predicted != null && i < predicted.Length && predicted[i].HasValue;
					if (!labelShownByPred)
					{
						WorldDrawHelper.DrawWorldText(pos + new Vector3(0f, 0.25f, 0f), col, $"#{i}");
					}
				}
			}

			Vector2? GetCenterForRoom(int index)
			{
				if (index < 0)
					return null;
				if (finals != null && index < finals.Length && finals[index].HasValue)
					return new Vector2(finals[index]!.Value.X, finals[index]!.Value.Z);
				if (predicted != null && index < predicted.Length && predicted[index].HasValue)
					return predicted[index];
				return null;
			}

			var playerRoomCenter = snapshot.PlayerRoomCenter ?? GetCenterForRoom(snapshot.PlayerRoomIndex);
			if (snapshot.PlayerRoomIndex >= 0 && playerRoomCenter.HasValue)
			{
				var playerPos2 = playerRoomCenter.Value;
				var playerPos3 = new Vector3(playerPos2.X, yLevel, playerPos2.Y);
				WorldDrawHelper.DrawWorldCircle(playerPos3, 2.4f, colPlayer);
				WorldDrawHelper.DrawWorldText(playerPos3 + new Vector3(0f, 0.35f, 0f), colPlayer, $"Player #{snapshot.PlayerRoomIndex}");

				WorldDrawHelper.DrawWorldText(playerPos3 + new Vector3(0f, 0.55f, 0f), snapshot.AlignmentFailed ? colRaw : colFinalActual,
					snapshot.AlignmentFailed ? "Alignment Failed" : "Alignment OK");

				if (deepDungeon != null)
				{
					var map = deepDungeon->MapData;
					if (snapshot.PlayerRoomIndex >= 0 && snapshot.PlayerRoomIndex < map.Length)
					{
						var flags = map[snapshot.PlayerRoomIndex];
						int row = snapshot.PlayerRoomIndex / 5;
						int col = snapshot.PlayerRoomIndex % 5;

						void TryDrawNeighbor(int targetIndex, string label)
						{
							var target = GetCenterForRoom(targetIndex);
							if (!target.HasValue)
								return;
							var neighborPos2 = target.Value;
							var neighborPos3 = new Vector3(neighborPos2.X, yLevel, neighborPos2.Y);
							DrawLabelledArrow(playerPos3, neighborPos3, colArrow, label);
						}

						if ((flags & InstanceContentDeepDungeon.RoomFlags.ConnectionE) != 0 && col < 4)
						{
							TryDrawNeighbor(snapshot.PlayerRoomIndex + 1, "E");
						}
						if ((flags & InstanceContentDeepDungeon.RoomFlags.ConnectionN) != 0 && row > 0)
						{
							TryDrawNeighbor(snapshot.PlayerRoomIndex - 5, "N");
						}
					}
				}
			}

			if (snapshot.FailureRoomIndex >= 0 && snapshot.FailureActualCenter.HasValue && snapshot.FailurePredictedCenter.HasValue)
			{
				var actual = snapshot.FailureActualCenter.Value;
				var predictedFailure = snapshot.FailurePredictedCenter.Value;
				var actual3 = new Vector3(actual.X, yLevel, actual.Y);
				var predicted3 = new Vector3(predictedFailure.X, yLevel, predictedFailure.Y);
				WorldDrawHelper.DrawWorldLine(predicted3, actual3, colFailure);
				var mid = (actual3 + predicted3) * 0.5f + new Vector3(0f, 0.3f, 0f);
				WorldDrawHelper.DrawWorldText(mid, colFailure, $"Δ#{snapshot.FailureRoomIndex}");
			}

			DrawAxisHints(snapshot, yLevel, colArrow);
		}


        // ===== FullSelfDelving public controls (to be hooked from UI/commands) =====

        public void StartFullSelfDelvingPTChest()
        {
            try
            {
                if (TryStartOutsideDutyFsd(
                        () => new PTChestScenario(),
                        1,
                        false,
                        detailedMapScenarioKey: null,
                        out var error))
                    Service.Log.Info($"[Necromancer] FullSelfDelving started: PT chest (banded only)");
                else
                    Service.Log.Warning($"[Necromancer] FullSelfDelving start rejected: {error}");
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[Necromancer] StartFullSelfDelvingPTChest failed: {ex}");
            }
        }

        public object StartControlledPilgrimsTraverseCapture(
            int targetLoops,
            bool infinite,
            string? confirmation)
        {
            if (!_detailedMapHostOptions.SupportsControlledPtSurvey)
                return new { ok = false, error = "Controlled reusable-save survey capture is unavailable for this FSD host." };

            if (!string.Equals(confirmation, "start-controlled-pt-capture", StringComparison.Ordinal))
                return new { ok = false, error = "Controlled PT capture requires confirmation=start-controlled-pt-capture." };

            var session = new ControlledPtSurveySession();
            bool started = TryStartOutsideDutyFsd(
                () => new ControlledPt21To30Scenario(session),
                Math.Max(1, targetLoops),
                infinite,
                DetailedMapEvidenceContract.PilgrimsTraverse21To30ScenarioKey,
                out string error);
            return started
                ? new { ok = true, scenario = "PT 21-30 controlled capture" }
                : new { ok = false, error };
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
                1 => "Safety",
                2 => "Sight",
                3 => "Strength",
                4 => "Steel",
                5 => "Affluence",
                6 => "Flight",
                7 => "Alteration",
                8 => "Purity",
                9 => "Fortune",
                10 => "Witching",
                11 => "Serenity",
                12 => "Rage",
                13 => "Lust",
                14 => "Intuition",
                15 => "Raising",
                16 => "Resolution",
                17 => "Frailty",
                18 => "Concealment",
                19 => "Petrification",
                20 => "Protomander of Lethargy",
                21 => "Protomander of Storms",
                22 => "Protomander of Dread",
                23 => "Protomander of Safety",
                24 => "Protomander of Sight",
                25 => "Protomander of Strength",
                26 => "Protomander of Steel",
                27 => "Protomander of Affluence",
                28 => "Protomander of Flight",
                29 => "Protomander of Alteration",
                30 => "Protomander of Purity",
                31 => "Protomander of Fortune",
                32 => "Protomander of Witching",
                33 => "Protomander of Serenity",
                34 => "Protomander of Intuition",
                35 => "Protomander of Raising",
                36 => "Haste",
                37 => "Purification",
                38 => "Devotion",
                _ => $"Pomander #{pomanderId}"
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
						string name = $"Pomander Slot {i + 1}";
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
				visuals[i] = new PomanderVisual((uint)(i + 1), $"Pomander Slot {i + 1}", 0, true);
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
