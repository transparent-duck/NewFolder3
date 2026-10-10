using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Runtime;
using FFXIVClientStructs.FFXIV.Application.Network;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.Info.Game.Packets.Upstream;
using System.Numerics;

namespace NewFolder3;

internal sealed unsafe class YAxisAdjustController : IDisposable
{
    private readonly Configuration _configuration;
    private readonly IObjectTable _objects;
    private readonly IPartyList _party;
    private readonly IPluginLog _log;
    private Hook<ZoneClient.Delegates.SendPacket>? _hook;
    private long _lastPolicyRevision = -1;
    private bool _unavailable;
    private bool _disposed;
    private DateTime _lastPacketErrorAtUtc;
    private float _overlayOffset = float.NaN;
    private string _overlayLabel = string.Empty;

    public YAxisAdjustController(Configuration configuration, IObjectTable objects, IPartyList party, IPluginLog log)
    {
        _configuration = configuration;
        _objects = objects;
        _party = party;
        _log = log;
    }

    public void Update(in DeepDungeonStateSnapshot state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (state.IsValid && !state.IsTransitioning && state.Revision != _lastPolicyRevision)
        {
            _lastPolicyRevision = state.Revision;
            if (_configuration.AutoYAxisAdjustment &&
                DeepDungeonFloorClassifier.AutomaticYOffset(state) is float offset)
            {
                if (offset != 0f && _configuration.MovementYDisableInParty && _party.Length > 1)
                    offset = 0f;
                if (_configuration.MovementYSubtract != offset)
                    SetOffset(offset);
            }
        }
        UpdateHookState();
    }

    private void UpdateHookState()
    {
        float delta = _configuration.MovementYSubtract;
        if (!float.IsFinite(delta) || Math.Abs(delta) <= 0.0001f)
        {
            _hook?.Dispose();
            _hook = null;
            _unavailable = false;
            return;
        }
        if (_hook != null || _unavailable)
            return;

        try
        {
            if (UpstreamOpcode.PositionUpdateOpcode <= 0 || UpstreamOpcode.PositionUpdateInstanceOpcode <= 0 ||
                UpstreamOpcode.PositionUpdateOpcode == UpstreamOpcode.PositionUpdateInstanceOpcode)
                throw new InvalidOperationException("Movement packet opcodes are unavailable.");
            _hook = DService.Instance().Hook.HookFromMemberFunction(
                typeof(ZoneClient.MemberFunctionPointers), "SendPacket",
                (ZoneClient.Delegates.SendPacket)OnSendPacket);
            _hook.Enable();
            if (!_hook.IsEnabled)
                throw new InvalidOperationException("Y-axis packet hook did not enable.");
        }
        catch (Exception error)
        {
            _hook?.Dispose();
            _hook = null;
            _unavailable = true;
            _log.Warning($"[YAxis] Could not enable movement packet adjustment: {error.Message}");
        }
    }

    private bool OnSendPacket(ZoneClient* client, nint packet, uint a3, uint a4, bool prioritize)
    {
        try
        {
            if (packet != 0)
            {
                int opcode = *(ushort*)packet;
                if ((opcode == UpstreamOpcode.PositionUpdateOpcode || opcode == UpstreamOpcode.PositionUpdateInstanceOpcode) &&
                    _objects.LocalPlayer is { } player)
                    YAxisPacketAdjustment.TryAdjust(packet, opcode == UpstreamOpcode.PositionUpdateInstanceOpcode,
                        _configuration.MovementYSubtract, player.Position);
            }
        }
        catch (Exception error)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastPacketErrorAtUtc >= TimeSpan.FromSeconds(2))
            {
                _lastPacketErrorAtUtc = now;
                _log.Warning($"[YAxis] Movement packet adjustment failed: {error.Message}");
            }
        }
        return _hook!.Original(client, packet, a3, a4, prioritize);
    }

    private void SetOffset(float offset)
    {
        _configuration.MovementYSubtract = Math.Clamp(offset, -15f, 15f);
        _configuration.Save(_configuration.Fsd);
    }

    public void DrawOverlay()
    {
        float offset = _configuration.MovementYSubtract;
        if (_disposed || !float.IsFinite(offset) || Math.Abs(offset) <= 0.0001f ||
            _objects.LocalPlayer is not { } player)
            return;

        offset = Math.Clamp(offset, -15f, 15f);
        if (_overlayOffset != offset)
        {
            _overlayOffset = offset;
            _overlayLabel = $"Y offset\n{offset:+0.0;-0.0}m";
        }

        WorldDrawHelper.DrawWorldLabel(
            ImGui.GetBackgroundDrawList(),
            player.Position + new Vector3(0f, 2.2f, 0f),
            _overlayLabel,
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.95f, 0.5f, 1f)),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.72f)),
            borderColor: ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.75f, 0.25f, 0.95f)),
            anchorY: 1.0f,
            rounding: 2f);
    }

    public void DrawSettings()
    {
        ImGui.Text("座標Y軸偏移");
        float offset = _configuration.MovementYSubtract;
        if (ImGui.SliderFloat("米##YAxis", ref offset, -15f, 15f, "%.1f"))
            SetOffset(offset);
        if (ImGui.Button("設定為0")) SetOffset(0f);
        ImGui.SameLine();
        if (ImGui.Button("設定為-7")) SetOffset(-7f);
        ImGui.SameLine();
        if (ImGui.Button("設定為+11")) SetOffset(11f);
        ImGui.Spacing();

        bool automatic = _configuration.AutoYAxisAdjustment;
        if (ImGui.Checkbox("於深宮自動設定偏移", ref automatic))
        {
            _configuration.AutoYAxisAdjustment = automatic;
            _lastPolicyRevision = -1;
            _configuration.Save(_configuration.Fsd);
        }
        ImGui.BeginDisabled(!automatic);
        bool disableInParty = _configuration.MovementYDisableInParty;
        if (ImGui.Checkbox("在隊伍內不自動偏移", ref disableInParty))
        {
            _configuration.MovementYDisableInParty = disableInParty;
            _lastPolicyRevision = -1;
            _configuration.Save(_configuration.Fsd);
        }
        ImGui.EndDisabled();
        ImGui.Spacing();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _hook?.Dispose();
        _hook = null;
        _disposed = true;
    }
}
