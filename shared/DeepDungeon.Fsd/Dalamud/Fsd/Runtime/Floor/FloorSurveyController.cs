using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using DeepDungeon.Fsd.Dalamud.Map;
using global::Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

/// <summary>Owns natural reveal evidence for one floor.</summary>
internal sealed class FloorSurveyController : IDisposable
{
    public const int IntuitionResolutionWindowMilliseconds = 1500;
    private const float PassageDispatchExclusionRadius = 3f;
    private readonly long _generation;
    private readonly uint _dungeonId;
    private readonly byte _floor;
    private readonly bool _isNormal;
    private readonly DateTime _readyAtUtc;
    private readonly NormalFloorGraphSnapshot? _normalGraph;
    private readonly FloorObjectEvidenceTracker _objectEvidence;
    private readonly FloorItemExecutor _items;
    private readonly RunContext _ctx;
    private readonly PalantirProvider _palantir = new();
    private readonly PomanderManager _pomanderManager;
    private readonly ChatWatchers? _chatWatchers;
    private readonly Func<FloorPhase> _getPhase;
    private readonly Func<bool> _readNativeIntuition;
    private readonly Action<string> _setStatus;
    private readonly Action<string, object> RecordReplayEvent;
    private readonly Action<string> RequestPlanRefresh;
    private bool _disposed;
    private bool _nativeIntuitionActive => _readNativeIntuition();

    public FloorSurveyController(long generation, uint dungeonId, byte floor, bool isNormal,
        DateTime readyAtUtc, NormalFloorGraphSnapshot? graph, FloorObjectEvidenceTracker evidence,
        FloorItemExecutor items, RunContext context, PomanderManager pomanders,
        ChatWatchers? watchers, Func<FloorPhase> getPhase, Func<bool> readNativeIntuition, Action<string> setStatus,
        Action<string, object> record, Action<string> requestRefresh)
    {
        _generation = generation; _dungeonId = dungeonId; _floor = floor; _isNormal = isNormal;
        _readyAtUtc = readyAtUtc; _normalGraph = graph; _objectEvidence = evidence; _items = items;
        _ctx = context; _pomanderManager = pomanders; _chatWatchers = watchers;
        _getPhase = getPhase;
        _readNativeIntuition = readNativeIntuition; _setStatus = setStatus;
        RecordReplayEvent = record; RequestPlanRefresh = requestRefresh;
    }

    public bool EntryIncenseWindowOpen() => _isNormal && FarmingItemPolicy.For(_ctx.FarmingPlan?.Mode)
        .AllowsEntryIncense(_getPhase() == FloorPhase.FloorSetup, (DateTime.UtcNow - _readyAtUtc).TotalSeconds);

