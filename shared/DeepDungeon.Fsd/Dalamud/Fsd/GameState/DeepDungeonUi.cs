using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DeepDungeon.Fsd.Core;

using global::Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Models;

namespace DeepDungeon.Fsd.Dalamud.GameState
{
	public sealed class DeepDungeonUi
	{
		private const uint DeleteSavePromptAddonRowId = 10412;
		private const uint AbandonDutyPromptAddonRowId = 2545;

		#region Agent-Based Menu Actions (Robust - not affected by menu order changes)
		
		/// <summary>
		/// Opens DeepDungeonSaveData for entering the dungeon via Agent event.
		/// This is more robust than addon callbacks as it's not affected by menu item order.
		/// </summary>
		public static unsafe bool EnterDeepDungeonViaAgent()
		{
			try
			{
				// EventType=0, Param=0: Enter DeepDungeon (opens save data)
				AgentId.DeepDungeonMenu.SendEvent(0, 0);
				return true;
			}
			catch { return false; }
		}
		
		/// <summary>
		/// Opens DeepDungeonSaveData for deleting a save via Agent event.
		/// This is more robust than addon callbacks as it's not affected by menu item order.
		/// </summary>
		public static unsafe bool OpenDeleteSaveViaAgent()
		{
			try
			{
				// EventType=0, Param=3: Delete Save (opens save data for deletion)
				AgentId.DeepDungeonMenu.SendEvent(0, 3);
				return true;
			}
			catch { return false; }
		}
		
		/// <summary>
		/// Clicks a save slot in DeepDungeonSaveData via Agent event for ENTRY mode.
		/// Based on debug data: event#0 with AtkValue[0]=slotIndex, AtkValue[1]=0 (entry mode)
		/// </summary>
		/// <param name="slotIndex">Zero-based logical save-list index.</param>
		public static unsafe bool ClickSaveSlotForEntry(int slotIndex)
		{
			try
			{
				if (slotIndex < 0 || !TryReadSaveSlots(out var slots, out _) ||
                    !slots.Any(slot => slot.Index == slotIndex && slot.Enterable)) return false;
                var idx = slotIndex;
				// Entry mode: SendEvent(0, slotIndex, 0) - AtkValue[0]=slotIndex, AtkValue[1]=0
				AgentId.DeepDungeonSaveData.SendEvent(0, idx, 0);
				try { Service.Log.Info($"[DeepDungeonUi] ClickSaveSlotForEntry via Agent: slot={idx}, mode=0"); } catch { }
				return true;
			}
			catch { return false; }
		}
		
		/// <summary>
		/// Clicks a save slot in DeepDungeonSaveData via Agent event for DELETE mode.
		/// Based on debug data: event#0 with AtkValue[0]=slotIndex, AtkValue[1]=1 (delete mode)
		/// </summary>
		/// <param name="slotIndex">Zero-based logical save-list index.</param>
		public static unsafe bool ClickSaveSlotForDelete(int slotIndex)
		{
			try
			{
				if (slotIndex < 0 || !TryReadSaveSlots(out var slots, out _) ||
                    !slots.Any(slot => slot.Index == slotIndex)) return false;
                var idx = slotIndex;
				// Delete mode: SendEvent(0, slotIndex, 1) - AtkValue[0]=slotIndex, AtkValue[1]=1
				AgentId.DeepDungeonSaveData.SendEvent(0, idx, 1);
				try { Service.Log.Info($"[DeepDungeonUi] ClickSaveSlotForDelete via Agent: slot={idx}, mode=1"); } catch { }
				// Note: Don't close for delete mode - the YesNo confirmation should appear
				return true;
			}
			catch { return false; }
		}
		
