using System.Numerics;
using System.Text.Json;
using global::Dalamud.Bindings.ImGui;
using global::Dalamud.Game.ClientState.Objects.Types;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

internal sealed class LosDebugOverlay
{
    private long _nextSample;
    private ulong _displayId;
    private string? _label;
    private (ulong Player, byte Type, uint Action, ulong Target)? _lastCast;
    private float _lastElapsed;

    public void Update()
    {
        var player = Service.LocalPlayer;
        var targets = Service.TargetManager;
        var selected = targets.Target;
        var hovered = targets.MouseOverTarget ?? targets.MouseOverNameplateTarget;
        var display = hovered ?? selected;
        if (player == null || display == null)
        {
            _label = null;
            _displayId = 0;
        }
        else if (display.GameObjectId != _displayId || Environment.TickCount64 >= _nextSample)
        {
            _displayId = display.GameObjectId;
            _nextSample = Environment.TickCount64 + 80;
            var sight = CombatPositionNavigation.SampleSight(player.Position, display.Position);
            _label = $"{(hovered != null ? "指向" : "選取")}　{display.Name.TextValue}\n" +
                $"一般　Y {sight.Origin.Y:F2}　{Describe(sight.Normal)}" +
                (sight.HasOffset
                    ? $"\n{FormattableString.Invariant($"{sight.YOffset:+0.##;-0.##;0}")}　Y {sight.OffsetOrigin.Y:F2}　{Describe(sight.Offset)}"
                    : string.Empty);
        }

        if (player == null || !player.IsCasting)
        {
            _lastCast = null;
            return;
        }
        var cast = (player.GameObjectId, player.CastActionType, player.CastActionId, player.CastTargetObjectId);
        float elapsed = player.CurrentCastTime;
        bool started = _lastCast != cast || elapsed + 0.02f < _lastElapsed;
        _lastCast = cast;
        _lastElapsed = elapsed;
        if (!started) return;

        var id = player.CastTargetObjectId;
        IGameObject? castTarget = id is 0 or 0xE0000000 ? null : Service.GameObjects.SearchById(id);
        if (castTarget == null && id is > 0 and <= uint.MaxValue && id != 0xE0000000)
            castTarget = Service.GameObjects.SearchByEntityId((uint)id);
        var position = player.Position;
        CombatSightSample? sample = castTarget == null ? null :
            CombatPositionNavigation.SampleSight(position, castTarget.Position);
        Service.Log.Information($"[FSD.LOS.CastStart] {JsonSerializer.Serialize(new
        {
            territory = Service.ClientState.TerritoryType, actionType = cast.CastActionType,
            actionId = cast.CastActionId, castTargetId = id, elapsed,
            player = Actor(player), castTarget = Actor(castTarget), selected = Actor(selected), hover = Actor(hovered),
            rays = sample is { } s ? new
            {
                yOffset = s.YOffset,
                origin = Point(s.Origin),
                offsetOrigin = s.HasOffset ? Point(s.OffsetOrigin) : null,
                end = Point(s.End),
                normal = s.Normal.ToString(),
                offset = s.HasOffset ? s.Offset.ToString() : null,
                normalHit = s.Normal == CombatSight.Blocked ? Point(s.NormalHit) : null,
                offsetHit = s.HasOffset && s.Offset == CombatSight.Blocked ? Point(s.OffsetHit) : null
            } : null
        })}");
    }

    public void Draw()
    {
        if (_label == null || Service.LocalPlayer == null) return;
        var mouse = ImGui.GetMousePos();
        var viewport = ImGui.GetMainViewport();
        var size = ImGui.CalcTextSize(_label) + 2 * ImGui.GetStyle().WindowPadding;
        var min = viewport.WorkPos;
        var max = Vector2.Max(min, min + viewport.WorkSize - size);
        ImGui.SetNextWindowPos(Vector2.Clamp(mouse + new Vector2(16, 22), min, max));
        ImGui.SetNextWindowBgAlpha(0.88f);
        bool visible = ImGui.Begin("###FsdLosHover", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing);
        if (visible) ImGui.TextUnformatted(_label);
        ImGui.End();
    }

    private static string Describe(CombatSight sight) => sight switch
    {
        CombatSight.Clear => "通過",
        CombatSight.Blocked => "阻擋",
        _ => "未知"
    };
    private static object Point(Vector3 p) => new { x = p.X, y = p.Y, z = p.Z };
    private static object? Actor(IGameObject? actor) =>
        actor == null ? null : new { id = actor.GameObjectId, position = Point(actor.Position) };
}
