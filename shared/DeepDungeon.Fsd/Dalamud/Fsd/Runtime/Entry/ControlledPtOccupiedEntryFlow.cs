using System.Numerics;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry;

internal sealed class ControlledPtOccupiedEntryFlow
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(350);
    private readonly ControlledPtSurveySession _session;
    private RunContext? _context;
    private NavigationHelper? _navigation;
    private DateTime _nextActionAt;
    private bool _openedMenu;
    private bool _clickedOccupiedSlot;

    public ControlledPtOccupiedEntryFlow(ControlledPtSurveySession session)
    {
        _session = session;
    }

    public void Prepare(RunContext context)
    {
        _context = context;
        _navigation = new NavigationHelper(context.Navigator);
        _nextActionAt = DateTime.MinValue;
        _openedMenu = false;
        _clickedOccupiedSlot = false;
        context.StatusLine = "Controlled PT 21-30: opening prepared save";
    }

    public unsafe bool Update(IFramework framework)
    {
        var context = _context;
        if (context == null)
            return false;

        if (context.Duty.IsInDuty)
            return true;
        if (context.StatusIsError || _session.Fatal)
            return true;
        if (DateTime.UtcNow < _nextActionAt)
            return false;

        if (DeepDungeonUi.TryGetSelectYesNo(out _))
            return Fail("Controlled PT entry stopped: an unowned confirmation prompt was open.");
        if (DeepDungeonUi.TryGetSelectString(out _))
            return Fail("Controlled PT entry stopped: save creation/floorset selection UI was reached instead of prepared-save entry.");

        if (DeepDungeonUi.TryGetAddon("ContentsFinderConfirm", out _))
        {
            if (DeepDungeonUi.IsAddonOpen("DeepDungeonSaveData"))
            {
                DeepDungeonUi.TryCloseAddon("DeepDungeonSaveData");
                Delay();
                return false;
            }

            if (!DeepDungeonUi.ClickCommenceButton())
                return Fail("Controlled PT entry could not click the duty Commence button.");
            context.StatusLine = "Controlled PT 21-30: commencing prepared save";
            Delay();
            return false;
        }

        if (DeepDungeonUi.IsAddonOpen("DeepDungeonSaveData"))
        {
            if (_clickedOccupiedSlot)
            {
                context.StatusLine = "Controlled PT 21-30: waiting for prepared-save duty entry";
                Delay();
                return false;
            }

            if (!DeepDungeonUi.TryGetEmptySlotsFromDeepDungeonSaveData(out bool slot1Empty, out bool slot2Empty))
                return Fail("Controlled PT entry cannot prove the save-slot state.");

            var decision = _session.ObserveAndPinSaveSlots(slot1Empty, slot2Empty);
            if (!decision.IsValid)
                return Fail(decision.Error);

            // Re-read immediately before the only slot callback. A slot flip must stop before selection.
            if (!DeepDungeonUi.TryGetEmptySlotsFromDeepDungeonSaveData(out slot1Empty, out slot2Empty, log: false))
                return Fail("Controlled PT entry lost save-slot visibility before selection.");
            decision = _session.ObserveAndPinSaveSlots(slot1Empty, slot2Empty);
            if (!decision.IsValid)
                return Fail(decision.Error);

            if (!context.SaveSlots.TrySelectSlot(decision.OccupiedSlotIndex))
                return Fail($"Controlled PT entry could not select occupied save slot {decision.OccupiedSlotIndex + 1}.");

            _clickedOccupiedSlot = true;
            context.StatusLine = $"Controlled PT 21-30: selected pinned occupied slot {decision.OccupiedSlotIndex + 1}";
            Delay();
            return false;
        }

        // Selecting the occupied slot submits the duty application. The save panel may
        // disappear before DutyState observes the new territory; do not fall through
        // to Talk/NPC interaction and resubmit callbacks during that handoff.
        if (_clickedOccupiedSlot)
        {
            context.StatusLine = "Controlled PT 21-30: waiting for prepared-save duty entry";
            Delay();
            return false;
        }

        if (DeepDungeonUi.IsAddonOpen("DeepDungeonMenu"))
        {
            if (!_openedMenu)
            {
                if (!DeepDungeonUi.EnterDeepDungeonViaAgent())
                    return Fail("Controlled PT entry could not open the save-slot panel.");
                _openedMenu = true;
            }
            Delay();
            return false;
        }

        if (DeepDungeonUi.TryGetTalk(out var talk))
        {
            DeepDungeonUi.Fire(talk, 0);
            Delay();
            return false;
        }

        var npc = NpcInteractionGuard.FindByBaseId(DungeonCatalog.PilgrimsTraverse.NpcDataId);
        var player = Service.LocalPlayer;
        if (npc == null || player == null)
        {
            context.StatusLine = "Controlled PT 21-30: waiting for entry NPC";
            Delay();
            return false;
        }

        float distance = Vector3.Distance(player.Position, npc.Position);
        if (distance > NpcInteractionGuard.MaxInteractDistance)
        {
            var state = _navigation?.Navigate(
                npc.Position,
                player.Position,
                NpcInteractionGuard.MaxInteractDistance - 0.4f) ?? NavigationState.Failed;
            if (state is NavigationState.Failed or NavigationState.StuckGiveUp)
                return Fail("Controlled PT entry navigation to the NPC failed.");
            context.StatusLine = $"Controlled PT 21-30: moving to entry NPC ({distance:F1}m)";
            Delay();
            return false;
        }

        _navigation?.Cancel();
        if (!NpcInteractionGuard.TryInteract(
                DungeonCatalog.PilgrimsTraverse.NpcDataId,
                DungeonCatalog.PilgrimsTraverse.Name,
                out string status))
        {
            context.StatusLine = status;
            Delay();
            return false;
        }

        context.StatusLine = status;
        Delay();
        return false;
    }

    public void Reset()
    {
        try { _navigation?.Cancel(); } catch { }
        _navigation = null;
        _context = null;
    }

    private bool Fail(string reason)
    {
        _session.Fail(reason);
        if (_context != null)
        {
            _context.StatusLine = reason;
            _context.StatusIsError = true;
        }
        DeepDungeonUi.TryCloseAddon("SelectYesno");
        return true;
    }

    private void Delay()
    {
        _nextActionAt = DateTime.UtcNow.Add(RetryInterval);
    }
}
