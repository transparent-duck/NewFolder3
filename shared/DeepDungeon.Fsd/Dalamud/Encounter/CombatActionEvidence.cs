using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Encounter;

/// <summary>Bounded native observations for the ordinary floor that owns combat movement.</summary>
internal sealed class CombatActionEvidence : IDisposable
{
    private const int ActionCapacity = 256;
    private const int EffectCapacity = 64;
    private readonly EncounterMechanicHost? _host;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly Queue<ActionCall> _calls = new(ActionCapacity);
    private readonly Queue<EffectReceipt> _effects = new(EffectCapacity);
    private readonly object _sync = new();
    private DateTime _lastDroppedCall;
    private DateTime _lastDroppedEffect;

    internal CombatActionEvidence(EncounterMechanicHost? host)
    {
        _host = host;
        _host?.AttachCombatActionEvidence(this);
    }

    internal readonly record struct ActionCall(
        DateTime At, bool GroundTargeted, ActionType Type, uint ActionId, ulong TargetId,
        ulong? HardTargetId, ActionManager.UseActionMode? Mode, bool BlockedByMechanic,
        bool ClientResult, ushort? NewSequence, uint? QueuedActionId, ulong? QueuedTargetId);

    internal readonly record struct EffectReceipt(
        DateTime At, uint CasterEntityId, byte ActionType, uint ActionId, ulong MainTargetId,
        uint SourceSequence, uint GlobalSequence, MechanicActionEffectTarget[] Targets);

    internal void Observe(in ActionCall call)
    {
        lock (_sync)
        {
            if (_calls.Count == ActionCapacity)
                _lastDroppedCall = _calls.Dequeue().At;
            _calls.Enqueue(call);
        }
    }

    internal void Observe(in EffectReceipt effect)
    {
        lock (_sync)
        {
            if (_effects.Count == EffectCapacity)
                _lastDroppedEffect = _effects.Dequeue().At;
            _effects.Enqueue(effect);
        }
    }

    internal object Snapshot(DateTime since)
    {
        ActionCall[] calls;
        EffectReceipt[] effects;
        bool callsTruncated, effectsTruncated;
        lock (_sync)
        {
            calls = _calls.Where(call => call.At >= since).ToArray();
            effects = _effects.Where(effect => effect.At >= since).ToArray();
            callsTruncated = _lastDroppedCall >= since;
            effectsTruncated = _lastDroppedEffect >= since;
        }
        return new
        {
            since,
            captureStartedAt = _startedAt,
            actionHookAvailable = _host?.CapturesActionCalls == true,
            effectHookAvailable = _host?.CapturesActionEffects == true,
            callsTruncated,
            effectsTruncated,
            calls,
            effects,
            lastActionCheckAtStall = CheckLastTargetedAction(calls),
        };
    }

    private static unsafe object? CheckLastTargetedAction(ActionCall[] calls)
    {
        var manager = ActionManager.Instance();
        if (manager == null || Service.LocalPlayer is not { Address: not 0 } player)
            return null;

        for (int i = calls.Length - 1; i >= 0; i--)
        {
            var call = calls[i];
            if (call.Type != ActionType.Action || call.GroundTargeted || call.ActionId == 0 ||
                call.TargetId is 0 or 0xE000_0000 ||
                Service.GameObjects.SearchById(call.TargetId) is not { Address: not 0 } target)
                continue;

            try
            {
                uint adjustedActionId = manager->GetAdjustedActionId(call.ActionId);
                if (adjustedActionId == 0)
                    return null;
                return new
                {
                    call.At,
                    call.ActionId,
                    adjustedActionId,
                    call.TargetId,
                    actionStatus = manager->GetActionStatus(call.Type, adjustedActionId, call.TargetId),
                    rangeOrSightStatus = ActionManager.GetActionInRangeOrLoS(adjustedActionId,
                        (GameObject*)player.Address, (GameObject*)target.Address),
                };
            }
            catch (Exception ex)
            {
                return new { call.At, call.ActionId, call.TargetId, error = ex.Message };
            }
        }
        return null;
    }

    public void Dispose() => _host?.DetachCombatActionEvidence(this);
}
