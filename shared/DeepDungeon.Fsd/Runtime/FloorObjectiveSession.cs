using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;
public enum RoomObjectiveCategory
{
    Hoard,
    Chests,
    Intel
}

public readonly record struct RoomObjectiveKey(
    int RoomIndex,
    RoomObjectiveCategory Category,
    FloorObjectiveKind Kind);

public readonly record struct ActiveObjectiveExecution(
    ObjectiveIdentity Identity,
    FloorObjectiveKind Kind,
    bool Required,
    RoomObjectiveCategory Category);

/// <summary>Owns objective identities, results and arbitration cache for one stable floor.</summary>
public sealed class FloorObjectiveSession : IDisposable
{
    public FloorObjectiveSession(long generation)
    {
        Generation = generation;
        ObjectiveLedger = new FloorObjectiveLedger(generation);
    }

    public long Generation { get; }
    public FloorObjectiveLedger ObjectiveLedger { get; }
    public ObjectiveArbiterDecision ObjectiveDecision { get; private set; }
    public bool HasObjectiveDecision { get; private set; }
    public bool IsDisposed { get; private set; }
    private readonly Dictionary<RoomObjectiveKey, long> _objectiveIds = new();
    private long _nextObjectiveId;
    private ObjectiveArbiterSnapshot _objectiveInput;
    private long _objectiveEvidenceVersion;
    private long _objectiveOptionsVersion;
    private long _objectiveLedgerVersion;
    private bool _hasObjectiveInput;

    public (bool HoardSearched, bool ChestsSearched, bool IntelVisited) GetRoomProgress(int roomIndex)
    {
    	bool hoardSearched = false;
    	bool chestsSearched = false;
    	bool intelVisited = false;
    	foreach (var pair in _objectiveIds)
    	{
    		if (pair.Key.RoomIndex != roomIndex || !ObjectiveLedger.TryGetObjective(pair.Value, out var objective))
    			continue;

    		switch (pair.Key.Category)
    		{
    			case RoomObjectiveCategory.Hoard:
    				hoardSearched |= objective.Outcome == ObjectiveOutcomeKind.Succeeded;
    				break;
    			case RoomObjectiveCategory.Chests:
    				chestsSearched |= objective.Outcome is ObjectiveOutcomeKind.Succeeded or ObjectiveOutcomeKind.Skipped;
    				break;
    			case RoomObjectiveCategory.Intel:
    				intelVisited |= objective.Outcome == ObjectiveOutcomeKind.Succeeded;
    				break;
    		}
    	}
    	return (hoardSearched, chestsSearched, intelVisited);
    }

    public ObjectiveRecord GetOrCreateObjective(
    	RoomObjectiveKey key,
    	FloorObjectiveKind kind,
    	bool required)
    {
    	if (!_objectiveIds.TryGetValue(key, out long objectiveId))
    	{
    		objectiveId = ++_nextObjectiveId;
    		_objectiveIds.Add(key, objectiveId);
    		ObjectiveLedger.AddObjective(objectiveId, kind, required);
    	}

    	if (!ObjectiveLedger.TryGetObjective(objectiveId, out var objective))
    		throw new InvalidOperationException($"Floor objective {objectiveId} is missing from generation {Generation} ledger.");
    	return objective;
    }

    public bool SetObjectiveDecision(ObjectiveArbiterDecision decision)
    {
    	if (IsDisposed ||
    	    HasObjectiveDecision &&
    	    ObjectiveDecision == decision)
    		return false;

    	ObjectiveDecision = decision;
    	HasObjectiveDecision = true;
    	return true;
    }

    public bool RefreshObjectiveDecision(
    	ObjectiveArbiterSnapshot input,
    	long evidenceVersion,
    	long optionsVersion,
    	long ledgerVersion,
    	out ObjectiveArbiterDecision decision)
    {
    	decision = ObjectiveDecision;
    	if (IsDisposed ||
    	    _hasObjectiveInput &&
    	    _objectiveInput == input &&
    	    _objectiveEvidenceVersion == evidenceVersion &&
    	    _objectiveOptionsVersion == optionsVersion &&
    	    _objectiveLedgerVersion == ledgerVersion)
    	{
    		return false;
    	}

    	_objectiveInput = input;
    	_objectiveEvidenceVersion = evidenceVersion;
    	_objectiveOptionsVersion = optionsVersion;
    	_objectiveLedgerVersion = ledgerVersion;
    	_hasObjectiveInput = true;
    	decision = ObjectiveArbiter.Decide(input);
    	return SetObjectiveDecision(decision);
    }

    public void ClearObjectiveDecision()
    {
    	ObjectiveDecision = default;
    	HasObjectiveDecision = false;
    	_hasObjectiveInput = false;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _objectiveIds.Clear();
        ClearObjectiveDecision();
    }
}