    private bool IsSightUseBlocked() => SightUseStateMachine.PreventsAutomaticUse(_chatWatchers?.SightState ?? SightUseState.None);
    public void Dispose()
    {
        _disposed = true;
        EvidenceSession = null;
    }
    public FloorEvidenceSession? EvidenceSession { get; private set; }
    private bool SightResearchDispatched { get; set; }
    private bool NaturalRevealInventoryBaselineEstablished { get; set; }
    private int NaturalPreviousSightStock { get; set; }
    private int NaturalPreviousMazerootStock { get; set; }
    private long NaturalPreviousSightLogSequence { get; set; }
    private long NaturalPreviousMazerootLogSequence { get; set; }
    private bool NaturalRevealDispatched { get; set; }
    private SightResearchRevealResource NaturalRevealResource { get; set; }
    private long NaturalSightLogSequenceAtDispatch { get; set; }
    private long NaturalMazerootLogSequenceAtDispatch { get; set; }
    private bool NaturalRevealConfirmed { get; set; }
    private long NaturalRevealConfirmationRefreshSequence { get; set; }
    private long NaturalRevealConfirmationFullScanCount { get; set; }
    private bool NaturalCandidateUniverseResolved { get; set; }
    private RawWorldPosition[] NaturalCandidateUniverse { get; set; } = [];
    private HashSet<TrapWitnessKey> NaturalObservedTrapWitnesses { get; } = [];
    private float NaturalMaximumTrapWitnessDistance { get; set; }
    private bool NaturalJointScanComplete { get; set; }
    public bool NaturalPoisonfruitAttempted { get; private set; }
    public bool NaturalMazerootAttemptedOrAdopted { get; private set; }
    private long InheritedIntuitionArmedAtMilliseconds { get; set; }
    public bool AwaitingInheritedIntuition => InheritedIntuitionArmedAtMilliseconds != 0 && !InheritedIntuitionDecision.HasValue;
    private InheritedIntuitionEvidenceKind InheritedIntuitionEvidence { get; set; }
    public InheritedIntuitionResolutionDecision? InheritedIntuitionDecision { get; private set; }
    public void OpenEvidenceSession(FloorEvidenceSession session) => EvidenceSession = session;
    public void BeginInheritedIntuition(long observedSince, bool hoardAlreadyObserved)
    {
        InheritedIntuitionArmedAtMilliseconds = observedSince;
        if (hoardAlreadyObserved) InheritedIntuitionEvidence = InheritedIntuitionEvidenceKind.HoardPresent;
    }
    public void ObserveConfirmedStone(FloorItemUsePurpose purpose)
    {
        switch (purpose)
        {
            case FloorItemUsePurpose.NaturalPoisonfruit:
                NaturalPoisonfruitAttempted = true;
                _objectEvidence.Invalidate();
                break;
            case FloorItemUsePurpose.NaturalPassageMazeroot:
                NaturalMazerootAttemptedOrAdopted = true;
                _objectEvidence.Invalidate();
                break;
        }
    }
    public void ObserveExhaustedItem(FloorItemUsePurpose purpose)
    {
        switch (purpose)
        {
            case FloorItemUsePurpose.NaturalPoisonfruit:
                // Preserve the existing floor policy: an exhausted Poisonfruit does not fall back to 敏慧.
                NaturalPoisonfruitAttempted = true;
                break;
            case FloorItemUsePurpose.NaturalPassageMazeroot:
                NaturalMazerootAttemptedOrAdopted = true;
                break;
        }
    }
    public void InitializeNaturalInventory()
    {
			if (_isNormal)
			{
				NaturalRevealInventoryBaselineEstablished = true;
				NaturalPreviousSightStock =
					_pomanderManager.GetCount(
						FloorInitPlanner.SightPomanderSlotIndex);
				NaturalPreviousMazerootStock =
					DungeonCatalog.SupportsNaturalPtStones(
						_dungeonId)
						? _pomanderManager.GetStoneCount(2)
						: 0;
			}
    }

    public void ObserveInheritedMessage(bool positive)
    {
        if (InheritedIntuitionArmedAtMilliseconds == 0 || InheritedIntuitionDecision.HasValue) return;
        InheritedIntuitionEvidence = positive ? InheritedIntuitionEvidenceKind.HoardPresent
            : InheritedIntuitionEvidenceKind.NoHoard;
    }

    private readonly record struct TrapWitnessKey(
    	ulong GameObjectId,
    	uint BaseId,
    	int X,
    	int Y,
    	int Z)
    {
        public static TrapWitnessKey From(in FloorObjectEvidence evidence)
    	{
    		const float normalization = 10f;
            return new TrapWitnessKey(
    			evidence.GameObjectId,
    			evidence.BaseId,
    			(int)MathF.Round(evidence.Position.X * normalization),
    			(int)MathF.Round(evidence.Position.Y * normalization),
    			(int)MathF.Round(evidence.Position.Z * normalization));
    	}
    }

