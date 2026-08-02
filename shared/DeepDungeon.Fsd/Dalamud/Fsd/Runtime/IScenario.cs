using System;
using global::Dalamud.Plugin.Services;

namespace DeepDungeon.Fsd.Dalamud.Runtime
{
	/// <summary>
	/// A concrete full self-farming scenario (e.g., HoH blind hoard, PT chest, PT chest+bronze).
	/// Implementations should be stateless single-run objects tied to a RunContext lifecycle.
	/// </summary>
	public interface IScenario : IDisposable
	{
		/// <summary>
		/// Scenario display name for logs and UI.
		/// </summary>
		string Name { get; }

		/// <summary>
		/// Called once when the engine starts this scenario with a fresh context.
		/// </summary>
		void Initialize(RunContext context);

		/// <summary>
		/// Called every tick by the engine.
		/// </summary>
		void Update(IFramework framework);

		/// <summary>
		/// Whether the scenario has completed its current run.
		/// </summary>
		bool IsComplete { get; }

		/// <summary>
		/// If true, engine should re-enter the duty and run again (loop).
		/// </summary>
		bool ShouldLoop { get; }

		/// <summary>
		/// Normal scenarios complete by clearing the duty. Controlled reusable-save capture completes by
		/// deliberate abandonment and must not fabricate a duty-completion event.
		/// </summary>
		bool RequiresDutyCompletionEvent { get; }
	}
}

