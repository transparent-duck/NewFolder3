namespace DeepDungeon.Fsd.Runtime;

public interface IFsdApplication : IDisposable
{
    DeepDungeonStateSnapshot CurrentDeepDungeonState { get; }
    object Start();
    object StartFarming(DeepDungeon.Fsd.Core.FarmingMode mode, DeepDungeon.Fsd.Core.SaveUse saveUse,
        int startFloor, int cycles, bool infinite, bool hoard, bool gold, bool silver, bool bronze);
    object Stop();
    void Update();
    void Draw();
    FsdApplicationSnapshot Snapshot();
}