		/// <summary>
		/// Clicks the Commence button in ContentsFinderConfirm using the button directly.
		/// This is more explicit than using callback index 8.
		/// </summary>
		public static unsafe bool ClickCommenceButton()
		{
			try
			{
				if (!AddonHelper.TryGetByName<AddonContentsFinderConfirm>("ContentsFinderConfirm", out var addon) 
				    || !addon->AtkUnitBase.IsAddonAndNodesReady())
					return false;
				
				if (addon->CommenceButton == null)
					return false;
				
				// Use the Click() extension method from OmenTools
				addon->CommenceButton->Click();
				return true;
			}
			catch { return false; }
		}
		
		#endregion
		public static unsafe bool TryGetAddon(string name, out AtkUnitBase* addon)
		{
			addon = null;
			try
			{
				if (AddonHelper.TryGetByName<AtkUnitBase>(name, out var a) && a->IsAddonAndNodesReady())
				{
					addon = a;
					return true;
				}
			}
			catch { }
			return false;
		}

		public static bool IsAddonOpen(string name)
		{
			unsafe
			{
				return TryGetAddon(name, out _);
			}
		}

		public static unsafe bool TryGetSelectYesNo(out AtkUnitBase* addon)
		{
			addon = null;
			try
			{
				if (TryGetAddon("SelectYesno", out var a) && a->IsAddonAndNodesReady())
				{
					addon = a;
					return true;
				}
			}
			catch { }
			return false;
		}

		public static unsafe bool TryGetSelectString(out AtkUnitBase* addon)
		{
			addon = null;
			try
			{
				if (TryGetAddon("SelectString", out var a) && a->IsAddonAndNodesReady())
				{
					addon = a;
					return true;
				}
			}
			catch { }
			return false;
		}

		public static unsafe bool TryGetTalk(out AtkUnitBase* addon)
		{
			addon = null;
			try
			{
				// Prefer "Talk"; fallback to "EventTalk" if present
				if (TryGetAddon("Talk", out var talk) && talk->IsAddonAndNodesReady())
				{
					addon = talk;
					return true;
				}
				if (TryGetAddon("EventTalk", out var etalk) && etalk->IsAddonAndNodesReady())
				{
					addon = etalk;
					return true;
				}
			}
			catch { }
			return false;
		}

		public static unsafe bool Fire(AtkUnitBase* addon, params object[] args)
		{
			try
			{
				if (addon == null) return false;
				using var atkValues = new AtkValueArray(args);
				addon->FireCallback((uint)atkValues.Length, atkValues.Pointer, true);
				return true;
			}
			catch { return false; }
		}

		public static unsafe bool TryCloseAddon(string name)
		{
			try
			{
				if (TryGetAddon(name, out var addon))
				{
					addon->Close(true);
					return true;
				}
			}
			catch { }
			return false;
		}

		public static unsafe bool IsDeleteSaveConfirmationPrompt(AtkUnitBase* addon, out string error)
		{
			return IsConfirmationPromptForAddonRow(addon, DeleteSavePromptAddonRowId, "delete", out error);
		}

		public static unsafe bool IsAbandonDutyConfirmationPrompt(AtkUnitBase* addon, out string error)
		{
			return IsConfirmationPromptForAddonRow(addon, AbandonDutyPromptAddonRowId, "abandon duty", out error);
		}

		public static unsafe bool TryGetConfirmationPromptText(AtkUnitBase* addon, out string prompt, out string error)
		{
			prompt = string.Empty;
			error = string.Empty;
			try
			{
				if (addon == null || !addon->IsAddonAndNodesReady())
				{
					error = "confirmation is not ready";
					return false;
				}

				var promptNode = ((AddonSelectYesno*)addon)->PromptText;
				if (promptNode == null)
				{
					error = "confirmation prompt node is unavailable";
					return false;
				}

				prompt = promptNode->NodeText.ExtractText();
				return true;
			}
			catch (Exception ex)
			{
				error = $"confirmation prompt read failed: {ex.Message}";
				return false;
			}
		}

