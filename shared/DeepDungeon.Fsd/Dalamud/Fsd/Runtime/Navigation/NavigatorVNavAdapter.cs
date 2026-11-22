using DeepDungeon.Fsd.Core;
using System;
using System.Numerics;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation
{
	public sealed class NavigatorVNavAdapter : INavigator, IDisposable
	{
		private bool _cancelPending;
        private global::Dalamud.Plugin.Ipc.ICallGateSubscriber<float, object>? _tolerance;

		public bool PathfindAndMoveTo(Vector3 dest, bool alignCamera)
		{
			try
			{
				_tolerance ??= Service.PluginInterface.GetIpcSubscriber<float, object>("vnavmesh.Path.SetTolerance");
                if (_tolerance.HasAction) _tolerance.InvokeAction(NavigationRange.MovementTolerance);
                bool started = DeepDungeon.Fsd.Dalamud.moveHelper.VNav.SimpleMove.PathfindAndMoveTo(dest, alignCamera);
				_cancelPending |= started;
				return started;
			}
			catch
			{
				return false;
			}
		}

		public bool RepathTo(Vector3 dest, bool alignCamera)
		{
			CancelAll();
			return PathfindAndMoveTo(dest, alignCamera);
		}

		public void CancelAll()
		{
			if (!_cancelPending)
				return;

			_cancelPending = false;
            try { DeepDungeon.Fsd.Dalamud.moveHelper.VNav.Path.Stop(); } catch { }
		}

		public void Dispose()
		{
			try { CancelAll(); } catch { }
		}
	}
}
