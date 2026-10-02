using System;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using global::Dalamud.Game.ClientState.Objects.Types;
using global::Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using OmenTools.Interop.Game;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal unsafe sealed class Pt50QuicksandRecoveryHelper(Func<bool, bool> setMovementOverride) : IDisposable
{
    private readonly MovementInputController _movement = new();
    private ICallGateSubscriber<Vector3, Vector3, bool>? _pathSafe;
    private bool _ownsMovement;
    private Vector3 _destination;
    private DateTime _nextDiagnosticAt;
    public bool IsActive => _ownsMovement;

    public void Update(InstanceContentDeepDungeon* dd)
    {
        var player = Service.LocalPlayer;
        if (dd == null || dd->DeepDungeonId != 4 || dd->Floor != 50 || player == null || player.IsDead)
        { Reset(); return; }
        float sinking = 0;
        foreach (var status in player.StatusList)
            if (status.StatusId == 567) { sinking = status.RemainingTime; break; }
        if (sinking <= 0) { Reset(); return; }
        float? wind = null;
        foreach (var obj in Service.GameObjects)
            if (obj is IBattleChara { IsCasting: true, CastActionId: 43538 } caster)
                wind = caster.TotalCastTime - caster.CurrentCastTime;
        if (!_ownsMovement)
        {
            if (!Pt50QuicksandRecoveryPolicy.ShouldRecover(sinking, wind)) return;
            if (!setMovementOverride(true))
            {
                Log($"recovery denied: external disable failed; sinking={sinking:F2}s");
                return;
            }
            _ownsMovement = true;
            byte safeMask = 0;
            try
            {
                _pathSafe ??= Service.PluginInterface.GetIpcSubscriber<Vector3, Vector3, bool>("BossMod.Hints.IsDashSafe");
                if (_pathSafe.HasFunction)
                {
                    var rocks = Pt50QuicksandRecoveryPolicy.RockInteriors;
                    for (int i = 0; i < rocks.Length; i++)
                    {
                        var candidate = new Vector3(rocks[i].X, player.Position.Y, rocks[i].Y);
                        // Avoid accepting a distant safe point which cannot beat the sink timer.
                        if (Vector3.Distance(player.Position, candidate) < Math.Max(0, sinking - 0.6f) * 6f &&
                            _pathSafe.InvokeFunc(player.Position, candidate)) safeMask |= (byte)(1 << i);
                    }
                }
            }
            catch { /* Optional hints cannot deny an emergency return to solid ground. */ }
            var rock = Pt50QuicksandRecoveryPolicy.NearestRock(new(player.Position.X, player.Position.Z), safeMask);
            _destination = new(rock.X, player.Position.Y, rock.Y);
            Service.Log.Info($"[FloorPhase] PT50 sinking recovery acquired: remaining={sinking:F2}s, wind={wind}, from={player.Position}, rock={_destination}, safeMask={safeMask}");
        }
        // Keep one destination until the native sinking status disappears. Do not chase changing BMR targets.
        _movement.DesiredPosition = _destination;
        _movement.IsAutoMove = true;
        if (!_movement.Enabled) _movement.Enabled = true;
        Log($"recovery active: sinking={sinking:F2}s, wind={wind}, position={player.Position}, rock={_destination}");
    }

    private void Log(string message)
    {
        var now = DateTime.UtcNow;
        if (now < _nextDiagnosticAt) return;
        _nextDiagnosticAt = now.AddSeconds(1);
        Service.Log.Info($"[FloorPhase] PT50 {message}");
    }

    public void Reset()
    {
        if (_movement.Enabled) _movement.Enabled = false;
        _movement.IsAutoMove = false;
        if (!_ownsMovement) return;
        _ownsMovement = false;
        setMovementOverride(false);
        Service.Log.Info("[FloorPhase] PT50 sinking recovery released; returning movement to mode policy");
    }
    public void Dispose() { Reset(); _movement.Dispose(); }
}
