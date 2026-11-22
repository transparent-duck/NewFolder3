using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Core;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Extensions;
using OmenTools.Interop.Game.Helpers;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry;

// Only runs at the start of an NPC operation. Observe a close on a later update
// before touching the parent, since cancelling a child may replace the parent.
internal sealed unsafe class EntranceWindowCleanup
{
    private static readonly string[] Windows = ["ContextIconMenu", "SelectYesno", "SelectString",
        "Talk", "EventTalk", "DeepDungeonSaveData", "DeepDungeonMenu"];
    private bool _complete;
    private long _nextRead;
    private (string Name, nint Address, ushort Id) _closing;
    private long _closeSentAt;

    public bool Ready(RunContext context)
    {
        if (_complete) return true;
        long now = Environment.TickCount64;
        if (now < _nextRead || DutyState.IsClientTransitioning || context.Duty.IsTransitioning || context.Duty.IsInDuty)
            return false;
        _nextRead = now + 150;
        if (EntryUiReader.QueuePending(ContentsFinder.Instance()) || Visible("ContentsFinderConfirm") != null)
        {
            context.StatusLine = "已有任務申請，等待入場。";
            return false;
        }

        foreach (var name in Windows)
        {
            var addon = Visible(name);
            if (addon == null) continue;
            var key = (name, (nint)addon, addon->Id);
            if (!addon->IsAddonAndNodesReady()) return false;
            if (key == _closing && now - _closeSentAt < InteractionTiming.GetRetryMilliseconds())
                return false;
            if (!IsEntranceWindow(name, addon))
            {
                context.StatusLine = $"等待其他對話結束：{name}。";
                return false;
            }

            // Use the negative response on a confirmation. Never confirm a stale delete.
            if (name == "SelectYesno")
            {
                if (!DeepDungeonUi.Fire(addon, 1))
                { _nextRead = now + InteractionTiming.GetRetryMilliseconds(); return false; }
            }
            else if (name is "SelectString" or "ContextIconMenu")
                addon->FireCloseCallback();
            else if (!addon->Close(true))
            { _nextRead = now + InteractionTiming.GetRetryMilliseconds(); return false; }
            _closing = key;
            _closeSentAt = now;
            context.StatusLine = $"等待入口視窗關閉：{name}。";
            return false;
        }
        _complete = true;
        return true;
    }

    private static bool IsEntranceWindow(string name, AtkUnitBase* addon)
    {
        if (name is "DeepDungeonSaveData" or "DeepDungeonMenu") return true;
        if (IsParent(addon, Visible("DeepDungeonSaveData")) || IsParent(addon, Visible("DeepDungeonMenu")))
            return true;

        // Event dialogs can outlive their parent. Identify them by the same
        // localized text used for entry and deletion before cancelling them.
        if (name == "SelectYesno" && DeepDungeonUi.IsDeleteSaveConfirmationPrompt(addon, out _))
            return true;
        foreach (var text in PtEntryText.AllSupported)
        {
            if (name == "SelectYesno" && DeepDungeonUi.TryGetConfirmationPromptText(addon, out var prompt, out _) &&
                (PtEntryText.Normalize(prompt) == text["ENTER_FIXED_Q"] || PtEntryText.Normalize(prompt) == text["ENTER_SOLO"]))
                return true;
            for (int i = 0; i < addon->AtkValuesCount; i++)
            {
                string value = EntryUiReader.StringAt(addon, i);
                if (name == "SelectString" && (value == text["ENTER_MENU_TITLE"] || value == text["ENTER_WHICH_Q"])) return true;
                if (name is "Talk" or "EventTalk" && value == text.NpcName) return true;
            }
        }
        return false;
    }

    private static bool IsParent(AtkUnitBase* child, AtkUnitBase* parent) => parent != null && parent->Id != 0 &&
        (child->ParentId == parent->Id || child->BlockedParentId == parent->Id);

    private static AtkUnitBase* Visible(string name) =>
        AddonHelper.TryGetByName<AtkUnitBase>(name, out var addon) && addon->IsVisible ? addon : null;
}
