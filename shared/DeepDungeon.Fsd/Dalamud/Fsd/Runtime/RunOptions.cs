using System;
using System.Threading;

namespace DeepDungeon.Fsd.Dalamud.Runtime
{
	/// <summary>
	/// Determines when the Full Self-Delving engine should stop starting new runs.
	/// </summary>
	// TODO: Deprecated item-count end modes; FSD should only stop by loop count.
	public enum FsdEndMode
	{
		Loops = 0,
		Potsherd = 1,
		Hoard = 2
	}

	public enum LeaveMode
	{
		AfterFinishDungeon = 0,
		AfterHoard = 1,
		Immediate = 2,
		OnBossFloorEntry = 3,
		AfterNMinutes = 4
	}

	public static class LeaveModeUiMapping
	{
		public static LeaveMode FromUiIndex(int modeIdx)
		{
			return modeIdx switch
			{
				0 => LeaveMode.AfterFinishDungeon,
				1 => LeaveMode.AfterHoard,
				2 => LeaveMode.Immediate,
				3 => LeaveMode.OnBossFloorEntry,
				4 => LeaveMode.AfterNMinutes,
				_ => LeaveMode.AfterFinishDungeon
			};
		}

		public static int ToUiIndex(LeaveMode leaveMode)
		{
			return leaveMode switch
			{
				LeaveMode.AfterFinishDungeon => 0,
				LeaveMode.AfterHoard => 1,
				LeaveMode.Immediate => 2,
				LeaveMode.OnBossFloorEntry => 3,
				LeaveMode.AfterNMinutes => 4,
				_ => 0
			};
		}
	}

	/// <summary>
	/// Live runtime options that can be changed mid-loop.
	/// </summary>
	public sealed class RunOptions
	{
		public bool OpenGold = false;
		public bool OpenSilver = false;
		public bool OpenBronze = false;

		public bool BandedEnabled = true;

		public LeaveMode LeaveMode = LeaveMode.AfterFinishDungeon;
		public int LeaveAfterMinutes = 0;
		public bool RequireValidatedAbandonPrompt = false;

		public RunOptions Copy()
		{
			return new RunOptions
			{
				OpenGold = OpenGold,
				OpenSilver = OpenSilver,
				OpenBronze = OpenBronze,
				BandedEnabled = BandedEnabled,
				LeaveMode = LeaveMode,
				LeaveAfterMinutes = LeaveAfterMinutes,
				RequireValidatedAbandonPrompt = RequireValidatedAbandonPrompt
			};
		}
	}

	public interface IRunOptionsProvider
	{
		RunOptions Current { get; }
		long Version { get; }
		void Set(RunOptions options);
		void Update(Action<RunOptions> update);
	}

	public sealed class RunOptionsProvider : IRunOptionsProvider
	{
		private readonly object _lock = new object();
		private PublishedRunOptions _published;

		private sealed record PublishedRunOptions(RunOptions Options, long Version);

		public RunOptionsProvider(RunOptions defaults)
		{
			_published = new PublishedRunOptions(defaults?.Copy() ?? new RunOptions(), 0);
		}

		public RunOptions Current => Volatile.Read(ref _published).Options;
		public long Version => Volatile.Read(ref _published).Version;

		public void Set(RunOptions options)
		{
			if (options == null) return;
			lock (_lock)
			{
				var current = Volatile.Read(ref _published);
				Volatile.Write(ref _published, new PublishedRunOptions(options.Copy(), current.Version + 1));
			}
		}

		public void Update(Action<RunOptions> update)
		{
			if (update == null) return;
			lock (_lock)
			{
				var current = Volatile.Read(ref _published);
				var next = current.Options.Copy();
				update(next);
				Volatile.Write(ref _published, new PublishedRunOptions(next, current.Version + 1));
			}
		}
	}
}
