using System.Numerics;
using global::Dalamud.Plugin.Ipc;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

/// <summary>Owns the native navigation projection cache for the current floor destination.</summary>
internal sealed class PassageDestinationResolver
{
    public Vector3 ActorPosition => _passageActorPosition;
    public Vector3 WalkingPosition => _passageWalkPosition;
    public bool Projected => _passageProjectionAccepted;
    private ICallGateSubscriber<Vector3, float, float, Vector3?>? _passageMeshPoint;
    private Vector3 _passageActorPosition, _passageWalkPosition;
    private long _passageProjectionGeneration = -1;
    private DateTime _nextPassageProjectionAt;
    private bool _passageProjectionAccepted;

    public Vector3 Resolve(Vector3 actor, long generation)
    {
        if (generation != _passageProjectionGeneration || Vector3.DistanceSquared(actor, _passageActorPosition) > 0.01f)
        {
            _passageProjectionGeneration = generation;
            _passageActorPosition = _passageWalkPosition = actor;
            _passageProjectionAccepted = false;
            _nextPassageProjectionAt = DateTime.MinValue;
        }
        if (_passageProjectionAccepted || DateTime.UtcNow < _nextPassageProjectionAt) return _passageWalkPosition;
        _nextPassageProjectionAt = DateTime.UtcNow.AddSeconds(1);
        try
        {
            _passageMeshPoint ??= Service.PluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
            if (_passageMeshPoint.HasFunction && PassageNavigationPolicy.TryProjectActor(actor,
                _passageMeshPoint.InvokeFunc(actor, 0.25f, 4f), out var projected))
            {
                _passageWalkPosition = projected;
                _passageProjectionAccepted = true;
                Service.Log.Info($"[FloorPhase] Passage walking destination: actor={actor}, mesh={projected}, generation={generation}");
            }
        }
        catch { /* Keep the actor destination if optional projection is unavailable. */ }
        return _passageWalkPosition;
    }
}
