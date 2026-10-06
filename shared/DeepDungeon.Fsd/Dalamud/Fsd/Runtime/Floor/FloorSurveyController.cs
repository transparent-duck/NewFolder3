using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using DeepDungeon.Fsd.Dalamud.Map;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

/// <summary>Owns natural reveal and controlled survey evidence for one floor.</summary>
internal sealed class FloorSurveyController : IDisposable
{
    internal unsafe delegate NavDriveResult RoomNavigation(InstanceContentDeepDungeon* dungeon, int room, IPlayerCharacter player);
    public const int IntuitionResolutionWindowMilliseconds = 1500;
    private const uint StrengthStatusId = 687;
    private readonly long _generation;
    private readonly uint _dungeonId;
    private readonly byte _floor;
    private readonly bool _isNormal;
    private readonly DateTime _readyAtUtc;
    private readonly NormalFloorGraphSnapshot? _normalGraph;
    private readonly FloorObjectEvidenceTracker _objectEvidence;
    private readonly FloorItemExecutor _items;
    private readonly RunContext _ctx;
    private readonly AutoPilotExecutor? _executor;
    private readonly PomanderManager _pomanderManager;
    private readonly ChatWatchers? _chatWatchers;
    private readonly NavigationDriver? _navDriver;
    private readonly FloorEvidenceJournal? _floorEvidenceJournal;
    private readonly Func<FloorPhase> _getPhase;
    private readonly Func<bool> _readNativeIntuition;
    private readonly Func<string> _getStatus;
    private readonly Action<string> _setStatus;
    private readonly Action CancelActiveMovement;
    private readonly Action<string, object> RecordReplayEvent;
    private readonly Action<string> RequestPlanRefresh;
    private readonly Func<string, bool> HandleNoHoardEvidenceInvalidated;
    private readonly Action<string> EndHoardEvidenceWait;
    private readonly RoomNavigation NavigateToRoom;
    private bool _disposed;
    private bool _nativeIntuitionActive => _readNativeIntuition();
    private string _status { get => _getStatus(); set => _setStatus(value); }

    public FloorSurveyController(long generation, uint dungeonId, byte floor, bool isNormal,
        DateTime readyAtUtc, NormalFloorGraphSnapshot? graph, FloorObjectEvidenceTracker evidence,
        FloorItemExecutor items, RunContext context, AutoPilotExecutor? executor, PomanderManager pomanders,
        ChatWatchers? watchers, NavigationDriver? navigation, FloorEvidenceJournal? journal,
        Func<FloorPhase> getPhase, Func<bool> readNativeIntuition, Func<string> getStatus, Action<string> setStatus,
        Action cancelMovement, Action<string, object> record, Action<string> requestRefresh,
        Func<string, bool> invalidateNoHoard, Action<string> endEvidenceWait, RoomNavigation navigateRoom)
    {
        _generation = generation; _dungeonId = dungeonId; _floor = floor; _isNormal = isNormal;
        _readyAtUtc = readyAtUtc; _normalGraph = graph; _objectEvidence = evidence; _items = items;
        _ctx = context; _executor = executor; _pomanderManager = pomanders; _chatWatchers = watchers;
        _navDriver = navigation; _floorEvidenceJournal = journal; _getPhase = getPhase;
        _readNativeIntuition = readNativeIntuition; _getStatus = getStatus; _setStatus = setStatus;
        CancelActiveMovement = cancelMovement; RecordReplayEvent = record; RequestPlanRefresh = requestRefresh;
        HandleNoHoardEvidenceInvalidated = invalidateNoHoard; EndHoardEvidenceWait = endEvidenceWait;
        NavigateToRoom = navigateRoom;
    }

    public bool EntryIncenseWindowOpen() => _isNormal && FarmingItemPolicy.For(_ctx.FarmingPlan?.Mode)
        .AllowsEntryIncense(_getPhase() == FloorPhase.FloorSetup, (DateTime.UtcNow - _readyAtUtc).TotalSeconds);

    private bool IsSightUseBlocked() => SightUseStateMachine.PreventsAutomaticUse(_chatWatchers?.SightState ?? SightUseState.None);
    private static bool HasLocalPlayerStatus(uint statusId)
    {
        var player = Service.LocalPlayer;
        if (player == null) return false;
        foreach (var status in player.StatusList)
            if (status.StatusId == statusId) return true;
        return false;
    }

    public unsafe bool TryUpdateControlledActive(InstanceContentDeepDungeon* dd, FloorObjectiveKind primaryObjective)
    {
			if (_ctx?.ControlledPtSurvey != null &&
			    this is { ControlledPositiveMessagePendingIndicator: true } indicatorRuntime)
			{
				UpdateControlledIndicatorAcquisition(dd);
				return true;
			}

			if (_ctx?.ControlledPtSurvey != null &&
			    this is { ControlledIntuitionResolutionPending: true } &&
			    primaryObjective == FloorObjectiveKind.EnterPassage)
			{
				CancelActiveMovement();
				_status = "Controlled PT: holding passage until Intuition resolution window completes";
				return true;
			}

			if (_ctx?.ControlledPtSurvey != null &&
			    this is
			    {
				    ControlledOpportunityCompleted: false,
				    ControlledDispatchBarrierActive: true
			    } barrierRuntime)
			{
				EnsureControlledDispatchOutsidePassage(
					dd,
					barrierRequired: true,
					"controlled positive 敏慧 capture");
				_status = ControlledDispatchBarrierActive
					? "Controlled PT: relocating away from passage before capture"
					: "Controlled PT: passage dispatch barrier cleared";
				return true;
			}

			if (_ctx?.ControlledPtSurvey != null &&
			    this is
			    {
				    ControlledOpportunityCompleted: false,
				    ControlledPositiveCapturePending: true
			    } controlledRuntime)
			{
				if (ControlledSightConfirmed)
					UpdateControlledCandidateCoverageMovement(dd);
				else
					CancelActiveMovement();
				return true;
			}

        return false;
    }

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
    private HashSet<ControlledTrapWitnessKey> NaturalObservedTrapWitnesses { get; } = [];
    private float NaturalMaximumTrapWitnessDistance { get; set; }
    private bool NaturalJointScanComplete { get; set; }
    public bool NaturalPoisonfruitAttempted { get; private set; }
    public bool NaturalMazerootAttemptedOrAdopted { get; private set; }
    private bool ControlledSightDispatched { get; set; }
    private long ControlledSightLogSequenceAtDispatch { get; set; }
    private long ControlledMazerootLogSequenceAtDispatch { get; set; }
    private ControlledPtSurveyItemAction ControlledCaptureItem { get; set; }
    private DateTime ControlledSightDispatchedAt { get; set; }
    private bool ControlledStrengthHandled { get; set; }
    private bool ControlledPoisonfruitDispatched { get; set; }
    private bool ControlledPendingPostCapturePoisonfruit { get; set; }
    private bool ControlledOpportunityCompleted { get; set; }
    private bool ControlledPositiveCapturePending { get; set; }
    private int ControlledHoardRoomIndex { get; set; } = -1;
    private Vector3 ControlledHoardPosition { get; set; }
    private bool ControlledHoardPositionResolved { get; set; }
    public bool ControlledSightConfirmed { get; private set; }
    private long ControlledSightConfirmedAtMilliseconds { get; set; }
    private long ControlledSightConfirmationRefreshSequence { get; set; }
    private long ControlledSightConfirmationFullScanCount { get; set; }
    private bool ControlledCandidateUniverseResolved { get; set; }
    private RawWorldPosition[] ControlledCandidateUniverse { get; set; } = [];
    private bool ControlledHoardRoomTargetReached { get; set; }
    private long ControlledHoardRoomTargetRefreshSequence { get; set; }
    private long ControlledHoardRoomTargetFullScanCount { get; set; }
    private HashSet<ControlledTrapWitnessKey> ControlledObservedTrapWitnesses { get; } = [];
    private float ControlledMaximumTrapWitnessDistance { get; set; }
    /// <summary>
    /// TEMPORARY controlled-survey research only: fixed PalacePal candidate audit universe.
    /// </summary>
    public bool ControlledCandidateObjectAuditArmed { get; private set; }
    public ControlledCandidateAuditPoint[] ControlledCandidateObjectAuditUniverse { get; private set; } = [];
    private HashSet<ControlledCandidateObjectAuditKey> ControlledCandidateObjectAuditLoggedKeys { get; } = [];
    private long ControlledIntuitionExpectationStartedAtMilliseconds { get; set; }
    private long ControlledIntuitionExpectationAttemptId { get; set; }
    private bool ControlledIntuitionRequiresCurrentUse { get; set; }
    public bool ControlledIntuitionResolved { get; private set; }
    private bool ControlledIntuitionResolutionPending { get; set; }
    private ControlledPtIntuitionResolutionDecision? ControlledIntuitionDecision { get; set; }
    private long InheritedIntuitionArmedAtMilliseconds { get; set; }
    public long InheritedIntuitionAttemptId { get; private set; }
    private InheritedIntuitionEvidenceKind InheritedIntuitionEvidence { get; set; }
    public InheritedIntuitionResolutionDecision? InheritedIntuitionDecision { get; private set; }
    private bool ControlledPositiveMessagePendingIndicator { get; set; }
    private int ControlledIndicatorRoomCursor { get; set; }
    public bool ControlledDispatchBarrierActive { get; private set; }
    private bool ControlledDispatchRelocationStarted { get; set; }