		private static unsafe bool IsConfirmationPromptForAddonRow(
			AtkUnitBase* addon,
			uint addonRowId,
			string promptKind,
			out string error)
		{
			error = string.Empty;
			try
			{
				if (addon == null || !addon->IsAddonAndNodesReady())
				{
					error = $"{promptKind} confirmation is not ready";
					return false;
				}

				var promptNode = ((AddonSelectYesno*)addon)->PromptText;
				var prompt = promptNode == null ? string.Empty : promptNode->NodeText.ExtractText();
				var expected = Service.DataManager
					.GetExcelSheet<Lumina.Excel.Sheets.Addon>()?
					.GetRow(addonRowId)
					.Text
					.ExtractText();
				var expectedPrefix = expected?
					.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.FirstOrDefault();
				if (string.IsNullOrWhiteSpace(expectedPrefix))
				{
					error = $"{promptKind} confirmation text row {addonRowId} is unavailable";
					return false;
				}

				var normalizedPrompt = string.Concat(prompt.Where(character => !char.IsWhiteSpace(character)));
				var normalizedExpected = string.Concat(expectedPrefix.Where(character => !char.IsWhiteSpace(character)));
				if (!normalizedPrompt.Contains(normalizedExpected, StringComparison.Ordinal))
				{
					error = $"unexpected confirmation prompt; expected Addon row {addonRowId}, actual: {prompt}";
					return false;
				}

				return true;
			}
			catch (Exception ex)
			{
				error = $"{promptKind} confirmation validation failed: {ex.Message}";
				return false;
			}
		}

		public static bool CloseDeepDungeonEntryWindows()
		{
            // A pending modal can callback into its entry agent when it closes.
            // Do not force-close its parent chain; let the game's cancellation
            // path handle it (native crash observed during bridge cleanup).
            if (IsAddonOpen("SelectString") || IsAddonOpen("SelectYesno") || IsAddonOpen("ContentsFinderConfirm"))
            {
                Service.Log.Warning("[DeepDungeonUi] Refusing forced entry-window cleanup while a selection or confirmation is open.");
                return false;
            }
			TryCloseAddon("DeepDungeonSaveData");
			TryCloseAddon("DeepDungeonMenu");
			TryCloseAddon("ContentsFinderConfirm");
			TryCloseAddon("SelectYesno");
			TryCloseAddon("SelectString");
			TryCloseAddon("Talk");
			TryCloseAddon("EventTalk");
			TryCloseAddon("ContextIconMenu");
            return true;
		}

        public static bool TryGetEmptySlotsFromDeepDungeonSaveData(out bool slot1Empty, out bool slot2Empty, bool log = true)
        {
            slot1Empty = slot2Empty = false;
            if (!TryReadSaveSlots(out var slots, out _) || slots.Count != 2) return false;
            slot1Empty = slots[0].Empty;
            slot2Empty = slots[1].Empty;
            return true;
        }

