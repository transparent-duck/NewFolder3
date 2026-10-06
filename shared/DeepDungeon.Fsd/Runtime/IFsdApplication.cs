namespace DeepDungeon.Fsd.Runtime;

public interface IFsdApplication : IDisposable
{
    DeepDungeonStateSnapshot CurrentDeepDungeonState { get; }
    FsdControlResult Start(FsdStartRequest? request = null);
    FsdControlResult StartFarming(FsdFarmingRequest request);
    FsdControlResult Stop();
    void Update();
    void Draw();
    FsdApplicationSnapshot Snapshot();
}
