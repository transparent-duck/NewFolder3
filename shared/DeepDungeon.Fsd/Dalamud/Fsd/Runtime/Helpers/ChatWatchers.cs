using System;
using DeepDungeon.Fsd.Core;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Helpers
{
	/// <summary>
	/// Watches deep-dungeon intuition log messages via the shared native log-message source.
	/// </summary>
	public sealed class ChatWatchers : IDisposable
	{
		public sealed record StateChangedInfo(
			string Reason,
			bool IntuitionActive,
			SightUseState SightState,
			bool UsedIntuitionThisFloor,
			bool ChatSaysHoard,
			bool ChatSaysNoHoard,
			bool HoardCofferFound,
			uint? GoldChestOvercapSlotIndex,
			bool EvidenceAccepted,
			string EvidenceDisposition,
			long EvidenceAttemptId,
			IntuitionEvidenceExpectationKind EvidenceExpectationKind,
			byte EvidenceSourceFloor,
			byte EvidenceTargetFloor);

		private readonly NativeDeepDungeonLogMessageSource _logMessageSource;
		private readonly Action<NativeDeepDungeonLogMessage> _logMessageHandler;
		private long _nextEvidenceAttemptId;
		private long _expectedEvidenceAttemptId;
		private IntuitionEvidenceExpectationKind _expectedEvidenceKind;
		private byte _expectedEvidenceSourceFloor;
		private byte _expectedEvidenceTargetFloor;

		public ChatWatchers(NativeDeepDungeonLogMessageSource logMessageSource)
		{
			_logMessageSource = logMessageSource ?? throw new ArgumentNullException(nameof(logMessageSource));
			_logMessageHandler = OnLogMessage;
			_logMessageSource.MessageReceived += _logMessageHandler;
		}

		public bool IntuitionActive { get; private set; }
		public SightUseState SightState { get; private set; }
		public bool UsedIntuitionThisFloor { get; private set; }
		public bool IntuitionUsePendingThisFloor { get; private set; }
		public bool HasCurrentFloorIntuitionUse =>
			UsedIntuitionThisFloor || IntuitionUsePendingThisFloor;
		public bool ChatSaysHoard { get; private set; }
		public bool ChatSaysNoHoard { get; private set; }
		public bool HoardCofferFound { get; private set; }
		public long SightLogSequence { get; private set; }
		public long MazerootLogSequence { get; private set; }
		public event Action<StateChangedInfo>? StateChanged;

		public void BeginReadyFloor(bool nativeIntuitionActive)
		{
			if (SightState == SightUseState.Attempted)
				Service.Log.Warning("[ChatWatchers] Sight attempt ended without confirmation before floor reset.");
			IntuitionActive = nativeIntuitionActive;
			SightState = SightUseState.None;
			UsedIntuitionThisFloor = false;
			IntuitionUsePendingThisFloor = false;
			ClearExpectedEvidence();
			ChatSaysHoard = false;
			ChatSaysNoHoard = false;
			HoardCofferFound = false;
			SightLogSequence = 0;
			MazerootLogSequence = 0;
			NotifyStateChanged("BeginReadyFloor");
		}

		public void Dispose()
		{
			if (SightState == SightUseState.Attempted)
				Service.Log.Warning("[ChatWatchers] Sight attempt ended without confirmation before watcher disposal.");
			_logMessageSource.MessageReceived -= _logMessageHandler;
		}

		public void MarkIntuitionUsedThisFloor()
		{
			if (UsedIntuitionThisFloor && !IntuitionUsePendingThisFloor)
				return;

			UsedIntuitionThisFloor = true;
			IntuitionUsePendingThisFloor = false;
			NotifyStateChanged("MarkIntuitionUsedThisFloor");
		}

		public void MarkIntuitionUsePendingThisFloor()
		{
			if (UsedIntuitionThisFloor || IntuitionUsePendingThisFloor)
				return;

			IntuitionUsePendingThisFloor = true;
			NotifyStateChanged("MarkIntuitionUsePendingThisFloor");
		}

		public void CancelPendingIntuitionUseThisFloor()
		{
			if (!IntuitionUsePendingThisFloor)
				return;

			IntuitionUsePendingThisFloor = false;
			NotifyStateChanged("CancelPendingIntuitionUseThisFloor");
		}

		public long ExpectIntuitionResult(byte sourceFloor)
		{
			return ExpectIntuitionResult(sourceFloor, IntuitionEvidenceExpectationKind.FloorResult);
		}

		public long ExpectInheritedIntuitionResult(byte floor)
		{
			return ExpectIntuitionResult(floor, IntuitionEvidenceExpectationKind.InheritedFloorResult);
		}

		private long ExpectIntuitionResult(byte sourceFloor, IntuitionEvidenceExpectationKind expectationKind)
		{
			_expectedEvidenceAttemptId = ++_nextEvidenceAttemptId;
			_expectedEvidenceKind = expectationKind;
			_expectedEvidenceSourceFloor = sourceFloor;
			_expectedEvidenceTargetFloor = sourceFloor;
			NotifyStateChanged("IntuitionEvidenceExpected", new EvidenceReceipt(
				false,
				"expecting-floor-result",
				_expectedEvidenceAttemptId,
				_expectedEvidenceKind,
				_expectedEvidenceSourceFloor,
				_expectedEvidenceTargetFloor));
			return _expectedEvidenceAttemptId;
		}

		public void CancelExpectedIntuitionResult(long attemptId)
		{
			if (attemptId == 0 || attemptId != _expectedEvidenceAttemptId ||
			    _expectedEvidenceKind == IntuitionEvidenceExpectationKind.BandedOpen)
			{
				return;
			}

			ClearExpectedEvidence();
			NotifyStateChanged("IntuitionEvidenceExpectationCancelled");
		}

		public void ExpectHoardCofferFound(byte floor)
		{
			_expectedEvidenceAttemptId = ++_nextEvidenceAttemptId;
			_expectedEvidenceKind = IntuitionEvidenceExpectationKind.BandedOpen;
			_expectedEvidenceSourceFloor = floor;
			_expectedEvidenceTargetFloor = floor;
			NotifyStateChanged("HoardCofferEvidenceExpected");
		}

		public void CancelExpectedHoardCofferFound()
		{
			if (_expectedEvidenceKind != IntuitionEvidenceExpectationKind.BandedOpen)
				return;

			ClearExpectedEvidence();
			NotifyStateChanged("HoardCofferEvidenceExpectationCancelled");
		}

		public void MarkSightAttemptedThisFloor()
		{
			SightUseState next = SightUseStateMachine.MarkAttempted(SightState);
			if (next == SightState)
				return;
			SightState = next;
			NotifyStateChanged("MarkSightAttemptedThisFloor");
		}

		public void ConfirmSightThisFloor(string reason)
		{
			if (SightState == SightUseState.Confirmed)
				return;
			SightState = SightUseStateMachine.MarkConfirmed(SightState);
			NotifyStateChanged(reason);
		}

		private void OnLogMessage(NativeDeepDungeonLogMessage message)
		{
			switch (message.Id)
			{
				case 7222:
					if (message.ParameterCount > 0 && TryMapGoldChestOvercapSlot(message.Value1, out var slotIndex))
						NotifyStateChanged("GoldChestOvercapObserved", goldChestOvercapSlotIndex: slotIndex);
					break;
				case 7256:
					SightLogSequence++;
					SightState = SightUseStateMachine.MarkConfirmed(SightState);
					NotifyStateChanged("LogMessage7256");
					break;
				case 11251:
					if (message.ParameterCount <= 1 || message.Value2 != 4)
						break;
					MazerootLogSequence++;
					SightState = SightUseStateMachine.MarkConfirmed(SightState);
					NotifyStateChanged("LogMessage11251Mazeroot");
					break;
				case 7272:
					if (!TryAcceptEvidence(IntuitionEvidenceMessageKind.HoardPresent, "LogMessage7272", out var positiveEvidence))
						break;
					IntuitionActive = true;
					ChatSaysHoard = true;
					ChatSaysNoHoard = false;
					HoardCofferFound = false;
					NotifyStateChanged("LogMessage7272", positiveEvidence);
					break;
				case 7273:
					if (!TryAcceptEvidence(IntuitionEvidenceMessageKind.NoHoard, "LogMessage7273", out var negativeEvidence))
						break;
					IntuitionActive = true;
					if (negativeEvidence.ExpectationKind != IntuitionEvidenceExpectationKind.InheritedFloorResult)
					{
						ChatSaysNoHoard = true;
						ChatSaysHoard = false;
					}
					HoardCofferFound = false;
					NotifyStateChanged("LogMessage7273", negativeEvidence);
					break;
				case 7274:
					if (!TryAcceptEvidence(IntuitionEvidenceMessageKind.HoardCofferFound, "LogMessage7274", out var cofferEvidence))
						break;
					HoardCofferFound = true;
					IntuitionActive = false;
					NotifyStateChanged("LogMessage7274", cofferEvidence);
					break;
			}
		}

		private bool TryAcceptEvidence(IntuitionEvidenceMessageKind messageKind, string reason, out EvidenceReceipt evidence)
		{
			var decision = IntuitionEvidenceAcceptancePlanner.Decide(new IntuitionEvidenceAcceptanceSnapshot(
				_expectedEvidenceKind,
				_expectedEvidenceAttemptId,
				messageKind));
			evidence = new EvidenceReceipt(
				decision.Accepted,
				decision.Reason,
				_expectedEvidenceAttemptId,
				_expectedEvidenceKind,
				_expectedEvidenceSourceFloor,
				_expectedEvidenceTargetFloor);
			if (!decision.Accepted)
			{
				Service.Log.Warning($"[ChatWatchers] Rejected {reason}: {decision.Reason}");
				NotifyStateChanged($"{reason}Rejected", evidence);
				return false;
			}

			ClearExpectedEvidence();
			return true;
		}

		private void ClearExpectedEvidence()
		{
			_expectedEvidenceAttemptId = 0;
			_expectedEvidenceKind = IntuitionEvidenceExpectationKind.None;
			_expectedEvidenceSourceFloor = 0;
			_expectedEvidenceTargetFloor = 0;
		}

		private readonly record struct EvidenceReceipt(
			bool Accepted,
			string Disposition,
			long AttemptId,
			IntuitionEvidenceExpectationKind ExpectationKind,
			byte SourceFloor,
			byte TargetFloor);

		private void NotifyStateChanged(
			string reason,
			EvidenceReceipt evidence = default,
			uint? goldChestOvercapSlotIndex = null)
		{
			StateChanged?.Invoke(new StateChangedInfo(
				reason,
				IntuitionActive,
				SightState,
				UsedIntuitionThisFloor,
				ChatSaysHoard,
				ChatSaysNoHoard,
				HoardCofferFound,
				goldChestOvercapSlotIndex,
				evidence.Accepted,
				evidence.Disposition ?? string.Empty,
				evidence.AttemptId,
				evidence.ExpectationKind,
				evidence.SourceFloor,
				evidence.TargetFloor));
		}

		private static unsafe bool TryMapGoldChestOvercapSlot(uint rawPomanderId, out uint slotIndex)
		{
			slotIndex = 0;
			if (rawPomanderId == 0)
				return false;

			var efw = EventFramework.Instance();
			var dd = efw != null ? efw->GetInstanceContentDeepDungeon() : null;
			uint dungeonId = dd != null ? dd->DeepDungeonId : 0u;

			slotIndex = rawPomanderId switch
			{
				1 or 23 => 0,   // safety / proto-safety
				2 or 24 => 1,   // sight / proto-sight
				3 or 25 => 2,   // strength / proto-strength
				4 or 26 => 3,   // steel / proto-steel
				5 or 27 => 4,   // affluence / proto-affluence
				6 or 28 => 5,   // flight / proto-flight
				7 or 29 => 6,   // alteration / proto-alteration
				8 or 30 => 7,   // purity / proto-purity
				9 or 31 => 8,   // fortune / proto-fortune
				10 or 32 => 9,  // witching / proto-witching
				11 or 33 => 10, // serenity / proto-serenity
				14 or 34 => 13, // intuition / proto-intuition
				15 or 35 => 14, // raising / proto-raising
				12 when dungeonId == 1 => 11, // rage
				13 when dungeonId == 1 => 12, // lust
				16 when dungeonId == 1 => 15, // resolution
				17 when dungeonId == 2 => 11, // frailty
				18 when dungeonId == 2 => 12, // concealment
				19 when dungeonId == 2 => 15, // petrification
				20 when dungeonId == 3 => 11, // proto-lethargy
				21 when dungeonId == 3 => 12, // proto-storms
				22 when dungeonId == 3 => 15, // proto-dread
				36 when dungeonId == 4 => 11, // haste
				37 when dungeonId == 4 => 12, // purification
				38 when dungeonId == 4 => 15, // devotion
				_ => uint.MaxValue
			};

			return slotIndex != uint.MaxValue;
		}
	}
}