        public static unsafe bool TryReadSaveSlots(out IReadOnlyList<SaveSlotSnapshot> slots, out string error)
        {
            slots = Array.Empty<SaveSlotSnapshot>();
            error = "存檔列表尚未完整載入或結構無法識別。";
            try
            {
                if (!TryGetAddon("DeepDungeonSaveData", out var save)) return false;
                if (!save->IsAddonAndNodesReady() || save->UldManager.NodeList == null)
                    return ReportSaveReadFailure(save, "addon-not-ready", out error);
                AtkComponentList* list = null;
                uint listNodeId = 0;
                for (int nodeIndex = 0; nodeIndex < save->UldManager.NodeListCount; nodeIndex++)
                {
                    var node = save->UldManager.NodeList[nodeIndex];
                    if (node == null) continue;
                    var candidate = node->GetAsAtkComponentList();
                    if (candidate == null) continue;
                    if (list != null) return ReportSaveReadFailure(save, "multiple-save-lists", out error);
                    list = candidate;
                    listNodeId = node->NodeId;
                }
                if (list == null) return ReportSaveReadFailure(save, "save-list-unavailable", out error);
                if (list->IsUpdatePending) return ReportSaveReadFailure(save, "list-update-pending", out error);
                int count = list->GetItemCount();
                if (count is < 1 or > 16) return ReportSaveReadFailure(save, $"list-count={count}", out error);
                string? emptyLabel = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>()?
                    .GetRow(10404).Text.ExtractText();
                if (string.IsNullOrWhiteSpace(emptyLabel)) return ReportSaveReadFailure(save, "empty-label-unavailable", out error);
                var addonSheet = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
                string? failedLabel = addonSheet?.GetRow(10499).Text.ExtractText().Trim();
                string? completedLabel = addonSheet?.GetRow(10497).Text.ExtractText().Trim();
                var result = new List<SaveSlotSnapshot>(count);
                for (int i = 0; i < count; i++)
                {
                    var renderer = list->GetItemRenderer(i);
                    if (renderer == null) return ReportSaveReadFailure(save, $"renderer-{i}-null", out error);
                    if (renderer->ListItemIndex != i || renderer->UldManager.NodeList == null)
                        return ReportSaveReadFailure(save, $"renderer-{i}-index={renderer->ListItemIndex},nodes={renderer->UldManager.NodeListCount}", out error);
                    // Live PT rows identify progress by NodeId 7. Array positions change
                    // with the ULD layout (the former index 15 now holds the job level).
                    var textNode = renderer->UldManager.SearchNodeById(7);
                    if (textNode == null) return ReportSaveReadFailure(save, $"progress-{i}-node-null", out error);
                    var text = textNode->GetAsAtkTextNode();
                    if (text == null) return ReportSaveReadFailure(save, $"progress-{i}-not-text", out error);
                    var identity = new List<string>();
                    bool entryBlocked = false;
                    for (int nodeIndex = 0; nodeIndex < renderer->UldManager.NodeListCount; nodeIndex++)
                    {
                        var node = renderer->UldManager.NodeList[nodeIndex];
                        if (node == null || node == textNode) continue;
                        var identityNode = node->GetAsAtkTextNode();
                        if (identityNode == null) continue;
                        string label = identityNode->NodeText.ExtractText().Trim();
                        if (SaveSlotUiPolicy.IsBlockingMessage(label, failedLabel, completedLabel))
                        {
                            entryBlocked = true;
                            continue;
                        }
                        if (label.Length > 0 && label.Any(char.IsLetter) && !label.Any(char.IsDigit)) identity.Add(label);
                    }
                    if (!SaveSlotUiPolicy.TryParse(i, text->NodeText.ExtractText(), emptyLabel,
                        string.Join("|", identity), !list->GetItemDisabledState(i) && !entryBlocked, out var slot, out var parseError))
                        return ReportSaveReadFailure(save, parseError, out error);
                    result.Add(slot);
                }
                slots = result;
                error = string.Empty;
                var snapshot = $"listNodeId={listNodeId}; count={count}; " + string.Join("; ",
                    result.Select(slot => $"slot={slot.Index},empty={slot.Empty},enterable={slot.Enterable},progress='{slot.Progress}',identity='{slot.Identity}'"));
                if (!string.Equals(snapshot, _lastSaveReadSnapshot, StringComparison.Ordinal))
                {
                    _lastSaveReadSnapshot = snapshot;
                    Service.Log.Info($"[DeepDungeonUi.SaveRead] {snapshot}");
                }
                return true;
            }
            catch (Exception ex) { error = $"存檔讀取失敗：{ex.Message}"; return false; }
        }