    public unsafe void ObserveFloorEvidence(InstanceContentDeepDungeon* dd)
    {
    	var session = EvidenceSession;
    	var snapshot = _objectEvidence.Current;
    	if (snapshot == null)
    		return;

    	int intuitionStock = _pomanderManager.GetCount(FloorInitPlanner.IntuitionPomanderSlotIndex);
    	int sightStock = _pomanderManager.GetCount(FloorInitPlanner.SightPomanderSlotIndex);
    	int effectiveSightStock = _items.IsPomanderBlockedForFloor(
    		FloorInitPlanner.SightPomanderSlotIndex)
    			? 0
    			: sightStock;
    	bool naturalPtStonesSupported =
    		DungeonCatalog.SupportsNaturalPtStones(dd->DeepDungeonId);
    	bool pomandersUsableThisFloor =
    		DeepDungeonFloorItemUsePolicy.CanUsePomanders(
    			dd->DeepDungeonBanId);
    	bool ptIncenseUsableThisFloor =
    		DeepDungeonFloorItemUsePolicy.CanUsePtIncense(
    			dd->DeepDungeonBanId);
    	int mazerootStock =
    		naturalPtStonesSupported
    			? _pomanderManager.GetStoneCount(2)
    			: 0;
    	int effectiveMazerootStock = naturalPtStonesSupported
    		? _items.GetStoneCountAvailableForFloorUse(2)
    		: 0;
    	bool sightTrapObserved = snapshot.SightTrapIndicators.Count > 0;
    	if (sightTrapObserved)
    	{
    		_chatWatchers?.ConfirmSightThisFloor("SightTrapIndicatorObserved");
    	}
    	bool sightConfirmed = _chatWatchers?.SightState == SightUseState.Confirmed;
        ObserveNaturalRevealInventory(sightStock, mazerootStock);
        TryAdoptExternalNaturalReveal(sightConfirmed);
        TryConfirmNaturalAuthoritativeReveal(sightTrapObserved);
    	session?.ObserveEffectStates(
    		_nativeIntuitionActive,
    		intuitionStock,
    		sightConfirmed,
    		sightStock,
    		"scheduled-object-evidence-refresh");
    	session?.ObserveObjectEvidence(dd, snapshot);

    var intuitionResolution = _ctx.Knowledge.Floor?.ChatSaysHoard == true
            ? IntuitionFloorResolution.Positive
            : _ctx.Knowledge.Floor?.ChatSaysNoHoard == true
                ? IntuitionFloorResolution.Negative
                : IntuitionFloorResolution.Unresolved;
    	bool exactHoardEvidenceFromBanded;
    	Vector3? exactIndicatorPosition =
    		TryResolveNaturalExactHoardPosition(
    			dd,
    			snapshot,
    			out exactHoardEvidenceFromBanded);
    	bool exactIndicatorAvailable = exactIndicatorPosition.HasValue;
    	bool acceptedIncomingEdgeKnown =
    		exactIndicatorPosition.HasValue &&
    		HasAcceptedIncomingEdge(
    			dd,
    			exactIndicatorPosition.Value);
    	var decision = SightResearchPolicy.Decide(new SightResearchSnapshot(
    		StableFloor: _isNormal && _floor == dd->Floor && _dungeonId == dd->DeepDungeonId,
    		IntuitionResolution: intuitionResolution,
    		ExactHoardIndicatorAvailable: exactIndicatorAvailable,
    		AcceptedIncomingEdgeKnown: acceptedIncomingEdgeKnown,
    		SightUseBlocked:
    			IsSightUseBlocked() ||
    			!pomandersUsableThisFloor,
    		SightStock: effectiveSightStock,
    		MazerootStock: effectiveMazerootStock,
    		RevealDispatchedThisFloor: NaturalRevealDispatched,
    		AuthoritativeRevealConfirmed: NaturalRevealConfirmed,
    		MazerootSupported: naturalPtStonesSupported,
    		MazerootUsableThisFloor: ptIncenseUsableThisFloor,
    		BandedHoardEvidenceAvailable: exactHoardEvidenceFromBanded));
    	int selectedResourceStock = decision.ShouldUseMazeroot
    		? effectiveMazerootStock
    		: effectiveSightStock;
    	session?.ObserveResearchDecision(
    		decision,
    		selectedResourceStock,
    		NaturalRevealResource);

    	if (decision.ShouldCollectJointScan && exactIndicatorPosition.HasValue)
    		UpdateNaturalJointCapture(dd, snapshot, exactIndicatorPosition.Value);

    	if (!decision.ShouldUseReveal || !_items.CanAttemptPomanderUse())
    	{
    		return;
    	}

    	bool dispatched = decision.ShouldUseSight
    		? _items.IsPomanderAvailableForFloorUse(FloorInitPlanner.SightPomanderSlotIndex) &&
    		  _items.TryUsePomander(
    			  FloorInitPlanner.SightPomanderSlotIndex,
    			  dd,
    			  "automatic exact-hoard research")
    		: effectiveMazerootStock > 0 &&
    		  TryUseNaturalMazeroot(
    			  dd,
    			  "automatic exact-hoard research");
    	session?.ObserveResearchAction(
    		dispatched,
    		decision.RevealResource,
    		selectedResourceStock);
    }

