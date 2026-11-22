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
			SightUseState SightState,
			bool UsedIntuitionThisFloor,
			uint? GoldChestOvercapSlotIndex,
			long EvidenceAttemptId,
			byte EvidenceTargetFloor,
			uint? SilverChestOvercapRowId,
			byte? UsedMagiciteTypeId)
		{
			public bool EvidenceAccepted => EvidenceAttemptId != 0 && Reason is ("LogMessage7272" or "LogMessage7273");
		}

		private readonly NativeDeepDungeonLogMessageSource _logMessageSource;
		private readonly Action<NativeDeepDungeonLogMessage> _logMessageHandler;
		private long _nextEvidenceAttemptId;
		private (long Id, byte Floor) _expectedIntuition;

		public ChatWatchers(NativeDeepDungeonLogMessageSource logMessageSource)
		{
			_logMessageSource = logMessageSource ?? throw new ArgumentNullException(nameof(logMessageSource));
			_logMessageHandler = OnLogMessage;
			_logMessageSource.MessageReceived += _logMessageHandler;
		}

		public SightUseState SightState { get; private set; }
		public bool UsedIntuitionThisFloor { get; private set; }
		public long SightLogSequence { get; private set; }
		public long MazerootLogSequence { get; private set; }
		public event Action<StateChangedInfo>? StateChanged;

		public void BeginReadyFloor()
		{
			if (SightState == SightUseState.Attempted)
				Service.Log.Warning("[ChatWatchers] Sight attempt ended without confirmation before floor reset.");
			SightState = SightUseState.None;
			UsedIntuitionThisFloor = false;
			_expectedIntuition = default;
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
			if (UsedIntuitionThisFloor)
				return;

			UsedIntuitionThisFloor = true;
			NotifyStateChanged("MarkIntuitionUsedThisFloor");
		}

		public long ExpectIntuitionResult(byte floor)
		{
			long id = ++_nextEvidenceAttemptId;
			_expectedIntuition = (id, floor);
			NotifyStateChanged("IntuitionEvidenceExpected", id, floor);
			return id;
		}

		public bool CompleteExpectedIntuitionResult(long attemptId)
		{
			if (attemptId == 0 || _expectedIntuition.Id != attemptId) return false;
			_expectedIntuition = default;
			return true;
		}

		public void CancelExpectedIntuitionResult(long attemptId)
		{
			if (CompleteExpectedIntuitionResult(attemptId))
				NotifyStateChanged("IntuitionEvidenceExpectationCancelled");
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
				case 9208:
					if (message.ParameterCount > 0 && message.Value1 is >= 1 and <= 4)
						NotifyStateChanged("SilverChestOvercapObserved", silverChestOvercapRowId: message.Value1);
					break;
				case 9209:
					if (message.ParameterCount > 1 && message.Value2 is >= 1 and <= 4)
						NotifyStateChanged("MagiciteUseObserved", usedMagiciteTypeId: (byte)message.Value2);
					break;
				case 9217:
					NotifyStateChanged("FalsePassageObserved");
					break;
				case 10287:
					if (message.ParameterCount > 0 && message.Value1 is 4 or 5)
						NotifyStateChanged("SilverChestOvercapObserved", silverChestOvercapRowId: message.Value1);
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
					NotifyStateChanged("LogMessage7272", _expectedIntuition.Id, _expectedIntuition.Floor);
					break;
				case 7273:
					NotifyStateChanged("LogMessage7273", _expectedIntuition.Id, _expectedIntuition.Floor);
					break;
				case 7274:
					NotifyStateChanged("LogMessage7274Observed");
					break;
			}
		}

		private void NotifyStateChanged(
			string reason,
			long attemptId = 0,
			byte expectedFloor = 0,
			uint? goldChestOvercapSlotIndex = null,
			uint? silverChestOvercapRowId = null,
			byte? usedMagiciteTypeId = null)
		{
			StateChanged?.Invoke(new StateChangedInfo(
				reason, SightState, UsedIntuitionThisFloor, goldChestOvercapSlotIndex,
				attemptId, expectedFloor, silverChestOvercapRowId, usedMagiciteTypeId));
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

