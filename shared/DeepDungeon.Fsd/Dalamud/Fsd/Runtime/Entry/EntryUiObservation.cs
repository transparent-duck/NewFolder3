using DeepDungeon.Fsd.Core;
using global::Dalamud.Utility;
using global::Dalamud.Game.ClientState.Conditions;
using DeepDungeon.Fsd.Dalamud.GameState;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Extensions;
using OmenTools.Interop.Game.Helpers;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry;

internal sealed record EntryUiObservation(
    EntryUiKind Kind, string Addon, nint Address, ushort AddonId, string Revision,
    int Choice = -1, IReadOnlyList<SaveSlotSnapshot>? Slots = null, string Detail = "",
    bool QueuePending = false, bool QueueMatches = false, bool ConfirmDisabled = false, ContentsFinderQueueState? QueueState = null);

internal static unsafe class EntryUiReader
{
    private static readonly string[] Priority = ["SelectYesno", "ContentsFinderConfirm", "SelectString",
        "Talk", "EventTalk", "DeepDungeonSaveData", "DeepDungeonMenu"];

    internal static bool QueuePending(ContentsFinder* finder) =>
        Service.Condition[ConditionFlag.WaitingForDuty] || Service.Condition[ConditionFlag.WaitingForDutyFinder] ||
        (finder != null && finder->QueueInfo.QueueState is ContentsFinderQueueState.Pending or ContentsFinderQueueState.Queued
            or ContentsFinderQueueState.Ready or ContentsFinderQueueState.Accepted);

    public static EntryUiObservation Read(PtEntryText text, int floor)
    {
        var finder = ContentsFinder.Instance();
        ContentsFinderQueueState? queueState = finder == null ? null : finder->QueueInfo.QueueState;
        bool pending = QueuePending(finder), queueMatches = false;
        if (finder != null)
        {
            var queue = &finder->QueueInfo;
            queueMatches = queue->PoppedQueueEntry.ContentType == ContentsType.Regular &&
                text.MatchesDuty(queue->PoppedQueueEntry.Id, floor);
            if (!queueMatches && pending)
                foreach (var entry in queue->QueuedEntries)
                    if (entry.ContentType == ContentsType.Regular && text.MatchesDuty(entry.Id, floor)) queueMatches = true;
        }
        foreach (var name in Priority)
        {
            if (!TryGetActiveAddon(name, out var addon)) continue;
            if (!addon->IsAddonAndNodesReady())
                return Result(EntryUiKind.Incomplete, "not-ready");
            var strings = ReadStrings(addon);
            string fingerprint = string.Join("|", strings);
            var kind = EntryUiKind.Unknown;
            bool confirmDisabled = false;
            int choice = -1;
            IReadOnlyList<SaveSlotSnapshot>? slots = null;
            string detail = fingerprint;
            switch (name)
            {
                case "SelectYesno":
                    if (DeepDungeonUi.TryGetConfirmationPromptText(addon, out var prompt, out _))
                    {
                        fingerprint = PtEntryText.Normalize(prompt);
                        kind = fingerprint == text["ENTER_FIXED_Q"] ? EntryUiKind.FixedPartyConfirmation
                            : fingerprint == text["ENTER_SOLO"] ? EntryUiKind.SoloConfirmation : EntryUiKind.Unknown;
                        var yes = ((AddonSelectYesno*)addon)->YesButton;
                        confirmDisabled = yes != null && !yes->IsEnabled && kind != EntryUiKind.Unknown;
                        if (yes == null) kind = EntryUiKind.Incomplete;
                    }
                    break;
                case "SelectString":
                    if (!TryReadMenu(addon, out var labels, out _)) { kind = EntryUiKind.Incomplete; break; }
                    fingerprint += "|choices=" + string.Join("|", labels);
                    bool party = strings.Contains(text["ENTER_MENU_TITLE"]) &&
                        labels.Contains(text["ENTER_MENU_FIXED"]) && labels.Contains(text["ENTER_MENU_MATCHING"]) &&
                        labels.Contains(text["ENTER_MENU_BACK"]);
                    if (party)
                    {
                        choice = EntryMenuChoice.FindUnique(labels, text["ENTER_MENU_FIXED"]);
                        if (choice >= 0) kind = EntryUiKind.PartyChoice;
                    }
                    else if (strings.Contains(text["ENTER_WHICH_Q"]) && labels.All(text.IsKnownFloorLabel) && text.SupportsStartFloor(floor))
                    {
                        string target = text.FloorLabel(floor);
                        choice = EntryMenuChoice.FindUnique(labels, target);
                        if (choice >= 0) kind = EntryUiKind.FloorChoice;
                        else detail = "指定起始層選項不存在或不唯一。";
                    }
                    break;
                case "Talk":
                case "EventTalk":
                    if (text.MatchesSoloExplanation(strings))
                        kind = EntryUiKind.SoloExplanation;
                    break;
                case "ContentsFinderConfirm":
                    var confirm = (AddonContentsFinderConfirm*)addon;
                    var title = confirm->AtkTextNode230 == null ? "" : confirm->AtkTextNode230->NodeText.ExtractText();
                    fingerprint += "|" + PtEntryText.Normalize(title);
                    if (text.MatchesDutyTitle(title, floor) && queueMatches)
                    {
                        kind = confirm->CommenceButton == null ? EntryUiKind.Incomplete : EntryUiKind.DutyConfirmation;
                        confirmDisabled = confirm->CommenceButton != null && !confirm->CommenceButton->IsEnabled;
                        if (!confirmDisabled && queueState != ContentsFinderQueueState.Ready)
                            kind = EntryUiKind.Incomplete;
                    }
                    break;
                case "DeepDungeonSaveData":
                    if (IsReadList(addon, text))
                    {
                        if (DeepDungeonUi.TryReadSaveSlots(out var read, out var error))
                        {
                            kind = EntryUiKind.ReadSaves;
                            slots = read;
                            fingerprint += "|" + string.Join(";", read);
                        }
                        else { kind = EntryUiKind.Incomplete; detail = error; }
                    }
                    break;
                case "DeepDungeonMenu":
                    if (IntAt(addon, 3) == text.DungeonId && TryReadMenu(addon, out var menuLabels, out _))
                    {
                        fingerprint += "|choices=" + string.Join("|", menuLabels);
                        choice = EntryMenuChoice.FindUnique(menuLabels, text["START_MENU_ENTER"]);
                        if (choice >= 0) kind = EntryUiKind.EntranceMenu;
                    }
                    break;
            }
            return Result(kind, fingerprint, choice, slots, detail, confirmDisabled);

            EntryUiObservation Result(EntryUiKind k, string key, int index = -1,
                IReadOnlyList<SaveSlotSnapshot>? rows = null, string why = "", bool disabled = false) =>
                new(disabled ? EntryUiKind.Incomplete : k, name, (nint)addon, addon->Id, $"{name}:{(nint)addon:X}:{addon->Id}:{k}:{key}",
                    index, rows, why, pending, queueMatches, disabled, queueState);
        }
        return new(EntryUiKind.None, "", 0, 0, "none", QueuePending: pending, QueueMatches: queueMatches, QueueState: queueState);
    }

