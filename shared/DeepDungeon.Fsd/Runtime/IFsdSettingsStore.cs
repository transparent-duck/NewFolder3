namespace DeepDungeon.Fsd.Runtime;

public interface IFsdSettingsStore
{
    FsdSettings Load();
    void Refresh(FsdSettings settings);
    void Save(FsdSettings settings);
}