    private unsafe Vector3? TryResolveNaturalExactHoardPosition(
    	InstanceContentDeepDungeon* dd,
    	FloorObjectEvidenceSnapshot snapshot,
    	out bool fromBandedChest)
    {
    	fromBandedChest = false;
    	if (snapshot.HoardIndicators.Count > 0)
    		return snapshot.HoardIndicators[0].Object.Position;

        if (_ctx.Knowledge.Floor?.ExactHoardPosition is { } cachedPosition)
    		return cachedPosition;

    	if (!BandedChestLocator.TryFindNearestToPlayer(snapshot, out Vector3? bandedPosition) ||
    		!bandedPosition.HasValue ||
    		!IsPalantirCandidatePosition(dd, bandedPosition.Value))
    	{
    		return null;
    	}

    	fromBandedChest = true;
    	return bandedPosition.Value;
    }

    private unsafe bool IsPalantirCandidatePosition(
    	InstanceContentDeepDungeon* dd,
    	Vector3 position)
    {
    	IReadOnlyList<int>? rooms = _normalGraph?.ReachableRooms;
    	if (rooms == null)
    		return false;

    	int roomIndex = RoomGraph.GetRoomIndexForPosition(dd, position, rooms, -1);
    	if (roomIndex < 0)
    		return false;

    	IReadOnlyList<Vector3>? candidates =
            _palantir.GetCandidatePositionsForRoom(dd, roomIndex);
    	if (candidates == null)
    		return false;

    	var rawPosition = new RawWorldPosition(position.X, position.Y, position.Z);
    	for (int i = 0; i < candidates.Count; i++)
    	{
    		Vector3 candidate = candidates[i];
    		if (RawWorldPosition.CanonicallyEquals(
    			rawPosition,
    			new RawWorldPosition(candidate.X, candidate.Y, candidate.Z)))
    		{
    			return true;
    		}
    	}

    	return false;
    }

    private unsafe bool HasAcceptedIncomingEdge(
    	InstanceContentDeepDungeon* dd,
    	Vector3 exactHoardPosition)
    {
    	DetailedMapCatalog? catalog = _ctx?.DetailedMap.Catalog;
    	IReadOnlyList<int>? rooms = _normalGraph?.ReachableRooms;
    	if (catalog == null || rooms == null)
    		return false;

    	int roomIndex = RoomGraph.GetRoomIndexForPosition(
    		dd,
    		exactHoardPosition,
    		rooms,
    		-1);
    	if (roomIndex < 0)
    		return false;

    	var rawPosition = new RawWorldPosition(
    		exactHoardPosition.X,
    		exactHoardPosition.Y,
    		exactHoardPosition.Z);
    	return DetailedMapResearchKnowledge.ResolveHoardPredecessor(
    		       catalog,
    		       dd->ActiveLayoutIndex,
    		       roomIndex,
    		       rawPosition) !=
    	       DetailedMapHoardPredecessorKnowledge.Unknown;
    }

