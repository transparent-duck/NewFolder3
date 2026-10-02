using System.Numerics;
using DeepDungeon.Fsd.Core;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry;

internal sealed class PreparedSaveFlow
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(350);
    private readonly PreparedSaveBinding _session;
    private RunContext? _context;
    private NavigationHelper? _navigation;
    private DateTime _nextActionAt;
    private bool _openedMenu;
    private readonly bool _verifyOnly;
    private DateTime _started;
    public int SourceFloor => _session.Pinned?.StartFloor ?? 0;
    public int SourceSlot => _session.Pinned?.Index ?? -1;
    private bool _clickedOccupiedSlot;
    private bool _waitingForNavigation;

    public PreparedSaveFlow(PreparedSaveBinding session, bool verifyOnly = false)
    {
        _session = session;
        _verifyOnly = verifyOnly;
    }

    public void Prepare(RunContext context)
    {
        _context = context;
        _navigation = new NavigationHelper(context.Navigator);
        _nextActionAt = DateTime.MinValue;
        _started = DateTime.UtcNow;
        _openedMenu = false;
        _clickedOccupiedSlot = false;
        context.StatusLine = "PT：讀取唯一來源存檔。";
    }

    public unsafe bool Update(IFramework framework)
    {
        var context = _context;
        if (context == null)
            return false;

        if (context.Duty.IsInDuty)
            return true;
        if (context.StatusIsError)
            return true;
        if (DateTime.UtcNow - _started > TimeSpan.FromMinutes(2))
            return Fail("來源存檔流程逾時；已停止。" );
        if (DateTime.UtcNow < _nextActionAt)
            return false;

        if (DeepDungeonUi.TryGetSelectYesNo(out _))
            return Fail("來源存檔流程遇到非本次操作的確認視窗；已停止。");
        if (DeepDungeonUi.TryGetSelectString(out _))
            return Fail("來源存檔流程進入了新建／選擇起點視窗；已停止。");

        if (DeepDungeonUi.TryGetAddon("ContentsFinderConfirm", out _))
        {
            if (_verifyOnly || !_clickedOccupiedSlot) return Fail("出現不屬於本次存檔入場的任務確認視窗；停止。");
            if (DeepDungeonUi.IsAddonOpen("DeepDungeonSaveData"))
            {
                DeepDungeonUi.TryCloseAddon("DeepDungeonSaveData");
                Delay();
                return false;
            }

            if (!DeepDungeonUi.ClickCommenceButton())
                return Fail("無法確認來源存檔的任務入場；已停止。");
            context.StatusLine = "PT：確認來源存檔入場。";
            Delay();
            return false;
        }

        if (DeepDungeonUi.IsAddonOpen("DeepDungeonSaveData"))
        {
            if (_clickedOccupiedSlot)
            {
                context.StatusLine = "PT：等待來源存檔入場。";
                Delay();
                return false;
            }

            if (!DeepDungeonUi.TryReadSaveSlots(out var slots, out string readError))
                return WaitForCompleteList(readError);
            if (!_session.Observe(slots, out var source, out string sourceError))
                return Fail(sourceError);
            context.StatusLine = _session.Description;
            context.PreparedSaveDescription = _session.Description;
            if (_verifyOnly)
            {
                DeepDungeonUi.TryCloseAddon("DeepDungeonSaveData");
                DeepDungeonUi.TryCloseAddon("DeepDungeonMenu");
                return true;
            }
            // Re-read the complete list immediately before the only selection callback.
            if (!DeepDungeonUi.TryReadSaveSlots(out slots, out readError)) return WaitForCompleteList(readError);
            if (!_session.Observe(slots, out source, out sourceError)) return Fail(sourceError);
            if (!context.SaveSlots.TrySelectSlot(source.Index))
                return Fail("無法選取唯一來源存檔。");

            _clickedOccupiedSlot = true;
            context.StatusLine = $"PT：已選取綁定的存檔 {source.Index + 1}。";
            Delay();
            return false;
        }

        // Selecting the occupied slot submits the duty application. The save panel may
        // disappear before DutyState observes the new territory; do not fall through
        // to Talk/NPC interaction and resubmit callbacks during that handoff.
        if (_clickedOccupiedSlot)
        {
            context.StatusLine = "PT：等待來源存檔入場。";
            Delay();
            return false;
        }

        if (DeepDungeonUi.IsAddonOpen("DeepDungeonMenu"))
        {
            if (!_openedMenu)
            {
                if (!DeepDungeonUi.EnterDeepDungeonViaAgent())
                    return Fail("無法開啟存檔列表；已停止。");
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
            context.StatusLine = "PT：等待入口 NPC。";
            Delay();
            return false;
        }

        float distance = Vector3.Distance(player.Position, npc.Position);
        if (distance > NpcInteractionGuard.MaxInteractDistance)
        {
            if (!moveHelper.VNav.NavmeshReady())
            {
                context.StatusLine = "PT：等待入口地圖導航載入。";
                if (!_waitingForNavigation)
                    Service.Log.Info("[PreparedSaveFlow] Waiting for overworld navmesh before NPC navigation");
                _waitingForNavigation = true;
                Delay();
                return false;
            }
            if (_waitingForNavigation)
            {
                Service.Log.Info("[PreparedSaveFlow] Overworld navmesh ready; resuming NPC navigation");
                _waitingForNavigation = false;
            }
            var state = _navigation?.Navigate(
                npc.Position,
                player.Position,
                NpcInteractionGuard.MaxInteractDistance - 0.4f) ?? NavigationState.Failed;
            if (state is NavigationState.Failed or NavigationState.StuckGiveUp)
                return Fail("前往入口 NPC 的導航失敗；已停止。");
            context.StatusLine = $"PT：前往入口 NPC（{distance:F1}m）。";
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
        if (_context != null)
        {
            _context.StatusLine = reason;
            _context.StatusIsError = true;
        }
        DeepDungeonUi.TryCloseAddon("SelectYesno");
        return true;
    }

    private bool WaitForCompleteList(string reason)
    {
        if (_context != null) _context.StatusLine = "等待完整存檔列表：" + reason;
        Delay();
        return false;
    }

    private void Delay()
    {
        _nextActionAt = DateTime.UtcNow.Add(RetryInterval);
    }
}
