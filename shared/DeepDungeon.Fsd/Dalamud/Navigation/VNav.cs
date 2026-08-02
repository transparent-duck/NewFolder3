using System;
using System.Numerics;
using global::Dalamud.Plugin.Ipc;

namespace DeepDungeon.Fsd.Dalamud.moveHelper
{
    // Lightweight VNAV IPC helper wrapper
    public static class VNav
    {
		private static ICallGateSubscriber<T1, T2, object>? TryGetActionAny<T1, T2>(params string[] names)
		{
			foreach (var n in names)
			{
				try
				{
					var s = Service.PluginInterface.GetIpcSubscriber<T1, T2, object>(n);
					if (s.HasAction) return s;
				}
				catch { }
			}
			return null;
		}

		private static ICallGateSubscriber<object>? TryGetActionAny(params string[] names)
		{
			foreach (var n in names)
			{
				try
				{
					var s = Service.PluginInterface.GetIpcSubscriber<object>(n);
					if (s.HasAction) return s;
				}
				catch { }
			}
			return null;
		}

		private static ICallGateSubscriber<T1, T2, TRet>? TryGetFuncAny<T1, T2, TRet>(params string[] names)
		{
			foreach (var n in names)
			{
				try
				{
					var s = Service.PluginInterface.GetIpcSubscriber<T1, T2, TRet>(n);
					if (s.HasFunction) return s;
				}
				catch { }
			}
			return null;
		}

        private static ICallGateSubscriber<T1, T2, object>? TryGetAction<T1, T2>(string a, string b)
        {
			try
			{
				var s = Service.PluginInterface.GetIpcSubscriber<T1, T2, object>(a);
				// avoid IpcNotReadyError by ensuring action is registered
				if (s.HasAction) return s;
			}
			catch { }
			try
			{
				var s2 = Service.PluginInterface.GetIpcSubscriber<T1, T2, object>(b);
				if (s2.HasAction) return s2;
			}
			catch { }
			return null;
        }

        private static ICallGateSubscriber<object>? TryGetAction(string a, string b)
        {
			return TryGetActionAny(a, b);
        }

        private static ICallGateSubscriber<bool>? TryGetIsReady()
        {
			try
			{
				var s = Service.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
				if (s.HasFunction) return s;
			}
			catch { }
			try
			{
				var s2 = Service.PluginInterface.GetIpcSubscriber<bool>("vnav.Nav.IsReady");
				if (s2.HasFunction) return s2;
			}
			catch { }
			return null;
        }