    private void ObserveNaturalRevealInventory(
    	int sightStock,
    	int mazerootStock)
    {
    	long sightLogSequence =
    		_chatWatchers?.SightLogSequence ?? 0;
    	long mazerootLogSequence =
    		_chatWatchers?.MazerootLogSequence ?? 0;
    	NaturalRevealInventoryDecision decision =
    		NaturalRevealInventoryPolicy.Decide(
    			new NaturalRevealInventorySnapshot(
    				NaturalRevealInventoryBaselineEstablished,
    				NaturalPreviousSightStock,
    				NaturalPreviousMazerootStock,
    				sightStock,
    				mazerootStock,
    				NaturalRevealDispatched ||
    				NaturalRevealConfirmed ||
    				NaturalMazerootAttemptedOrAdopted,
    				DungeonCatalog.SupportsNaturalPtStones(
    					_dungeonId)));
    	long previousSightLogSequence =
    		NaturalPreviousSightLogSequence;
    	long previousMazerootLogSequence =
    		NaturalPreviousMazerootLogSequence;
    	NaturalRevealInventoryBaselineEstablished = true;
    	NaturalPreviousSightStock = sightStock;
    	NaturalPreviousMazerootStock = mazerootStock;
    	NaturalPreviousSightLogSequence =
    		sightLogSequence;
    	NaturalPreviousMazerootLogSequence =
    		mazerootLogSequence;
    	if (decision.Kind !=
    	    NaturalRevealInventoryDecisionKind.AdoptExternalPending)
    	{
    		return;
    	}

    	NaturalRevealDispatched = true;
    	NaturalRevealResource = decision.Resource;
    	if (decision.Resource == SightResearchRevealResource.Mazeroot)
    		NaturalMazerootAttemptedOrAdopted = true;
    	NaturalSightLogSequenceAtDispatch =
    		previousSightLogSequence;
    	NaturalMazerootLogSequenceAtDispatch =
    		previousMazerootLogSequence;
    	NaturalRevealConfirmed = false;
    	NaturalJointScanComplete = false;
    	_chatWatchers?.MarkSightAttemptedThisFloor();
    	RecordReplayEvent("external-reveal-pending", new
    	{
    		floor = _floor,
    		revealSource = decision.Resource.ToString()
    	});
    }

    private bool TryConfirmNaturalAuthoritativeReveal(
    	bool sightTrapObserved)
    {
    	if (NaturalRevealConfirmed)
    		return true;
    	if (!NaturalRevealDispatched)
    		return false;

    	bool confirmed =
    		NaturalRevealInventoryPolicy.IsAuthoritativeConfirmation(
    			NaturalRevealResource,
    			(_chatWatchers?.SightLogSequence ?? 0) >
    				NaturalSightLogSequenceAtDispatch,
    			(_chatWatchers?.MazerootLogSequence ?? 0) >
    				NaturalMazerootLogSequenceAtDispatch,
    			sightTrapObserved,
    			DungeonCatalog.SupportsNaturalPtStones(
    				_dungeonId));
    	if (!confirmed)
    		return false;

    	NaturalRevealConfirmed = true;
    	NaturalRevealConfirmationRefreshSequence =
    		_objectEvidence.Current?.RefreshSequence ??
    		_objectEvidence.RefreshCount;
    	NaturalRevealConfirmationFullScanCount =
    		_objectEvidence.FullScanCount;
    	EvidenceSession?.ObserveResearchAuthoritativeRevealConfirmed();
    	return true;
    }

    private void TryAdoptExternalNaturalReveal(
    	bool sightConfirmed)
    {
    	if (NaturalRevealDispatched ||
    	    NaturalRevealConfirmed ||
    	    NaturalMazerootAttemptedOrAdopted ||
    	    !sightConfirmed)
    	{
    		return;
    	}

    	bool sightLogObserved =
    		(_chatWatchers?.SightLogSequence ?? 0) > 0;
    	bool mazerootLogObserved =
    		DungeonCatalog.SupportsNaturalPtStones(
    			_dungeonId) &&
    		(_chatWatchers?.MazerootLogSequence ?? 0) > 0;
    	SightResearchRevealResource resource =
    		(sightLogObserved, mazerootLogObserved) switch
    		{
    			(true, false) => SightResearchRevealResource.Sight,
    			(false, true) => SightResearchRevealResource.Mazeroot,
    			_ => SightResearchRevealResource.None
    		};
    	if (resource == SightResearchRevealResource.None)
    		return;

    	NaturalRevealResource = resource;
    	if (resource == SightResearchRevealResource.Mazeroot)
    		NaturalMazerootAttemptedOrAdopted = true;
    	NaturalRevealConfirmed = true;
    	NaturalRevealConfirmationRefreshSequence =
    		_objectEvidence.Current?.RefreshSequence ??
    		_objectEvidence.RefreshCount;
    	NaturalRevealConfirmationFullScanCount =
    		_objectEvidence.FullScanCount;
    	EvidenceSession?.ObserveResearchAuthoritativeRevealConfirmed();
    }

