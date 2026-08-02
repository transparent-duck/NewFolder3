using System;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.Actions;
using global::Dalamud.Game.ClientState.Conditions;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Helpers
{
	/// <summary>
	/// Automatic recovery potion usage when HP drops below threshold.
	/// Extracted from FsdEngine to consolidate all in-duty logic in RunHost.
	/// </summary>
	internal sealed class RecoveryPotionHelper
	{
		private readonly FsdSettings _configuration;
		private DateTime _nextAttemptAtUtc = DateTime.MinValue;
		private DateTime _nextUnavailableLogAtUtc = DateTime.MinValue;

		public RecoveryPotionHelper(FsdSettings configuration)
		{
			_configuration = configuration;
		}

		public void Update()
		{
			if (!_configuration.AutoUseRecoveryPotion) return;

			var player = Service.LocalPlayer;
			if (player == null || player.IsDead || player.MaxHp == 0) return;
			if (Service.Condition[ConditionFlag.Casting] ||
			    Service.Condition[ConditionFlag.BetweenAreas] ||
			    Service.Condition[ConditionFlag.BetweenAreas51])
				return;
			var now = DateTime.UtcNow;
			if (now < _nextAttemptAtUtc) return;

			var hpPercentage = (float)player.CurrentHp / player.MaxHp * 100f;
			if (hpPercentage >= _configuration.RecoveryPotionHpThresholdPercent) return;

			if (HasActiveRecoveryBuff(player.StatusList))
				return;

			if (!DeepDungeonHelper.TryGetRecoveryPotionForCurrentDungeon(out var potionItemId, out _))
				return;

			if (!FsdItemExecutor.IsItemReady(potionItemId))
			{
				_nextAttemptAtUtc = now.AddMilliseconds(500);
				if (now >= _nextUnavailableLogAtUtc)
				{
					_nextUnavailableLogAtUtc = now.AddSeconds(5);
					Service.Log.Debug($"[RecoveryPotion] Potion {potionItemId} is not ready for use");
				}
				return;
			}

			_nextAttemptAtUtc = now.AddSeconds(3);
			if (!FsdItemExecutor.UseItem(potionItemId))
			{
				_nextAttemptAtUtc = now.AddSeconds(1);
			}
		}

		private static bool HasActiveRecoveryBuff(global::Dalamud.Game.ClientState.Statuses.StatusList statuses)
		{
			foreach (var status in statuses)
			{
				if (status.StatusId == FsdStatusIds.HealthRecovering && status.RemainingTime >= 3.0f)
				{
					return true;
				}
			}

			return false;
		}
	}
}