    public static bool IsReadList(AtkUnitBase* addon, PtEntryText text) =>
        addon != null && addon->IsAddonAndNodesReady() && IntAt(addon, 11) == text.DungeonId && StringAt(addon, 10) == text.ReadSaveTitle;

    private static bool TryGetActiveAddon(string name, out AtkUnitBase* addon)
    {
        if (name == "SelectString")
        {
            for (int index = 5; index >= 1; index--)
            {
                nint ptr = Service.GameGui.GetAddonByName(name, index).Address;
                if (ptr != nint.Zero)
                {
                    addon = (AtkUnitBase*)ptr;
                    if (addon->IsVisible) return true;
                }
            }
            addon = null;
            return false;
        }
        return AddonHelper.TryGetByName<AtkUnitBase>(name, out addon) && addon->IsVisible;
    }

    public static bool TryReadMenu(AtkUnitBase* addon, out string[] labels, out AtkComponentList* list)
    {
        labels = [];
        list = null;
        if (addon == null || !addon->IsAddonAndNodesReady() || addon->UldManager.NodeList == null) return false;
        for (int i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null) continue;
            var candidate = node->GetAsAtkComponentList();
            if (candidate == null) continue;
            if (list != null) return false;
            list = candidate;
        }
        if (list == null || list->IsUpdatePending) return false;
        int count = list->GetItemCount();
        if (count is < 1 or > 64) return false;
        labels = new string[count];
        for (int i = 0; i < count; i++)
        {
            var label = list->GetItemLabel(i);
            if (!label.HasValue) return false;
            labels[i] = PtEntryText.Normalize(label.ExtractText());
            if (labels[i].Length == 0) return false;
        }
        return true;
    }

    public static bool ActivateChoice(AtkUnitBase* addon, string expected)
    {
        if (!TryReadMenu(addon, out var labels, out var list)) return false;
        int index = EntryMenuChoice.FindUnique(labels, expected);
        if (index < 0 || list->GetItemDisabledState(index)) return false;
        list->DispatchItemEvent(index, AtkEventType.ListItemClick);
        return true;
    }

    public static bool ActivateSelectStringChoice(AtkUnitBase* addon, string expected)
    {
        if (!TryReadMenu(addon, out var labels, out var list)) return false;
        int index = EntryMenuChoice.FindUnique(labels, expected);
        if (index < 0 || list->GetItemDisabledState(index)) return false;
        return GameState.DeepDungeonUi.Fire(addon, index);
    }

    public static string StringAt(AtkUnitBase* addon, int index)
    {
        if (addon == null || addon->AtkValues == null || index < 0 || index >= addon->AtkValuesCount) return "";
        ref var value = ref addon->AtkValues[index];
        return value.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString && value.String.HasValue
            ? PtEntryText.Normalize(value.String.ExtractText()) : "";
    }

    public static int? IntAt(AtkUnitBase* addon, int index)
    {
        if (addon == null || addon->AtkValues == null || index < 0 || index >= addon->AtkValuesCount) return null;
        ref var value = ref addon->AtkValues[index];
        return value.Type is AtkValueType.Int or AtkValueType.UInt ? value.Int : null;
    }

    private static string[] ReadStrings(AtkUnitBase* addon)
    {
        var result = new List<string>();
        for (int i = 0; i < Math.Min((int)addon->AtkValuesCount, 96); i++)
        { var value = StringAt(addon, i); if (value.Length > 0) result.Add(value); }
        return result.ToArray();
    }
}