    private unsafe void UpdateNaturalJointCapture(
    	InstanceContentDeepDungeon* dd,
    	FloorObjectEvidenceSnapshot snapshot,
    	Vector3 exactHoardPosition)
    {
    	if (NaturalJointScanComplete ||
    	    !NaturalRevealConfirmed ||
    	    EvidenceSession?.Bundle.AcquisitionMode !=
    	    FloorEvidenceAcquisitionMode.AutomaticCommunitySurvey ||
    	    snapshot.PlayerPosition is not { } playerPosition)
    	{
    		return;
    	}

    	if (!NaturalCandidateUniverseResolved)
    	{
    		IReadOnlyList<int>? rooms = _normalGraph?.ReachableRooms;
    		if (rooms == null)
    			return;

    		int hoardRoomIndex = RoomGraph.GetRoomIndexForPosition(
    			dd,
    			exactHoardPosition,
    			rooms,
    			-1);
    		if (hoardRoomIndex < 0)
    			return;

    		IReadOnlyList<Vector3>? palantirCandidates =
                _palantir.GetCandidatePositionsForRoom(dd, hoardRoomIndex);
    		if (palantirCandidates == null || palantirCandidates.Count == 0)
    			return;

    		var candidates = new RawWorldPosition[palantirCandidates.Count];
    		for (int i = 0; i < palantirCandidates.Count; i++)
    		{
    			Vector3 candidate = palantirCandidates[i];
    			candidates[i] = new RawWorldPosition(
    				candidate.X,
    				candidate.Y,
    				candidate.Z);
    		}
    		NaturalCandidateUniverse = candidates;
    		NaturalCandidateUniverseResolved = true;
    	}

    	for (int i = 0; i < snapshot.SightTrapIndicators.Count; i++)
    	{
    		FloorObjectEvidence trap = snapshot.SightTrapIndicators[i];
    		if (!NaturalObservedTrapWitnesses.Add(
                    TrapWitnessKey.From(trap)))
    		{
    			continue;
    		}

    		float dx = trap.Position.X - playerPosition.X;
    		float dz = trap.Position.Z - playerPosition.Z;
    		NaturalMaximumTrapWitnessDistance = MathF.Max(
    			NaturalMaximumTrapWitnessDistance,
    			MathF.Sqrt(dx * dx + dz * dz));
    	}

    	float safeRadius =
            SightResearchPolicy.GetProvenTrapLoadSafeRadius(
    			NaturalMaximumTrapWitnessDistance);
    	bool trapWitnessAvailable =
    		NaturalObservedTrapWitnesses.Count > 0 &&
    		safeRadius > 0f;
    	var rawPlayerPosition = new RawWorldPosition(
    		playerPosition.X,
    		playerPosition.Y,
    		playerPosition.Z);
    	bool allCandidatesCovered =
    		trapWitnessAvailable &&
            SightResearchPolicy.AreAllCandidatesCovered(
    			rawPlayerPosition,
    			NaturalCandidateUniverse,
    			safeRadius);
    	bool synchronizedScanAvailable =
    		snapshot.Available &&
    		snapshot.RefreshSequence >
    		NaturalRevealConfirmationRefreshSequence &&
    		_objectEvidence.FullScanCount >
    		NaturalRevealConfirmationFullScanCount;
    	if (!synchronizedScanAvailable ||
    	    !trapWitnessAvailable ||
    	    !allCandidatesCovered)
    	{
    		return;
    	}

    	NaturalJointScanComplete = true;
    	EvidenceSession?.ObserveResearchJointScanComplete();
    	RecordReplayEvent("natural-joint-scan-complete", new
    	{
    		floor = dd->Floor,
    		revealSource = NaturalRevealResource.ToString(),
    		safeRadius,
    		candidateCount = NaturalCandidateUniverse.Length
    	});
    }

    private void RegisterNaturalRevealDispatch(
    	SightResearchRevealResource resource,
    	long sightLogSequenceBeforeDispatch,
    	long mazerootLogSequenceBeforeDispatch)
    {
        if (resource == SightResearchRevealResource.None ||
    	    resource == SightResearchRevealResource.Mazeroot &&
    	    !DungeonCatalog.SupportsNaturalPtStones(_dungeonId))
    	{
    		return;
    	}

    	NaturalRevealDispatched = true;
    	NaturalRevealResource = resource;
    	if (resource == SightResearchRevealResource.Mazeroot)
    		NaturalMazerootAttemptedOrAdopted = true;
    	NaturalSightLogSequenceAtDispatch =
    		sightLogSequenceBeforeDispatch;
    	NaturalMazerootLogSequenceAtDispatch =
    		mazerootLogSequenceBeforeDispatch;
    	NaturalRevealConfirmed = false;
    	NaturalJointScanComplete = false;
    	_objectEvidence.Invalidate();
    }