        private static string _lastSaveReadSnapshot = string.Empty;
        private static DateTime _lastSaveReadDiagnosticAt;
        private static unsafe bool ReportSaveReadFailure(AtkUnitBase* save, string reason, out string error)
        {
            error = "存檔列表讀取失敗：" + reason;
            if (DateTime.UtcNow - _lastSaveReadDiagnosticAt < TimeSpan.FromSeconds(3)) return false;
            _lastSaveReadDiagnosticAt = DateTime.UtcNow;
            try
            {
                var detail = new System.Text.StringBuilder();
                detail.Append($"reason={reason}; addonNodes={save->UldManager.NodeListCount}; values={save->AtkValuesCount}; ");
                if (save->AtkValues != null)
                    for (int i = 0; i < Math.Min((int)save->AtkValuesCount, 96); i++)
                    {
                        ref var value = ref save->AtkValues[i];
                        detail.Append($"v{i}({value.Type})=");
                        if (value.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString)
                            detail.Append(value.String.ToString());
                        else if (value.Type is AtkValueType.Int or AtkValueType.UInt or AtkValueType.Bool)
                            detail.Append(value.UInt);
                        detail.Append("; ");
                    }
                int budget = 180;
                for (int i = 0; i < Math.Min((int)save->UldManager.NodeListCount, 64); i++)
                    AppendSaveNodeDiagnostic(save->UldManager.NodeList[i], $"n{i}", 0, detail, ref budget);
                Service.Log.Info($"[DeepDungeonUi.SaveRead] {detail}");
            }
            catch (Exception ex) { Service.Log.Info($"[DeepDungeonUi.SaveRead] reason={reason}; diagnostic-error={ex.Message}"); }
            return false;
        }

        private static unsafe void AppendSaveNodeDiagnostic(AtkResNode* node, string path, int depth,
            System.Text.StringBuilder detail, ref int budget)
        {
            if (node == null || budget-- <= 0) return;
            detail.Append($"{path}:id={node->NodeId},type={node->Type},enabled={(node->NodeFlags & NodeFlags.Enabled) != 0}");
            if (node->Type == NodeType.Text)
                detail.Append($",text='{((AtkTextNode*)node)->NodeText.ExtractText()}'");
            if ((int)node->Type >= 1000)
            {
                var component = node->GetAsAtkComponentNode()->Component;
                if (component != null)
                {
                    var kind = component->GetComponentType();
                    detail.Append($",component={kind},nodes={component->UldManager.NodeListCount}");
                    if (kind == ComponentType.List)
                    {
                        var list = (AtkComponentList*)component;
                        detail.Append($",count={list->GetItemCount()},pending={list->IsUpdatePending},renderers={list->AllocatedItemRendererListLength}");
                    }
                    detail.Append("; ");
                    if (depth < 3 && component->UldManager.NodeList != null)
                        for (int i = 0; i < Math.Min((int)component->UldManager.NodeListCount, 64); i++)
                            AppendSaveNodeDiagnostic(component->UldManager.NodeList[i], $"{path}/{i}", depth + 1, detail, ref budget);
                    return;
                }
            }
            detail.Append("; ");
        }

		public static unsafe bool TryFindSelectStringIndexContaining(string needle, out int index)
		{
			index = -1;
			try
			{
				if (!TryGetSelectString(out var sel) || !sel->IsAddonAndNodesReady()) return false;
				var addon = (AddonSelectString*)sel;
				var entryCount = addon->PopupMenu.PopupMenu.EntryCount;
				var atkValues = addon->AtkValues;
				
				for (var i = 0; i < entryCount; i++)
				{
					ref var atkValue = ref atkValues[i + 7];
					if (atkValue.Type == 0 || !atkValue.String.HasValue) continue;
					
					var text = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpanFromNullTerminated(atkValue.String);
					if (text.IsEmpty) continue;
					
					var textStr = System.Text.Encoding.UTF8.GetString(text);
					if (needle.All(char.IsDigit)
                        ? Regex.IsMatch(textStr, $"(?<![0-9]){Regex.Escape(needle)}(?![0-9])")
                        : textStr.Contains(needle, StringComparison.OrdinalIgnoreCase))
					{
						index = i;
						return true;
					}
				}
				return false;
			}
			catch { return false; }
		}

	}
}

