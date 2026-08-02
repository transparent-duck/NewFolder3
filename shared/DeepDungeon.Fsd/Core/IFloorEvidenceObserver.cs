namespace DeepDungeon.Fsd.Core;

/// <summary>
/// Optional host-owned receiver for finalized floor evidence after the local journal
/// has persisted it. Implementations run on the journal writer thread and must not
/// block FSD progress or mutate the supplied bundle.
/// </summary>
public interface IFloorEvidenceObserver
{
    void OnFloorEvidencePersisted(FloorEvidenceBundle bundle);
}
