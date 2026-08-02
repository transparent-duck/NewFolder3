using System;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat
{
/// <summary>
/// Shared combat-assist policy used by both manual banded farming and FullSelfDelving controllers.
/// Encapsulates target selection and out-of-combat attract casting based on configuration.
/// Run-scoped state is maintained between calls.
/// </summary>
internal sealed class CombatAssistPolicy
	{
		private DateTime _nextAttractCastAt = DateTime.MinValue;
		private bool _prevInCombat = false;
		private uint _cachedEngageRangeSkillId = uint.MaxValue;
		private float _cachedEngageRange;

	/// <summary>
	/// Apply combat-assist behavior for the current tick.
	/// </summary>
	/// <param name="configuration">Global configuration (Necromancer* options).</param>
	/// <param name="context">Current run context.</param>
	/// <param name="isBossFloor">Whether the current floor is treated as a boss floor.</param>
	/// <param name="passageOpen">If true, passage is open; combat assist will not engage new targets.</param>
	/// <param name="status">Primary status line (combined select/attract summary).</param>
	/// <param name="selectStatus">Status line focused on target selection.</param>
	/// <param name="attractStatus">Status line focused on attract casting.</param>
	public void Tick(FsdSettings configuration, RunContext? context, bool isBossFloor, bool passageOpen,
		out string status, out string selectStatus, out string attractStatus)
		{
		status = "Assist: Idle";
		selectStatus = configuration.NecromancerBandedAutoSelect ? "active" : "off";
		attractStatus = configuration.NecromancerBandedAutoAttract ? "active" : "off";

		try
		{
			var player = Service.LocalPlayer;
			if (player == null || player.IsDead)
				return;

			bool inCombat = Service.Condition[ConditionFlag.InCombat];
			float engageRange = GetCachedEngageRange(configuration);
			if (engageRange <= 0f)
			{
				status = "Assist: configured attract action has no valid cast range";
				selectStatus = "Unavailable →invalid attract action range";
				attractStatus = "Unavailable →invalid attract action range";
				return;
			}
			ulong preferredAggroId = 0;
			bool hasPreferredAggro = context?.TryGetPreferredAggroTarget(out preferredAggroId) ?? false;
			bool IsAllowedCombatTarget(IBattleChara target) => context?.IsCombatTargetSuppressed(target.GameObjectId) != true;
			IBattleChara? preferredOocTarget = null;
			bool preferredOocTargetWithinRange = false;
			if (!inCombat && hasPreferredAggro)
			{
				var candidate = CombatTargetingHelpers.GetBattleCharaByGameObjectId(preferredAggroId);
				if (candidate is { IsTargetable: true, IsDead: false } &&
				    IsAllowedCombatTarget(candidate))
				{
					preferredOocTarget = candidate;
					var dx = candidate.Position.X - player.Position.X;
					var dz = candidate.Position.Z - player.Position.Z;
					preferredOocTargetWithinRange =
						dx * dx + dz * dz <= engageRange * engageRange;
				}
			}

			IBattleChara? TryPickPreferredAggro(float range, out bool withinRange)
			{
				if (!hasPreferredAggro)
				{
					withinRange = false;
					return null;
				}

				var selected = CombatTargetingHelpers.PickAggroedHostile(range, out withinRange, preferredAggroId);
				if (selected != null && !IsAllowedCombatTarget(selected))
				{
					withinRange = false;
					return null;
				}

				return selected;
			}

			if (passageOpen)
			{
				if (inCombat)
				{
					if (configuration.NecromancerBandedAutoSelect)
					{
						IBattleChara? target = isBossFloor
							? CombatTargetingHelpers.PickHostileLowestHP(engageRange, onlyInRange: true, mustBeInCombat: true, minHpExclusive: 1, IsAllowedCombatTarget)
							: CombatTargetingHelpers.PickHostileHighestHP(engageRange, onlyInRange: true, mustBeInCombat: true, IsAllowedCombatTarget);
						
					if (target != null)
					{
						try { Service.TargetManager.Target = target; } catch { }
					}

					string tname = target?.Name?.TextValue ?? "無目標";
					status = isBossFloor 
						? "Assist: Passage open - finishing combat (lowest HP)"
						: "Assist: Passage open - finishing combat (highest HP)";
						selectStatus = target != null
							? $"Finishing combat →{tname}"
							: "Finishing combat →no target";
						attractStatus = "Disabled →Passage open";
						
						_prevInCombat = inCombat;
						return;
					}
					else
					{
						status = "Assist: Passage open - in combat (select disabled)";
						selectStatus = "Disabled →Passage open";
						attractStatus = "Disabled →Passage open";
						_prevInCombat = inCombat;
						return;
					}
				}
				else
				{
					var nearestHostile = TryPickPreferredAggro(engageRange * 2.5f, out bool withinRange)
					                     ?? CombatTargetingHelpers.PickNearestHostile(engageRange * 2f, out withinRange);
					
					if (nearestHostile != null && !withinRange)
					{
						if (configuration.NecromancerBandedAutoSelect)
						{
							try { Service.TargetManager.Target = nearestHostile; } catch { }
						}
						
						string tname = nearestHostile.Name?.TextValue ?? "無名";
						status = "Assist: Passage open - waiting for navigation to remaining mob";
						selectStatus = $"Awaiting nav →{tname}";
						attractStatus = "Disabled →Passage open";
						_prevInCombat = inCombat;
						return;
					}
					else
					{
						status = "Assist: Passage open (idle)";
						selectStatus = "Disabled →Passage open";
						attractStatus = "Disabled →Passage open";
						_prevInCombat = inCombat;
						return;
					}
				}
			}

			if (_prevInCombat && !inCombat)
				_nextAttractCastAt = DateTime.MinValue;

			if (configuration.NecromancerBandedAutoSelect)
				{
					if (inCombat)
					{
						IBattleChara? target = isBossFloor
							? CombatTargetingHelpers.PickHostileLowestHP(engageRange, onlyInRange: true, mustBeInCombat: true, minHpExclusive: 1, IsAllowedCombatTarget)
							: CombatTargetingHelpers.PickHostileHighestHP(engageRange, onlyInRange: true, mustBeInCombat: true, IsAllowedCombatTarget);
					if (target != null)
					{
						try { Service.TargetManager.Target = target; } catch { }
					}

			string tname = target?.Name?.TextValue ?? "無目標";
			if (isBossFloor)
				{
					status = "Assist: In combat (最低HP目標)";
					selectStatus = target != null
						? $"In combat (lowest HP) →{tname}"
						: "In combat (lowest HP) →no target";
				}
				else
				{
					status = "Assist: In combat (最高HP目標)";
					selectStatus = target != null
						? $"In combat (highest HP) →{tname}"
						: "In combat (highest HP) →no target";
				}
					}
					else
					{
					bool withinRange;
					var target = preferredOocTarget;
					if (target != null)
					{
						withinRange = preferredOocTargetWithinRange;
					}
					else
					{
						target = CombatTargetingHelpers.PickNearestHostile(engageRange, out withinRange);
					}
					if (target != null)
					{
						try { Service.TargetManager.Target = target; } catch { }

				string tname = target.Name?.TextValue ?? "無名";
				status = withinRange
					? "Assist: OOC target selected (in range)"
					: "Assist: OOC target selected (awaiting navigation)";
				selectStatus = $"OOC target →{tname} (in range: {withinRange})";
			}
				else
				{
					status = "Assist: OOC no target in range";
					selectStatus = "No hostile in range";
				}
					}
				}

				if (!inCombat && configuration.NecromancerBandedAutoAttract)
				{
					uint sid = configuration.NecromancerBandedAttractSkillId;
					if (sid != 0)
					{
						IGameObject? tgt;
						bool targetOk;
						if (preferredOocTarget != null)
						{
							tgt = preferredOocTarget;
							targetOk = preferredOocTargetWithinRange;
						}
						else
						{
							tgt = Service.TargetManager.Target;
							targetOk = false;
							if (tgt is IBattleChara tbc && !tbc.IsDead)
							{
								var dx = tgt.Position.X - player.Position.X;
								var dz = tgt.Position.Z - player.Position.Z;
								targetOk = (dx * dx + dz * dz) <= (engageRange * engageRange);
							}
						}
						if (!targetOk)
						{
							if (preferredOocTarget != null)
							{
								tgt = null;
							}
							else
							{
								var pick = CombatTargetingHelpers.PickNearestHostile(engageRange, out bool withinRange);
								if (pick != null && withinRange)
								{
									try { Service.TargetManager.Target = pick; } catch { }
									tgt = pick;
									targetOk = true;
								}
								else
								{
									tgt = null;
								}
							}
						}

					if (tgt != null && DateTime.Now >= _nextAttractCastAt)
					{
						bool ok = DeepDungeon.Fsd.Dalamud.Actions.FsdActionExecutor.Cast(sid, tgt.GameObjectId);
						_nextAttractCastAt = DateTime.Now.AddSeconds(2.0);
						status = $"Assist: Cast {(ok ? "OK" : "FAIL")}";
						attractStatus = $"Cast {(ok ? "OK" : "FAIL")}";
					}
					else
					{
						if (tgt == null)
							status = "Assist: no in-range target";
						else
							status = "Assist: waiting GCD";
						attractStatus = tgt == null ? "No in-range target" : "Waiting GCD";
					}
					}
				}

				_prevInCombat = inCombat;
			}
			catch (Exception ex)
			{
			status = "Assist: error (CombatAssistPolicy)";
			try { Service.Log.Error($"[CombatAssistPolicy] Tick error: {ex}"); } catch { }
			}
		}

	/// <summary>
	/// Computes the cast range of the configured attract skill.
	/// </summary>
	private float GetCachedEngageRange(FsdSettings configuration)
	{
		uint sid = configuration.NecromancerBandedAttractSkillId;
		if (sid == _cachedEngageRangeSkillId)
			return _cachedEngageRange;

		_cachedEngageRangeSkillId = sid;
		_cachedEngageRange = ResolveEngageRange(sid);
		return _cachedEngageRange;
	}

	private static float ResolveEngageRange(uint sid)
	{
		if (sid == 0)
			return 0f;

		var info = FsdSkillCatalog.GetOrRegister(sid);
		return ResolveConfiguredCastRange(info);
	}

	internal static float ResolveConfiguredCastRange(in FsdSkillInfo info) =>
		info.IsValid ? info.Range : 0f;
}
}