    public unsafe bool TryUseNaturalMazeroot(
    	InstanceContentDeepDungeon* dd,
    	string reason)
    {
        if (_ctx.FarmingPlan?.Mode == FarmingMode.DeepProgression ||
            !DungeonCatalog.SupportsNaturalPtStones(dd->DeepDungeonId) ||
    	    !DeepDungeonFloorItemUsePolicy.CanUsePtIncense(
    		    dd->DeepDungeonBanId) ||
    	    !_items.CanAttemptPomanderUse() ||
    	    _disposed || !EntryIncenseWindowOpen() ||
    	    NaturalPoisonfruitAttempted ||
    	    _items.GetStoneCountAvailableForFloorUse(2) <= 0)
    	{
    		return false;
    	}
    	if (!CanDispatchNaturalPassageOpeningStoneSafely(dd))
    	{
            _setStatus("Waiting to use 敏慧 safely away from the passage");
    		return false;
    	}

    	return _items.TryDispatchFloorStone(
    		2,
    		dd,
    		reason,
    		FloorItemUsePurpose.NaturalReveal);
    }

    internal unsafe bool CanDispatchNaturalPassageOpeningStoneSafely(
    	InstanceContentDeepDungeon* dd)
    {
    	var player = Service.LocalPlayer;
    	if (player == null)
    		return false;

    	FloorObjectEvidenceSnapshot? evidence =
    		_objectEvidence.Current;
    	int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
    	int passageRoom = RoomGraph.GetPassageRoomIndex(dd);
    	bool roomRelationAvailable =
    		playerRoom >= 0 && passageRoom >= 0;
    	Vector3 passagePosition = default;
    	bool exactPassageAvailable =
    		evidence?.Available == true &&
    		PassageLocator.TryGetPassageActorPosition(
    			evidence,
    			out passagePosition);
    	float distanceSquared = exactPassageAvailable
    		? Vector3.DistanceSquared(
    			player.Position,
    			passagePosition)
    		: 0f;
        return (roomRelationAvailable && playerRoom != passageRoom) ||
            (exactPassageAvailable && distanceSquared >= PassageDispatchExclusionRadius * PassageDispatchExclusionRadius);
    }

    public unsafe bool TryUseNaturalPassageAcceleration(
    	InstanceContentDeepDungeon* dd)
    {
        if (!DungeonCatalog.SupportsNaturalPtStones(dd->DeepDungeonId) ||
    	    !DeepDungeonFloorItemUsePolicy.CanUsePtIncense(
    		    dd->DeepDungeonBanId) ||
    	    !EntryIncenseWindowOpen() ||
    	    _ctx?.Duty.PassageOpen == true)
    	{
    		return false;
    	}

    	int poisonfruitStock = _items.GetStoneCountAvailableForFloorUse(1);
    	int mazerootStock = _items.GetStoneCountAvailableForFloorUse(2);
    	bool canDispatch = _items.CanAttemptPomanderUse();
    	bool passageDispatchSafe =
    		canDispatch &&
    		CanDispatchNaturalPassageOpeningStoneSafely(dd);
    	var action = NaturalPassageAccelerationPolicy.Decide(
    		new NaturalPassageAccelerationSnapshot(
    			EntryWindowOpen: EntryIncenseWindowOpen(),
    			PassageOpen: _ctx?.Duty.PassageOpen == true,
    			PoisonfruitStock: poisonfruitStock,
    			PoisonfruitAttemptedThisFloor:
    				NaturalPoisonfruitAttempted,
    			MazerootStock: mazerootStock,
    			MazerootAttemptedOrAdopted:
    				NaturalMazerootAttemptedOrAdopted,
    			CanDispatch: canDispatch,
    			PassageDispatchSafe: passageDispatchSafe,
    			PtStoneSupported:
    				DungeonCatalog.SupportsNaturalPtStones(
    					dd->DeepDungeonId),
    			PtStoneUsableThisFloor:
    				DeepDungeonFloorItemUsePolicy.CanUsePtIncense(
    					dd->DeepDungeonBanId)));
    	if (action == NaturalPassageAccelerationAction.DispatchMazeroot)
    	{
    		return TryUseNaturalPassageMazeroot(
    			dd,
    			"ordinary passage acceleration fallback");
    	}

    	if (action != NaturalPassageAccelerationAction.DispatchPoisonfruit)
    		return false;

    	return _items.TryDispatchFloorStone(
    		1,
    		dd,
    		"ordinary passage acceleration",
    		FloorItemUsePurpose.NaturalPoisonfruit);
    }

