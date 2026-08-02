using System.Numerics;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry;

internal sealed class ControlledPtSaveVerificationFlow
{
    private readonly ControlledPtSurveySession _session;
    private RunContext? _context;
    private NavigationHelper? _navigation;
    private DateTime _nextActionAt;
    private bool _openedMenu;

    public ControlledPtSaveVerificationFlow(ControlledPtSurveySession session)
    {
        _session = session;
    }

    public void Prepare(RunContext context)
    {
        _context = context;
        _navigation = new NavigationHelper(context.Navigator);
        _nextActionAt = DateTime.MinValue;
        _openedMenu = false;
        context.StatusLine = "Controlled PT 21-30: verifying reusable save";
    }

    public unsafe bool Update(IFramework framework)
    {
        var context = _context;
        if (context == null || context.StatusIsError)
            return true;
        if (DateTime.UtcNow < _nextActionAt)
            return false;

        if (DeepDungeonUi.TryGetSelectYesNo(out _))
            return Fail("Controlled PT save verification stopped: an unowned confirmation prompt was open.");
        if (DeepDungeonUi.TryGetSelectString(out _))
            return Fail("Controlled PT save verification reached an unexpected selection prompt.");

        if (DeepDungeonUi.IsAddonOpen("DeepDungeonSaveData"))
        {
            if (!DeepDungeonUi.TryGetEmptySlotsFromDeepDungeonSaveData(out bool slot1Empty, out bool slot2Empty))
                return Fail("Controlled PT save verification cannot read the save slots.");
            var decision = _session.ObserveAndPinSaveSlots(slot1Empty, slot2Empty);
            if (!decision.IsValid)
                return Fail(decision.Error);

            DeepDungeonUi.TryCloseAddon("DeepDungeonSaveData");
            DeepDungeonUi.TryCloseAddon("DeepDungeonMenu");
            context.StatusLine = $"Controlled PT 21-30: pinned save slot {decision.OccupiedSlotIndex + 1} preserved";
            return true;
        }

        if (DeepDungeonUi.IsAddonOpen("DeepDungeonMenu"))
        {
            if (!_openedMenu)
            {
                if (!DeepDungeonUi.EnterDeepDungeonViaAgent())
                    return Fail("Controlled PT save verification could not open save data.");
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
            context.StatusLine = "Controlled PT save verification: waiting for entry NPC";
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
                return Fail("Controlled PT save verification navigation failed.");
            Delay();
            return false;
        }

        _navigation?.Cancel();
        NpcInteractionGuard.TryInteract(
            DungeonCatalog.PilgrimsTraverse.NpcDataId,
            DungeonCatalog.PilgrimsTraverse.Name,
            out string status);
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

    private void Delay() => _nextActionAt = DateTime.UtcNow.AddMilliseconds(350);
}
