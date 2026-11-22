using BossMod;
using BossMod.Dawntrail.DeepDungeon.PilgrimsTraverse;
using BossMod.Endwalker.Trial.T02Hydaelyn;

namespace MasterPlayer.MechanicRuntime;

internal sealed class EncounterDutyAdapters : IDisposable
{
    private const uint PassionsHeat = 43322;
    private CastStatusRestriction? _heat;
    private readonly EventSubscriptions _subscriptions;

    internal EncounterDutyAdapters(MechanicSession session)
    {
        var world = session.World;
        _subscriptions = new(
            session.BossModules.ModuleActivated.Subscribe(module =>
            {
                if (module is T02Hydaelyn && module.FindComponent<Lightwave>() != null)
                {
                    module.DeactivateComponent<Lightwave>();
                    module.ActivateComponent<HydaelynLightwave>();
                }
            }),
            session.ZoneModules.ModuleLoaded.Subscribe(module =>
            {
                if (module is PTFloorModule)
                    _heat = new(world, 960, ExecutionRestrictions.StopAll,
                        Service.LuminaRow<Lumina.Excel.Sheets.Action>(PassionsHeat)?.EffectRange ?? 0);
            }),
            session.ZoneModules.ModuleUnloaded.Subscribe(_ => _heat = null),
            world.Actors.CastStarted.Subscribe(actor =>
            {
                if (actor.CastInfo?.Action.ID == PassionsHeat) _heat?.CastStarted(actor);
            }),
            world.Actors.CastFinished.Subscribe(actor =>
            {
                if (actor.CastInfo?.Action.ID == PassionsHeat) _heat?.CastFinished(actor);
            }),
            world.Actors.CastEvent.Subscribe((actor, ev) =>
            {
                if (ev.Action.ID == PassionsHeat) _heat?.CastResolved(actor, ev);
            }));
    }

    internal ExecutionRestrictions GetRestrictions(Actor player) => _heat?.Get(player) ?? ExecutionRestrictions.None;
    public void Dispose() => _subscriptions.Dispose();
}