        private static bool NavmeshReady()
        {
            try
            {
                var isReady = TryGetIsReady();
				return isReady?.HasFunction == true && isReady.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }

        public static class SimpleMove
        {
            /// <summary>
            /// True while an async SimpleMove pathfind is in progress (path following
            /// may not have started yet). Calls vnavmesh.SimpleMove.PathfindInProgress.
            /// </summary>
            public static bool PathfindInProgress()
            {
                try
                {
                    var ipc = Service.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
                    if (ipc?.HasFunction != true)
                    {
                        // Try legacy vnav namespace
                        var ipc2 = Service.PluginInterface.GetIpcSubscriber<bool>("vnav.SimpleMove.PathfindInProgress");
                        if (ipc2?.HasFunction != true) return false;
                        return ipc2.InvokeFunc();
                    }
                    return ipc.InvokeFunc();
                }
                catch
                {
                    return false;
                }
            }

            // Calls: vnavmesh.SimpleMove.PathfindAndMoveTo(Vector3 dest, bool fly)
            public static bool PathfindAndMoveTo(Vector3 dest, bool fly = false)
            {
                try
                {
					// Prefer function variant returning a bool per vnavmesh API
					var ipcFunc = TryGetFuncAny<Vector3, bool, bool>(
						"vnavmesh.SimpleMove.PathfindAndMoveTo",
						"vnav.SimpleMove.PathfindAndMoveTo",
						"vnavmesh.Nav.PathfindAndMoveTo",
						"vnav.Nav.PathfindAndMoveTo"
					);
					// do not block/poll; if not registered yet, exit fast

					// Fallback to an action variant if some build exposes it as an action
					var ipcAction = ipcFunc == null
						? TryGetActionAny<Vector3, bool>(
							"vnavmesh.SimpleMove.PathfindAndMoveTo",
							"vnav.SimpleMove.PathfindAndMoveTo",
							"vnavmesh.Nav.PathfindAndMoveTo",
							"vnav.Nav.PathfindAndMoveTo"
						)
						: null;

					if (ipcFunc == null && ipcAction == null)
                    {
						Service.Log.Debug("[vnav] SimpleMove.PathfindAndMoveTo not available (IPC not registered).");
                        return false;
                    }

                    // If IsReady is exposed, check once without blocking
                    if (!NavmeshReady())
						return false;

					if (ipcFunc != null)
					{
						return ipcFunc.InvokeFunc(dest, fly);
					}

					// action variant has no return
					ipcAction!.InvokeAction(dest, fly);
					return true; // assume success if no exception
                }
                catch (Exception ex)
                {
					Service.Log.Debug($"[vnav] SimpleMove.PathfindAndMoveTo failed: {ex}");
                    return false;
                }
            }
        }

        public static class Nav
        {
            // Calls: vnavmesh.Nav.PathfindCancelAll()
            public static void PathfindCancelAll()
            {
                try
                {
                    var ipc = TryGetAction("vnavmesh.Nav.PathfindCancelAll", "vnav.Nav.PathfindCancelAll");
                    if (ipc != null)
                    {
                        ipc.InvokeAction();
                    }
                    else
                    {
						Service.Log.Debug("[vnav] Nav.PathfindCancelAll not available (IPC not registered).");
                    }
                }
                catch (Exception ex)
                {
					Service.Log.Debug($"[vnav] Nav.PathfindCancelAll failed: {ex}");
                }
            }
        }

        public static class Path
        {
            /// <summary>
            /// Stops the current path following without forcing a navmesh reload.
            /// Falls back to Nav.PathfindCancelAll only if the stop IPC is unavailable.
            /// </summary>
            public static void Stop()
            {
                try
                {
                    var stop = Service.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
                    if (stop?.HasAction == true)
                    {
                        stop.InvokeAction();
                        return;
                    }

                    var legacy = Service.PluginInterface.GetIpcSubscriber<object>("vnav.Path.Stop");
                    if (legacy?.HasAction == true)
                    {
                        legacy.InvokeAction();
                        return;
                    }

                    // No direct stop IPC exposed - fall back to the heavy cancel-all.
                    Nav.PathfindCancelAll();
                }
                catch (Exception ex)
                {
                    Service.Log.Debug($"[vnav] Path.Stop failed: {ex}");
                }
            }

            /// <summary>
            /// Checks if VNav is currently following a path.
            /// Returns true if waypoints exist and path following is active.
            /// </summary>
            public static bool IsRunning()
            {
                try
                {
                    var ipc = Service.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
                    if (ipc?.HasFunction != true)
                    {
                        // Try legacy vnav namespace
                        var ipc2 = Service.PluginInterface.GetIpcSubscriber<bool>("vnav.Path.IsRunning");
                        if (ipc2?.HasFunction != true) return false;
                        return ipc2.InvokeFunc();
                    }
                    return ipc.InvokeFunc();
                }
                catch
                {
                    return false;
                }
            }

            /// <summary>
            /// Gets the number of remaining waypoints in the current path.
            /// Returns 0 if no path is active.
            /// </summary>
            public static int NumWaypoints()
            {
                try
                {
                    var ipc = Service.PluginInterface.GetIpcSubscriber<int>("vnavmesh.Path.NumWaypoints");
                    if (ipc?.HasFunction != true)
                    {
                        // Try legacy vnav namespace
                        var ipc2 = Service.PluginInterface.GetIpcSubscriber<int>("vnav.Path.NumWaypoints");
                        if (ipc2?.HasFunction != true) return 0;
                        return ipc2.InvokeFunc();
                    }
                    return ipc.InvokeFunc();
                }
                catch
                {
                    return 0;
                }
            }
        }
    }
}