    private unsafe bool TryUseNaturalPassageMazeroot(
    	InstanceContentDeepDungeon* dd,
    	string reason)
    {
        if (NaturalMazerootAttemptedOrAdopted ||
    	    !DungeonCatalog.SupportsNaturalPtStones(dd->DeepDungeonId) ||
    	    !DeepDungeonFloorItemUsePolicy.CanUsePtIncense(
    		    dd->DeepDungeonBanId) ||
    	    !_items.CanAttemptPomanderUse() ||
    	    _disposed || !EntryIncenseWindowOpen() ||
    	    NaturalPoisonfruitAttempted ||
    	    _items.GetStoneCountAvailableForFloorUse(2) <= 0)
    	{
    		return false;
    	}
    	if (!CanDispatchNaturalPassageOpeningStoneSafely(dd))
    	{
            _setStatus("Waiting to use passage accelerator safely away from the passage");
    		return false;
    	}
    	return _items.TryDispatchFloorStone(
    		2,
    		dd,
    		reason,
    		FloorItemUsePurpose.NaturalPassageMazeroot);
    }

    public void CancelInheritedObservation() => InheritedIntuitionArmedAtMilliseconds = 0;

    public void ResolveInheritedIntuition()
    {
    	if (!_isNormal ||
    	    _disposed ||
            InheritedIntuitionArmedAtMilliseconds == 0 ||
    	    InheritedIntuitionDecision.HasValue)
    	{
    		return;
    	}

    	int elapsedMilliseconds = (int)Math.Clamp(
    		Environment.TickCount64 - InheritedIntuitionArmedAtMilliseconds,
    		0L,
    		int.MaxValue);
    	var decision = InheritedIntuitionResolutionPlanner.Decide(
    		InheritedIntuitionEvidence,
    		elapsedMilliseconds,
    		IntuitionResolutionWindowMilliseconds);
    	if (!decision.Terminal)
    		return;

    	InheritedIntuitionDecision = decision;
    	EvidenceSession?.ObserveInheritedIntuitionResolution(
    		decision.Source,
    		decision.ElapsedMilliseconds,
    		IntuitionResolutionWindowMilliseconds);

    	if (decision.NoHoard)
    	{
            _ctx.Knowledge.Floor?.MarkInheritedNoHoard();
            RequestPlanRefresh("inherited-no-hoard-inferred");
    	}
    	else if (decision.HoardPresent)
    	{
    		_objectEvidence.Invalidate();
    		RequestPlanRefresh("inherited-hoard-present");
    	}
    	else if (decision.IsError)
    	{
    		Service.Log.Error(
    			$"[FloorPhase] Inherited Intuition protocol anomaly on floor {_floor}: {decision.Source}.");
    	}

    	RecordReplayEvent("inherited-intuition-resolved", new
    	{
    		floor = _floor,
    		floorGeneration = _generation,
    		dungeonId = _dungeonId,
    		source = decision.Source.ToString(),
    		decision.HoardPresent,
    		decision.NoHoard,
    		decision.IsError,
    		decision.ElapsedMilliseconds,
    		windowMilliseconds = IntuitionResolutionWindowMilliseconds
    	});
        }

    public void ApplyConfirmedNaturalReveal(
    	PendingFloorItemUse pending,
    	SightResearchRevealResource resource)
    {
    	SightResearchDispatched = true;
    	RegisterNaturalRevealDispatch(
    		resource,
    		pending.SightLogSequenceBeforeDispatch,
    		pending.MazerootLogSequenceBeforeDispatch);
    }
}