    public void OpenEvidenceSession(FloorEvidenceSession session) => EvidenceSession = session;
    public void ConfigureCurrentIntuitionUse(bool required) => ControlledIntuitionRequiresCurrentUse = required;
    public void BeginInheritedIntuition()
    {
        InheritedIntuitionArmedAtMilliseconds = Environment.TickCount64;
        InheritedIntuitionAttemptId = _chatWatchers?.ExpectInheritedIntuitionResult(_floor) ?? 0;
    }
    public void ObserveConfirmedIntuition(PendingFloorItemUse pending)
    {
        ControlledIntuitionExpectationStartedAtMilliseconds = pending.IntuitionExpectedAtMilliseconds;
        ControlledIntuitionExpectationAttemptId = pending.IntuitionAttemptId;
        ControlledIntuitionResolved = false;
        ControlledIntuitionDecision = null;
    }
    public void MarkControlledStrengthHandled() => ControlledStrengthHandled = true;
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
            case FloorItemUsePurpose.ControlledPoisonfruit:
                ControlledPoisonfruitDispatched = true;
                ControlledPendingPostCapturePoisonfruit = false;
                _objectEvidence.Invalidate();
                break;
        }
    }
    public void ObserveExhaustedItem(FloorItemUsePurpose purpose)
    {
        switch (purpose)
        {
            case FloorItemUsePurpose.ControlledStrength:
                ControlledStrengthHandled = true;
                break;
            case FloorItemUsePurpose.NaturalPoisonfruit:
                // Preserve the existing floor policy: an exhausted Poisonfruit does not fall back to 敏慧.
                NaturalPoisonfruitAttempted = true;
                break;
            case FloorItemUsePurpose.NaturalPassageMazeroot:
                NaturalMazerootAttemptedOrAdopted = true;
                break;
            case FloorItemUsePurpose.ControlledPoisonfruit:
                ControlledPendingPostCapturePoisonfruit = false;
                break;
        }
    }
    public void InitializeNaturalInventory()
    {
			if (_ctx?.ControlledPtSurvey == null &&
			    _isNormal)
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

    public void ObserveInheritedMessage(ChatWatchers.StateChangedInfo info)
    {
			bool inheritedMessage =
				info.Reason is "LogMessage7272" or "LogMessage7273" or
					"LogMessage7272Rejected" or "LogMessage7273Rejected";
			if (inheritedMessage &&
			    info.EvidenceExpectationKind == IntuitionEvidenceExpectationKind.InheritedFloorResult &&
			    !_disposed &&
			    _isNormal &&
			    InheritedIntuitionDecision == null &&
			    InheritedIntuitionAttemptId == info.EvidenceAttemptId &&
			    _floor == info.EvidenceTargetFloor)
			{
				InheritedIntuitionEvidence = !info.EvidenceAccepted
					? InheritedIntuitionEvidenceKind.Rejected
					: info.Reason == "LogMessage7272"
						? InheritedIntuitionEvidenceKind.HoardPresent
						: InheritedIntuitionEvidenceKind.NoHoard;
			}

    }

    private readonly record struct ControlledTrapWitnessKey(
    	ulong GameObjectId,
    	uint BaseId,
    	int X,
    	int Y,
    	int Z)
    {
    	public static ControlledTrapWitnessKey From(in FloorObjectEvidence evidence)
    	{
    		const float normalization = 10f;
    		return new ControlledTrapWitnessKey(
    			evidence.GameObjectId,
    			evidence.BaseId,
    			(int)MathF.Round(evidence.Position.X * normalization),
    			(int)MathF.Round(evidence.Position.Y * normalization),
    			(int)MathF.Round(evidence.Position.Z * normalization));
    	}
    }

    /// <summary>
    /// TEMPORARY controlled-survey research only: dedupe key for once-per-floor
    /// controlled-candidate-object-observed recorder events.
    /// </summary>
    private readonly record struct ControlledCandidateObjectAuditKey(
    	int CandidateRoomIndex,
    	int SourceCandidateIndex,
    	ushort ObjectIndex,
    	ulong GameObjectId,
    	uint EntityId,
    	uint BaseId,
    	string ObjectKind,
    	byte SubKind,
    	uint LayoutId,
    	uint GimmickId,
    	uint NameId)
    {
    	public static ControlledCandidateObjectAuditKey From(in ControlledCandidateObjectMatch match) =>
    		new(
    			match.CandidateRoomIndex,
    			match.SourceCandidateIndex,
    			match.ObjectIndex,
    			match.GameObjectId,
    			match.EntityId,
    			match.BaseId,
    			match.ObjectKind,
    			match.SubKind,
    			match.LayoutId,
    			match.GimmickId,
    			match.NameId ?? 0u);
    }

    /// <summary>
    /// TEMPORARY controlled-survey research only: build a fixed PalacePal candidate audit
    /// universe for all reachable rooms once graph/executor exist. Position coincidence only;
    /// does not infer from Sight timing / load distance.
    /// </summary>
    public unsafe void TryArmControlledCandidateObjectAudit(
    	InstanceContentDeepDungeon* dd)
    {
    	if (_ctx?.ControlledPtSurvey == null ||
    	    !_isNormal ||
    	    ControlledCandidateObjectAuditArmed)
    	{
    		return;
    	}

    	var graph = _normalGraph;
    	var executor = _executor;
    	if (graph == null || executor == null)
    		return;

    	var universe = new List<ControlledCandidateAuditPoint>();
    	IReadOnlyList<int> rooms = graph.ReachableRooms;
    	for (int roomOffset = 0; roomOffset < rooms.Count; roomOffset++)
    	{
    		int roomIndex = rooms[roomOffset];
    		IReadOnlyList<Vector3> candidates =
    			executor.GetPalacePalCandidatesForRoom(dd, roomIndex);
    		for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
    		{
    			Vector3 candidate = candidates[candidateIndex];
    			var position = new RawWorldPosition(candidate.X, candidate.Y, candidate.Z);
    			bool duplicate = false;
    			for (int existing = 0; existing < universe.Count; existing++)
    			{
    				if (!RawWorldPosition.CanonicallyEquals(universe[existing].Position, position))
    					continue;
    				duplicate = true;
    				break;
    			}
    			if (duplicate)
    				continue;

    			universe.Add(new ControlledCandidateAuditPoint(
    				roomIndex,
    				candidateIndex,
    				position));
    		}
    	}

    	ControlledCandidateObjectAuditUniverse = universe.ToArray();
    	ControlledCandidateObjectAuditArmed = true;
    	RecordReplayEvent("controlled-candidate-object-audit-armed", new
    	{
    		floor = _floor,
    		floorGeneration = _generation,
    		candidateCount = universe.Count,
    		roomCount = rooms.Count
    	});
    }

    /// <summary>
    /// TEMPORARY controlled-survey research only: log each unique candidate+object signature
    /// once per FloorRuntime into the run recorder.
    /// </summary>
    public void ObserveControlledCandidateObjectMatches(
    	in FloorObjectEvidenceRefreshResult refresh)
    {
    	if (_ctx?.ControlledPtSurvey == null ||
    	    !refresh.ScanCompleted ||
    	    refresh.Snapshot?.ControlledCandidateObjectMatches is not { Count: > 0 } matches)
    	{
    		return;
    	}

    	for (int i = 0; i < matches.Count; i++)
    	{
    		ControlledCandidateObjectMatch match = matches[i];
    		var key = ControlledCandidateObjectAuditKey.From(match);
    		if (!ControlledCandidateObjectAuditLoggedKeys.Add(key))
    			continue;

    		float dx = match.ObjectPosition.X - match.CandidatePosition.X;
    		float dy = match.ObjectPosition.Y - match.CandidatePosition.Y;
    		float dz = match.ObjectPosition.Z - match.CandidatePosition.Z;
    		float matchDistance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    		RecordReplayEvent("controlled-candidate-object-observed", new
    		{
    			floor = _floor,
    			floorGeneration = _generation,
    			candidateRoomIndex = match.CandidateRoomIndex,
    			sourceCandidateIndex = match.SourceCandidateIndex,
    			candidatePosition = new
    			{
    				match.CandidatePosition.X,
    				match.CandidatePosition.Y,
    				match.CandidatePosition.Z
    			},
    			objectPosition = new
    			{
    				match.ObjectPosition.X,
    				match.ObjectPosition.Y,
    				match.ObjectPosition.Z
    			},
    			matchDistance,
    			match.ObjectIndex,
    			match.GameObjectId,
    			match.EntityId,
    			match.BaseId,
    			match.ObjectKind,
    			match.SubKind,
    			match.Name,
    			match.NameId,
    			match.LayoutId,
    			match.GimmickId,
    			match.IsTargetable,
    			match.IsDead,
    			match.HitboxRadius,
    			match.CurrentDistance
    		});
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
    		_ctx?.ControlledPtSurvey != null ||
    		naturalPtStonesSupported
    			? _pomanderManager.GetStoneCount(2)
    			: 0;
    	int effectiveMazerootStock = naturalPtStonesSupported
    		? _items.GetStoneCountAvailableForFloorUse(2)
    		: 0;
    	bool sightTrapObserved = snapshot.SightTrapIndicators.Count > 0;
    	if (sightTrapObserved)
    	{
    		_executor?.ObserveSightTrapIndicators(snapshot);
    		_chatWatchers?.ConfirmSightThisFloor("SightTrapIndicatorObserved");
    	}
    	bool sightConfirmed = _chatWatchers?.SightState == SightUseState.Confirmed;
    	if (_ctx?.ControlledPtSurvey != null)
    		TryConfirmControlledAuthoritativeReveal();
    	else
    	{
    		ObserveNaturalRevealInventory(sightStock,
    			mazerootStock);
    		TryAdoptExternalNaturalReveal(sightConfirmed);
    		TryConfirmNaturalAuthoritativeReveal(sightTrapObserved);
    	}
    	session?.ObserveEffectStates(
    		_nativeIntuitionActive,
    		intuitionStock,
    		sightConfirmed,
    		sightStock,
    		"scheduled-object-evidence-refresh");
    	session?.ObserveObjectEvidence(dd, snapshot);

    	if (_ctx?.ControlledPtSurvey != null)
    	{
    		UpdateControlledPtSurvey(dd, snapshot, intuitionStock, sightStock, sightConfirmed);
    		return;
    	}

    	var intuitionResolution = _chatWatchers?.ChatSaysHoard == true
    		? IntuitionFloorResolution.Positive
    		: _chatWatchers?.ChatSaysNoHoard == true
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

    	if (_executor?.CachedHoardIndicatorPos is { } cachedPosition)
    		return cachedPosition;

    	if (!BandedChestLocator.TryFindNearestToPlayer(snapshot, out Vector3? bandedPosition) ||
    		!bandedPosition.HasValue ||
    		!IsPalacePalCandidatePosition(dd, bandedPosition.Value))
    	{
    		return null;
    	}

    	fromBandedChest = true;
    	return bandedPosition.Value;
    }

    private unsafe bool IsPalacePalCandidatePosition(
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
    		_executor?.GetPalacePalCandidatesForRoom(dd, roomIndex);
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

    		IReadOnlyList<Vector3>? palacePalCandidates =
    			_executor?.GetPalacePalCandidatesForRoom(dd, hoardRoomIndex);
    		if (palacePalCandidates == null || palacePalCandidates.Count == 0)
    			return;

    		var candidates = new RawWorldPosition[palacePalCandidates.Count];
    		for (int i = 0; i < palacePalCandidates.Count; i++)
    		{
    			Vector3 candidate = palacePalCandidates[i];
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
    			    ControlledTrapWitnessKey.From(trap)))
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
    		ControlledPtSurveyPolicy.GetProvenTrapLoadSafeRadius(
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
    		ControlledPtSurveyPolicy.AreAllCandidatesCovered(
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

    private bool TryConfirmControlledAuthoritativeReveal()
    {
    	if (ControlledSightConfirmed)
    		return true;

    	bool confirmed = ControlledPtSurveyPolicy.IsAuthoritativeCaptureReveal(
    		ControlledCaptureItem,
    		(_chatWatchers?.SightLogSequence ?? 0) >
    			ControlledSightLogSequenceAtDispatch,
    		(_chatWatchers?.MazerootLogSequence ?? 0) >
    			ControlledMazerootLogSequenceAtDispatch);
    	if (!confirmed)
    		return false;

    	ControlledSightConfirmed = true;
    	ControlledSightConfirmedAtMilliseconds = Environment.TickCount64;
    	ControlledSightConfirmationRefreshSequence =
    		_objectEvidence.Current?.RefreshSequence ?? _objectEvidence.RefreshCount;
    	ControlledSightConfirmationFullScanCount = _objectEvidence.FullScanCount;
    	EvidenceSession?.ObserveAuthoritativeRevealConfirmed();
    	return true;
    }

    private unsafe void UpdateControlledPtSurvey(
    	InstanceContentDeepDungeon* dd,
    	FloorObjectEvidenceSnapshot snapshot,
    	int intuitionStock,
    	int sightStock,
    	bool sightActive)
    {
    	var survey = _ctx?.ControlledPtSurvey;
    	var evidence = EvidenceSession;
    	if (survey == null)
    		return;

    	int mazerootCount = _pomanderManager.GetStoneCount(2);
    	int poisonfruitCount = _pomanderManager.GetStoneCount(1);
    	int effectiveSightStock =
    		DeepDungeonFloorItemUsePolicy.CanUsePomanders(
    			dd->DeepDungeonBanId) &&
    		!_items.IsPomanderBlockedForFloor(
    			FloorInitPlanner.SightPomanderSlotIndex)
    			? sightStock
    			: 0;
    	int effectiveMazerootCount =
    		DeepDungeonFloorItemUsePolicy.CanUsePtIncense(
    			dd->DeepDungeonBanId) &&
    		!_items.IsStoneBlockedForFloor(2)
    			? mazerootCount
    			: 0;
    	int effectivePoisonfruitCount =
    		DeepDungeonFloorItemUsePolicy.CanUsePtIncense(
    			dd->DeepDungeonBanId) &&
    		!_items.IsStoneBlockedForFloor(1)
    			? poisonfruitCount
    			: 0;
    	ProcessPendingControlledPostCapturePoisonfruit(
    		dd,
    		effectivePoisonfruitCount);
    	if (TryUseControlledStrength(dd))
    		return;
    	if (ControlledOpportunityCompleted)
    		return;

    	evidence?.ConfigureControlledSurvey(
    		ControlledPtSurveyPolicy.IsResearchFloor(dd->Floor)
    			? ControlledSurveyFloorRole.SelectedTarget
    			: ControlledSurveyFloorRole.Transit,
    		survey.ResearchFloors);

    	if (ControlledIntuitionRequiresCurrentUse &&
    	    ControlledIntuitionExpectationStartedAtMilliseconds == 0)
    	{
    		bool firstFloor = dd->Floor == ControlledPtSurveyPolicy.FirstFloor;
    		if (firstFloor &&
    		    !ControlledPtSurveyPolicy.HasSightCapableResource(sightStock, mazerootCount))
    		{
    			survey.Fail("Controlled PT capture requires at least one Sight or 敏慧 before arming Intuition on floor 21.");
    			survey.RequestSuccessfulLeave();
    			return;
    		}
    		if (firstFloor && _nativeIntuitionActive)
    		{
    			survey.Fail("Controlled PT floor 21 must arm Intuition only after its explicit current-floor dispatch.");
    			survey.RequestSuccessfulLeave();
    			return;
    		}
    		if (!firstFloor && intuitionStock <= 0)
    		{
    			FailControlledInheritedState($"Controlled PT floor {dd->Floor} lost Intuition with no remaining stock to reactivate it.");
    			return;
    		}
    		if (!_items.CanAttemptPomanderUse())
    			return;
    		if (!_items.IsPomanderAvailableForFloorUse(FloorInitPlanner.IntuitionPomanderSlotIndex))
    		{
    			if (firstFloor)
    			{
    				survey.Fail("Controlled PT capture requires one usable Intuition on floor 21.");
    				survey.RequestSuccessfulLeave();
    				return;
    			}
    			_status = $"Controlled PT: waiting to reactivate Intuition on floor {dd->Floor}";
    			return;
    		}
    		_items.TryUsePomander(
    			FloorInitPlanner.IntuitionPomanderSlotIndex,
    			dd,
    			firstFloor
    				? "controlled persistent intuition"
    				: "controlled Intuition reactivation");
    		return;
    	}

    	ControlledPtIntuitionResolutionDecision intuitionDecision;
    	if (ControlledIntuitionRequiresCurrentUse)
    	{
    		if (ControlledIntuitionExpectationStartedAtMilliseconds == 0)
    		{
    			_status = "Controlled PT: waiting for correlated Intuition expectation";
    			return;
    		}

    		int intuitionElapsedMilliseconds = (int)Math.Clamp(
    			Environment.TickCount64 - ControlledIntuitionExpectationStartedAtMilliseconds,
    			0L,
    			int.MaxValue);
    		intuitionDecision = ControlledIntuitionDecision ??
    			ControlledPtSurveyPolicy.ResolveCurrentIntuition(
    				_chatWatchers?.ChatSaysHoard == true,
    				_chatWatchers?.ChatSaysNoHoard == true,
    				intuitionElapsedMilliseconds,
    				IntuitionResolutionWindowMilliseconds);
    	}
    	else
    	{
    		if (!InheritedIntuitionDecision.HasValue)
    		{
    			int elapsedMilliseconds = InheritedIntuitionArmedAtMilliseconds > 0
    				? (int)Math.Clamp(
    					Environment.TickCount64 - InheritedIntuitionArmedAtMilliseconds,
    					0L,
    					int.MaxValue)
    				: 0;
    			ControlledIntuitionResolutionPending = true;
    			_status =
    				$"Controlled PT: waiting for inherited Intuition result ({elapsedMilliseconds}/{IntuitionResolutionWindowMilliseconds}ms)";
    			return;
    		}

    		var inheritedDecision = InheritedIntuitionDecision.Value;
    		var source = inheritedDecision.Source switch
    		{
    			InheritedIntuitionResolutionSource.HoardPresent =>
    				ControlledPtIntuitionResolutionSource.InheritedHoardPresent,
    			InheritedIntuitionResolutionSource.NoHoardInferred =>
    				ControlledPtIntuitionResolutionSource.InheritedNoHoardInferred,
    			InheritedIntuitionResolutionSource.InvalidNoHoardMessage =>
    				ControlledPtIntuitionResolutionSource.InvalidInheritedNoHoardMessage,
    			InheritedIntuitionResolutionSource.RejectedEvidence =>
    				ControlledPtIntuitionResolutionSource.RejectedInheritedEvidence,
    			_ => ControlledPtIntuitionResolutionSource.None
    		};
    		intuitionDecision = new ControlledPtIntuitionResolutionDecision(
    			inheritedDecision.Terminal,
    			inheritedDecision.HoardPresent,
    			inheritedDecision.NoHoard,
    			inheritedDecision.IsError,
    			source,
    			inheritedDecision.ElapsedMilliseconds);
    	}
    	if (!intuitionDecision.Terminal)
    	{
    		ControlledIntuitionResolutionPending = true;
    		_status = "Controlled PT: waiting for Intuition result";
    		return;
    	}
    	ControlledIntuitionResolutionPending = false;

    	if (!ControlledIntuitionResolved)
    	{
    		_chatWatchers?.CancelExpectedIntuitionResult(ControlledIntuitionExpectationAttemptId);
    		_items.PendingIntuition.CancelAttempt(ControlledIntuitionExpectationAttemptId);
    		ControlledIntuitionResolved = true;
    		ControlledIntuitionDecision = intuitionDecision;
    		evidence?.ObserveControlledIntuitionResolution(
    			intuitionDecision.Source,
    			intuitionDecision.ElapsedMilliseconds,
    			IntuitionResolutionWindowMilliseconds);
    	}
    	if (intuitionDecision.IsError)
    	{
    		survey.Fail($"Controlled PT floor {dd->Floor} Intuition result failed: {intuitionDecision.Source}.");
    		survey.RequestSuccessfulLeave();
    		return;
    	}

    	if (intuitionDecision.NoHoard)
    	{
    		CompleteControlledOpportunity(
    			dd,
    			intuitionDecision.Source == ControlledPtIntuitionResolutionSource.InheritedNoHoardInferred
    				? ControlledPtSurveyTargetOutcome.InheritedNoHoardInferred
    				: ControlledPtSurveyTargetOutcome.IntuitionNegative,
    			sightStock,
    			mazerootCount,
    			poisonfruitCount);
    		return;
    	}

    	bool hasIndicator = snapshot.HoardIndicators.Count > 0;
    	var indicatorAction = ControlledPtSurveyPolicy.DecidePositiveIndicatorAction(
    		ControlledHoardPositionResolved,
    		hasIndicator);
    	if (indicatorAction == ControlledPtPositiveIndicatorAction.AcquireExactIndicator)
    	{
    		ControlledPositiveMessagePendingIndicator = true;
    		_status = "Controlled PT: positive Intuition result; acquiring exact indicator";
    		return;
    	}
    	ControlledPositiveMessagePendingIndicator = false;

    	if (indicatorAction == ControlledPtPositiveIndicatorAction.ContinueCapture)
    	{
    		if (!ControlledHoardPositionResolved)
    		{
    			var hoardPosition = snapshot.HoardIndicators[0].Object.Position;
    			var rooms = _normalGraph?.ReachableRooms;
    			int hoardRoom = rooms == null
    				? -1
    				: RoomGraph.GetRoomIndexForPosition(
    					dd,
    					hoardPosition,
    					rooms,
    					-1);
    			if (hoardRoom < 0)
    			{
    				CompleteControlledJointSampleIncomplete(
    					dd,
    					$"Controlled PT floor {dd->Floor} could not resolve the exact H coordinate to a reachable room.");
    				return;
    			}
    			ControlledHoardRoomIndex = hoardRoom;
    			ControlledHoardPosition = hoardPosition;
    			ControlledHoardPositionResolved = true;
    		}

    		ControlledPositiveCapturePending = true;
    		if (!sightActive && !ControlledSightDispatched)
    		{
    			if (!_items.CanAttemptPomanderUse())
    				return;
    			var action = ControlledPtSurveyPolicy.DecidePositiveCaptureItem(
    				dd->Floor,
    				sightActive,
    				effectiveSightStock,
    				effectiveMazerootCount);
    			if (action == ControlledPtSurveyItemAction.UseSight &&
    			    !_items.IsPomanderAvailableForFloorUse(
    				    FloorInitPlanner.SightPomanderSlotIndex))
    			{
    				_status =
    					"Controlled PT: waiting for selected Sight to become usable";
    				return;
    			}
    			if (action == ControlledPtSurveyItemAction.UseMazeroot &&
    			    !EnsureControlledDispatchOutsidePassage(
    				    dd,
    				    barrierRequired: true,
    				    "controlled positive 敏慧 capture"))
    			{
    				return;
    			}
    			bool dispatched = action switch
    			{
    				ControlledPtSurveyItemAction.UseMazeroot => TryUseControlledStone(
    					2,
    					dd,
    					"controlled positive capture with 敏慧"),
    				ControlledPtSurveyItemAction.UseSight => _items.TryUsePomander(
    					FloorInitPlanner.SightPomanderSlotIndex,
    					dd,
    					"controlled positive capture with Sight",
    					FloorItemUsePurpose.ControlledReveal),
    				_ => false
    			};
    			if (!dispatched)
    			{
    				CompleteControlledJointSampleIncomplete(
    					dd,
    					$"Controlled PT floor {dd->Floor} has an exact hoard indicator but no Sight-capable resource could be dispatched.");
    				return;
    			}
    			return;
    		}

    		bool authoritativeRevealConfirmed =
    			TryConfirmControlledAuthoritativeReveal();
    		if (authoritativeRevealConfirmed)
    		{
    			if (snapshot.PlayerPosition is not { } scanPlayerPosition)
    			{
    				_status = "Controlled PT: waiting for scan-captured player position";
    				return;
    			}

    			if (!ControlledCandidateUniverseResolved)
    			{
    				var palacePalCandidates =
    					_executor?.GetPalacePalCandidatesForRoom(
    						dd,
    						ControlledHoardRoomIndex);
    				if (palacePalCandidates == null || palacePalCandidates.Count == 0)
    				{
    					CompleteControlledJointSampleIncomplete(
    						dd,
    						$"Controlled PT floor {dd->Floor} has no PalacePal T/H candidate universe for H room {ControlledHoardRoomIndex}.");
    					return;
    				}

    				var candidates = new RawWorldPosition[palacePalCandidates.Count];
    				for (int i = 0; i < palacePalCandidates.Count; i++)
    				{
    					var candidate = palacePalCandidates[i];
    					candidates[i] = new RawWorldPosition(
    						candidate.X,
    						candidate.Y,
    						candidate.Z);
    				}
    				ControlledCandidateUniverse = candidates;
    				ControlledCandidateUniverseResolved = true;
    			}

    			for (int i = 0; i < snapshot.SightTrapIndicators.Count; i++)
    			{
    				var trap = snapshot.SightTrapIndicators[i];
    				if (!ControlledObservedTrapWitnesses.Add(
    					    ControlledTrapWitnessKey.From(trap)))
    				{
    					continue;
    				}

    				float dx = trap.Position.X - scanPlayerPosition.X;
    				float dz = trap.Position.Z - scanPlayerPosition.Z;
    				float firstAppearanceDistance = MathF.Sqrt(dx * dx + dz * dz);
    				ControlledMaximumTrapWitnessDistance = MathF.Max(
    					ControlledMaximumTrapWitnessDistance,
    					firstAppearanceDistance);
    				RecordReplayEvent("controlled-trap-load-witness", new
    				{
    					floor = dd->Floor,
    					trap.BaseId,
    					trap.GameObjectId,
    					trap.Position,
    					playerPosition = scanPlayerPosition,
    					firstAppearanceDistance,
    					provenSafeRadius =
    						ControlledPtSurveyPolicy.GetProvenTrapLoadSafeRadius(
    							ControlledMaximumTrapWitnessDistance)
    				});
    			}

    			float safeRadius =
    				ControlledPtSurveyPolicy.GetProvenTrapLoadSafeRadius(
    					ControlledMaximumTrapWitnessDistance);
    			bool trapWitnessAvailable =
    				ControlledObservedTrapWitnesses.Count > 0 &&
    				safeRadius > 0f;
    			var rawPlayerPosition = new RawWorldPosition(
    				scanPlayerPosition.X,
    				scanPlayerPosition.Y,
    				scanPlayerPosition.Z);
    			bool allCandidatesCovered =
    				trapWitnessAvailable &&
    				ControlledPtSurveyPolicy.AreAllCandidatesCovered(
    					rawPlayerPosition,
    					ControlledCandidateUniverse,
    					safeRadius);
    			bool synchronizedScanAvailable =
    				snapshot.Available &&
    				snapshot.RefreshSequence >
    					ControlledSightConfirmationRefreshSequence &&
    				_objectEvidence.FullScanCount >
    					ControlledSightConfirmationFullScanCount;
    			bool postArrivalScanAvailable =
    				ControlledHoardRoomTargetReached &&
    				snapshot.Available &&
    				snapshot.RefreshSequence >
    					Math.Max(
    						ControlledSightConfirmationRefreshSequence,
    						ControlledHoardRoomTargetRefreshSequence) &&
    				_objectEvidence.FullScanCount >
    					Math.Max(
    						ControlledSightConfirmationFullScanCount,
    						ControlledHoardRoomTargetFullScanCount);
    			var jointAction = ControlledPtSurveyPolicy.DecideJointCapture(
    				authoritativeRevealConfirmed,
    				ControlledCandidateUniverse.Length > 0,
    				trapWitnessAvailable,
    				allCandidatesCovered,
    				synchronizedScanAvailable,
    				ControlledHoardRoomTargetReached,
    				postArrivalScanAvailable);
    			if (jointAction == ControlledPtJointCaptureAction.Complete)
    			{
    				CancelActiveMovement();
    				CompleteControlledOpportunity(
    					dd,
    					ControlledPtSurveyTargetOutcome.PositiveCaptured,
    					sightStock,
    					mazerootCount,
    					poisonfruitCount);
    				return;
    			}

    			if (jointAction == ControlledPtJointCaptureAction.Incomplete)
    			{
    				CompleteControlledJointSampleIncomplete(
    					dd,
    					trapWitnessAvailable
    						? $"Controlled PT floor {dd->Floor} T witness radius {safeRadius:F1}m did not cover every PalacePal candidate after reaching H room {ControlledHoardRoomIndex}."
    						: $"Controlled PT floor {dd->Floor} obtained no T visibility witness after reaching H room {ControlledHoardRoomIndex}.");
    				return;
    			}

    			_status = jointAction == ControlledPtJointCaptureAction.WaitForHoardRoomScan
    				? $"Controlled PT: waiting for synchronized scan in H room {ControlledHoardRoomIndex}"
    				: $"Controlled PT: approaching H room {ControlledHoardRoomIndex}";
    		}
    		else if (ControlledSightDispatched &&
    		         DateTime.UtcNow - ControlledSightDispatchedAt >= TimeSpan.FromSeconds(5))
    		{
    			CompleteControlledJointSampleIncomplete(
    				dd,
    				$"Controlled PT floor {dd->Floor} did not expose authoritative reveal confirmation after the capture item was dispatched.");
    		}
    		return;
    	}

    }

    private unsafe void CompleteControlledOpportunity(
    	InstanceContentDeepDungeon* dd,
    	ControlledPtSurveyTargetOutcome outcome,
    	int sightStock,
    	int mazerootCount,
    	int poisonfruitCount)
    {
    	var survey = _ctx?.ControlledPtSurvey;
    	if (survey == null || ControlledOpportunityCompleted)
    		return;

    	ControlledOpportunityCompleted = true;
    	ControlledPositiveCapturePending = false;
    	ControlledPositiveMessagePendingIndicator = false;
    	ControlledDispatchBarrierActive = false;
    	EvidenceSession?.ObserveControlledOutcome(outcome);
    	if (!PersistControlledFloorBeforeLeave($"target-terminal:{outcome}"))
    	{
    		survey.Fail($"Controlled PT floor {dd->Floor} evidence could not be persisted.");
    		survey.RequestSuccessfulLeave();
    		CancelActiveMovement();
    		return;
    	}
    	var decision = ControlledPtSurveyPolicy.DecideFloorAction(
    		dd->Floor,
    		outcome,
    		sightStock,
    		mazerootCount,
    		poisonfruitCount);

    	if (!decision.ShouldAbandon)
    	{
    		bool negativeOutcome =
    			outcome is ControlledPtSurveyTargetOutcome.IntuitionNegative or
    				ControlledPtSurveyTargetOutcome.InheritedNoHoardInferred;
    		bool sightCaptureOutcome =
    			(outcome is ControlledPtSurveyTargetOutcome.PositiveCaptured or
    				ControlledPtSurveyTargetOutcome.PositiveJointSampleIncomplete) &&
    			ControlledCaptureItem == ControlledPtSurveyItemAction.UseSight;
    		if (negativeOutcome || sightCaptureOutcome)
    		{
    			ControlledPendingPostCapturePoisonfruit = true;
    			ProcessPendingControlledPostCapturePoisonfruit(dd, poisonfruitCount);
    		}
    		return;
    	}

    	survey.RequestSuccessfulLeave();
    	CancelActiveMovement();
    }

    private unsafe void UpdateControlledCandidateCoverageMovement(
    	InstanceContentDeepDungeon* dd)
    {
    	var player = Service.LocalPlayer;
    	if (player == null)
    	{
    		_status = "Controlled PT: waiting for player before candidate coverage";
    		return;
    	}

    	if (!ControlledCandidateUniverseResolved ||
    	    ControlledCandidateUniverse.Length == 0)
    	{
    		CancelActiveMovement();
    		_status = "Controlled PT: waiting for candidate universe";
    		return;
    	}

    	int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
    	if (ControlledHoardRoomTargetReached)
    	{
    		CancelActiveMovement();
    		_status =
    			$"Controlled PT: waiting for synchronized scan in H room {ControlledHoardRoomIndex}";
    		return;
    	}

    	if (ControlledHoardRoomIndex < 0 ||
    	    !MapPos.TryGetRoomCenter(
    		    dd,
    		    ControlledHoardRoomIndex,
    		    out var hoardRoomCenter))
    	{
    		CompleteControlledJointSampleIncomplete(
    			dd,
    			$"Controlled PT floor {dd->Floor} lost the center of H room {ControlledHoardRoomIndex}.");
    		return;
    	}

    	var navigation = _navDriver?.Drive(
    		hoardRoomCenter,
    		player.Position,
    		1.2f,
    		dd,
    		playerRoom,
    		ControlledHoardRoomIndex) ?? NavDriveResult.Failed;
    	if (navigation == NavDriveResult.Failed)
    	{
    		CompleteControlledJointSampleIncomplete(
    			dd,
    			$"Controlled PT floor {dd->Floor} could not approach H room {ControlledHoardRoomIndex}.");
    		return;
    	}
    	if (navigation == NavDriveResult.Arrived)
    	{
    		CancelActiveMovement();
    		ControlledHoardRoomTargetReached = true;
    		ControlledHoardRoomTargetRefreshSequence =
    			_objectEvidence.Current?.RefreshSequence ??
    			_objectEvidence.RefreshCount;
    		ControlledHoardRoomTargetFullScanCount =
    			_objectEvidence.FullScanCount;
    		_status =
    			$"Controlled PT: scanning after reaching H room {ControlledHoardRoomIndex}";
    		return;
    	}

    	_status =
    		$"Controlled PT: approaching H room {ControlledHoardRoomIndex}";
    }

    private unsafe void CompleteControlledJointSampleIncomplete(
    	InstanceContentDeepDungeon* dd,
    	string reason)
    {
    	if (_ctx?.ControlledPtSurvey == null || ControlledOpportunityCompleted)
    		return;

    	CancelActiveMovement();
    	Service.Log.Warning($"[ControlledPT] {reason}");
    	RecordReplayEvent("controlled-joint-sample-incomplete", new
    	{
    		floor = dd->Floor,
    		ControlledHoardRoomIndex,
    		reason
    	});
    	CompleteControlledOpportunity(
    		dd,
    		ControlledPtSurveyTargetOutcome.PositiveJointSampleIncomplete,
    		_pomanderManager.GetCount(FloorInitPlanner.SightPomanderSlotIndex),
    		_pomanderManager.GetStoneCount(2),
    		_pomanderManager.GetStoneCount(1));
    }

    private void FailControlledInheritedState(string reason)
    {
    	var survey = _ctx?.ControlledPtSurvey;
    	if (survey == null || ControlledOpportunityCompleted)
    		return;

    	ControlledOpportunityCompleted = true;
    	ControlledIntuitionResolutionPending = false;
    	EvidenceSession?.ObserveControlledOutcome(
    		ControlledPtSurveyTargetOutcome.InheritedStateInconsistent);
    	if (!PersistControlledFloorBeforeLeave($"inherited-state-inconsistent:{reason}"))
    		reason += " Evidence persistence also failed.";
    	survey.Fail(reason);
    	survey.RequestSuccessfulLeave();
    	CancelActiveMovement();
    }

    private bool PersistControlledFloorBeforeLeave(string reason)
    {
    	var evidence = EvidenceSession;
    	if (evidence == null)
    		return false;

    	try
    	{
    		var bundle = evidence.Finalize($"controlled-exit:{reason}");
    		EvidenceSession = null;
    		return _floorEvidenceJournal?.EnqueueAndWait(bundle, TimeSpan.FromSeconds(2)) == true;
    	}
    	catch (Exception ex)
    	{
    		Service.Log.Error($"[FloorEvidenceJournal] Controlled pre-leave flush failed: {ex}");
    		return false;
    	}
    }

    private unsafe bool TryUseControlledStrength(
    	InstanceContentDeepDungeon* dd)
    {
    	if (ControlledStrengthHandled)
    		return false;
    	if (HasLocalPlayerStatus(StrengthStatusId))
    	{
    		ControlledStrengthHandled = true;
    		return false;
    	}
    	if (!_items.IsPomanderAvailableForFloorUse(FloorInitPlanner.StrengthPomanderSlotIndex) ||
    	    !_items.CanAttemptPomanderUse())
    	{
    		return false;
    	}
    	if (!_items.TryUsePomander(
    		    FloorInitPlanner.StrengthPomanderSlotIndex,
    		    dd,
    		    "controlled combat acceleration",
    		    FloorItemUsePurpose.ControlledStrength))
    	{
    		return false;
    	}

    	return true;
    }

    private unsafe bool TryUseControlledStone(
    	byte stoneId,
    	InstanceContentDeepDungeon* dd,
    	string reason)
    {
    	return _items.TryDispatchFloorStone(
    		stoneId,
    		dd,
    		reason,
    		stoneId == 2
    			? FloorItemUsePurpose.ControlledReveal
    			: FloorItemUsePurpose.ControlledPoisonfruit);
    }

    private void RegisterNaturalRevealDispatch(
    	SightResearchRevealResource resource,
    	long sightLogSequenceBeforeDispatch,
    	long mazerootLogSequenceBeforeDispatch)
    {
    	if (_ctx?.ControlledPtSurvey != null ||
    	    resource == SightResearchRevealResource.None ||
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
    	if (_ctx?.ControlledPtSurvey != null ||
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
    		_status = "Waiting to use 敏慧 safely away from the passage";
    		return false;
    	}

    	return _items.TryDispatchFloorStone(
    		2,
    		dd,
    		reason,
    		FloorItemUsePurpose.NaturalReveal);
    }

    private unsafe bool CanDispatchNaturalPassageOpeningStoneSafely(
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
    	ControlledPtDispatchGateAction decision =
    		ControlledPtSurveyPolicy.DecidePassageDispatchGate(
    			barrierRequired: true,
    			roomRelationAvailable,
    			roomRelationAvailable && playerRoom == passageRoom,
    			exactPassageAvailable,
    			distanceSquared);
    	return decision == ControlledPtDispatchGateAction.Allow;
    }

    public unsafe bool TryUseNaturalPassageAcceleration(
    	InstanceContentDeepDungeon* dd)
    {
    	if (_ctx?.ControlledPtSurvey != null ||
    	    !DungeonCatalog.SupportsNaturalPtStones(dd->DeepDungeonId) ||
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
    			ControlledSurveyActive: _ctx?.ControlledPtSurvey != null,
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
    	if (_ctx?.ControlledPtSurvey != null ||
    	    NaturalMazerootAttemptedOrAdopted ||
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
    		_status = "Waiting to use passage accelerator safely away from the passage";
    		return false;
    	}
    	return _items.TryDispatchFloorStone(
    		2,
    		dd,
    		reason,
    		FloorItemUsePurpose.NaturalPassageMazeroot);
    }

    private unsafe bool EnsureControlledDispatchOutsidePassage(
    	InstanceContentDeepDungeon* dd,
    	bool barrierRequired,
    	string operation)
    {
    	var player = Service.LocalPlayer;
    	var evidence = _objectEvidence.Current;
    	int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
    	int passageRoom = RoomGraph.GetPassageRoomIndex(dd);
    	bool roomRelationAvailable = playerRoom >= 0 && passageRoom >= 0;
    	Vector3 passagePosition = default;
    	bool exactPassageAvailable =
    		player != null &&
    		evidence?.Available == true &&
    		PassageLocator.TryGetPassageActorPosition(evidence, out passagePosition);
    	float distanceSquared = exactPassageAvailable && player != null
    		? Vector3.DistanceSquared(player.Position, passagePosition)
    		: 0f;
    	var decision = ControlledPtSurveyPolicy.DecidePassageDispatchGate(
    		barrierRequired,
    		roomRelationAvailable,
    		roomRelationAvailable && playerRoom == passageRoom,
    		exactPassageAvailable,
    		distanceSquared);
    	if (decision == ControlledPtDispatchGateAction.Allow)
    	{
    		if (ControlledDispatchBarrierActive)
    			CancelActiveMovement();
    		ControlledDispatchBarrierActive = false;
    		ControlledDispatchRelocationStarted = false;
    		return true;
    	}

    	ControlledDispatchBarrierActive = true;
    	if (decision == ControlledPtDispatchGateAction.WaitForExactPassage || player == null)
    	{
    		CancelActiveMovement();
    		_status = $"Controlled PT: waiting for exact passage position before {operation}";
    		return false;
    	}

    	var away = player.Position - passagePosition;
    	away.Y = 0f;
    	if (away.LengthSquared() < 0.01f)
    	{
    		if (!MapPos.TryGetRoomCenter(dd, playerRoom, out var roomCenter))
    		{
    			CancelActiveMovement();
    			_status = $"Controlled PT: cannot resolve relocation direction before {operation}";
    			return false;
    		}
    		away = roomCenter - passagePosition;
    		away.Y = 0f;
    		if (away.LengthSquared() < 0.01f)
    		{
    			CancelActiveMovement();
    			_status = $"Controlled PT: passage relocation direction is degenerate before {operation}";
    			return false;
    		}
    	}

    	away = Vector3.Normalize(away);
    	var destination = passagePosition +
    		away * (ControlledPtSurveyPolicy.PassageDispatchExclusionRadius + 1f);
    	destination.Y = player.Position.Y;
    	if (!ControlledDispatchRelocationStarted)
    	{
    		CancelActiveMovement();
    		ControlledDispatchRelocationStarted = true;
    	}
    	_navDriver?.Drive(destination, player.Position, 0.4f, dd, playerRoom, playerRoom);
    	_status = $"Controlled PT: relocating away from passage before {operation}";
    	return false;
    }

    private unsafe void TryUseControlledPoisonfruit(
    	InstanceContentDeepDungeon* dd,
    	int poisonfruitCount,
    	string reason)
    {
    	if (ControlledPoisonfruitDispatched || poisonfruitCount <= 0 || !_items.CanAttemptPomanderUse())
    		return;
    	TryUseControlledStone(1, dd, reason);
    }

    private unsafe void ProcessPendingControlledPostCapturePoisonfruit(
    	InstanceContentDeepDungeon* dd,
    	int poisonfruitCount)
    {
    	if (_items.Session.Pending?.Purpose ==
    	    FloorItemUsePurpose.ControlledPoisonfruit)
    	{
    		return;
    	}

    	var action = ControlledPtSurveyPolicy.DecidePostCaptureAcceleration(
    		ControlledPendingPostCapturePoisonfruit,
    		ControlledPoisonfruitDispatched,
    		_ctx?.Duty.PassageOpen == true,
    		poisonfruitCount,
    		_items.CanAttemptPomanderUse());
    	switch (action)
    	{
    		case ControlledPtPostCaptureAccelerationAction.Dispatch:
    			TryUseControlledStone(1, dd, "controlled post-Sight continuation acceleration");
    			break;
    		case ControlledPtPostCaptureAccelerationAction.CompleteWithoutDispatch:
    		case ControlledPtPostCaptureAccelerationAction.None:
    			ControlledPendingPostCapturePoisonfruit = false;
    			break;
    	}
    }

    public unsafe void UpdateControlledIndicatorAcquisition(
    	InstanceContentDeepDungeon* dd)
    {
    	var player = Service.LocalPlayer;
    	var rooms = _normalGraph?.ReachableRooms;
    	if (player == null || rooms == null)
    	{
    		_status = "Controlled PT: waiting for indicator acquisition geometry";
    		return;
    	}

    	while (ControlledIndicatorRoomCursor < rooms.Count &&
    	       (EvidenceSession?.HasVisitedRoom(rooms[ControlledIndicatorRoomCursor]) == true ||
    	        MapPos.TryGetRoomCenter(
    		        dd,
    		        rooms[ControlledIndicatorRoomCursor],
    		        out var coveredCenter) &&
    	        Vector3.DistanceSquared(player.Position, coveredCenter) <= 1.5f * 1.5f))
    	{
    		ControlledIndicatorRoomCursor++;
    	}

    	if (ControlledIndicatorRoomCursor >= rooms.Count)
    	{
    		CompleteControlledJointSampleIncomplete(
    			dd,
    			$"Controlled PT floor {_floor} received 7272 but the exact indicator did not load after room-center coverage.");
    		return;
    	}

    	int targetRoom = rooms[ControlledIndicatorRoomCursor];
    	NavigateToRoom(dd, targetRoom, player);
    	_status = $"Controlled PT: acquiring exact indicator via room {targetRoom}";
    }

    public void ResolveInheritedIntuition()
    {
    	if (!_isNormal ||
    	    _disposed ||
    	    InheritedIntuitionAttemptId <= 0 ||
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

    	_chatWatchers?.CancelExpectedIntuitionResult(InheritedIntuitionAttemptId);
    	InheritedIntuitionDecision = decision;
    	EvidenceSession?.ObserveInheritedIntuitionResolution(
    		decision.Source,
    		decision.ElapsedMilliseconds,
    		IntuitionResolutionWindowMilliseconds);

    	if (decision.NoHoard)
    	{
    		_executor?.MarkInheritedNoHoardInferred();
    		if (HandleNoHoardEvidenceInvalidated("inherited-no-hoard-inferred"))
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
    		attemptId = InheritedIntuitionAttemptId,
    		source = decision.Source.ToString(),
    		decision.HoardPresent,
    		decision.NoHoard,
    		decision.IsError,
    		decision.ElapsedMilliseconds,
    		windowMilliseconds = IntuitionResolutionWindowMilliseconds
    	});
    	EndHoardEvidenceWait($"inherited-intuition:{decision.Source}");
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

    public void ApplyConfirmedControlledReveal(
    	PendingFloorItemUse pending)
    {
    	var action = pending.Key.Kind == FloorItemUseKind.Pomander
    		? ControlledPtSurveyItemAction.UseSight
    		: ControlledPtSurveyItemAction.UseMazeroot;
    	ControlledCaptureItem = action;
    	ControlledSightDispatched = true;
    	ControlledSightLogSequenceAtDispatch =
    		pending.SightLogSequenceBeforeDispatch;
    	ControlledMazerootLogSequenceAtDispatch =
    		pending.MazerootLogSequenceBeforeDispatch;
    	ControlledSightDispatchedAt = pending.DispatchedAtUtc;
    	_objectEvidence.Invalidate();
    	EvidenceSession?.ObserveControlledCaptureItem(action);
    	EvidenceSession?.ObserveResearchAction(
    		true,
    		action == ControlledPtSurveyItemAction.UseMazeroot
    			? SightResearchRevealResource.Mazeroot
    			: SightResearchRevealResource.Sight,
    		pending.CountBeforeDispatch);
    }

}
