namespace DeepDungeon.Fsd.Runtime;

public interface IFsdApplication : IDisposable
{
    DeepDungeonStateSnapshot CurrentDeepDungeonState { get; }
    object Start();
    object Stop();
    void Update();
    void Draw();
    FsdApplicationSnapshot Snapshot();
}
