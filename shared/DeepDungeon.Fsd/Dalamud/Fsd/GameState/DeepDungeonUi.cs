using System;
using System.Linq;

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
		/// <param name="slotIndex">0 for slot 1, 1 for slot 2</param>
		public static unsafe bool ClickSaveSlotForEntry(int slotIndex)
		{
			try
			{
				var idx = slotIndex <= 0 ? 0 : 1;
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
		/// <param name="slotIndex">0 for slot 1, 1 for slot 2</param>
		public static unsafe bool ClickSaveSlotForDelete(int slotIndex)
		{
			try
			{
				var idx = slotIndex <= 0 ? 0 : 1;
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

		public static void CloseDeepDungeonEntryWindows()
		{
			TryCloseAddon("DeepDungeonSaveData");
			TryCloseAddon("DeepDungeonMenu");
			TryCloseAddon("ContentsFinderConfirm");
			TryCloseAddon("SelectYesno");
			TryCloseAddon("SelectString");
			TryCloseAddon("Talk");
			TryCloseAddon("EventTalk");
			TryCloseAddon("ContextIconMenu");
		}

		public static unsafe bool TryGetEmptySlotsFromDeepDungeonSaveData(out bool slot1Empty, out bool slot2Empty, bool log = true)
		{
			slot1Empty = false;
			slot2Empty = false;
			try
			{
				if (!TryGetAddon("DeepDungeonSaveData", out var save) || !save->IsAddonAndNodesReady())
					return false;

				var nodeA = save->UldManager.NodeList[2];
				
				var slot1Comp = GetChildNode(nodeA, 1);
				var slot1TextNode = GetChildNode(slot1Comp, 15);
				var slot1Text = GetTextNodeContent(slot1TextNode);
				
				var slot2Comp = GetChildNode(nodeA, 2);
				var slot2TextNode = GetChildNode(slot2Comp, 15);
				var slot2Text = GetTextNodeContent(slot2TextNode);
				
				if (log) try { Service.Log.Info($"[DeepDungeonUi] SaveData slot1 text: '{slot1Text}', slot2 text: '{slot2Text}'"); } catch { }
				
				slot1Empty = IsSlotTextEmpty(slot1Text);
				slot2Empty = IsSlotTextEmpty(slot2Text);
				
				if (log) try { Service.Log.Info($"[DeepDungeonUi] Detection result: slot1Empty={slot1Empty}, slot2Empty={slot2Empty}"); } catch { }
				return true;
			}
			catch { return false; }
		}
		
		private static unsafe string GetTextNodeContent(AtkResNode* node)
		{
			try
			{
				if (node == null) return string.Empty;
				var tn = node->GetAsAtkTextNode();
				if (tn == null) return string.Empty;
				return tn->NodeText.ToString();
			}
			catch { return string.Empty; }
		}
		
		private static bool IsSlotTextEmpty(string text)
		{
			if (string.IsNullOrWhiteSpace(text)) return true;
			foreach (var c in text)
			{
				if (char.IsDigit(c)) return false;
			}
			return true;
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
					if (textStr.Contains(needle, StringComparison.OrdinalIgnoreCase))
					{
						index = i;
						return true;
					}
				}
				return false;
			}
			catch { return false; }
		}

		private static unsafe AtkResNode* GetChildNode(AtkResNode* parent, int childIndex)
		{
			if (parent == null) return null;
			// Move into component if necessary
			if ((int)parent->Type >= 1000)
			{
				var comp = parent->GetAsAtkComponentNode()->Component;
				var uld = comp->UldManager;
				return uld.NodeList[childIndex];
			}
			// Otherwise, treat as container starting at ChildNode, traverse to index
			var child = parent->ChildNode;
			for (int i = 0; i < childIndex && child != null; i++)
			{
				child = child->PrevSiblingNode;
			}
			return child;
		}
	}
}

