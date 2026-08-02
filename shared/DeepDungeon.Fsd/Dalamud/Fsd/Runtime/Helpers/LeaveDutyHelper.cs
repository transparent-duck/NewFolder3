using System;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Helpers
{
	public static unsafe class LeaveDutyHelper
	{
		/// <summary>
		/// Sends the "Leave Duty" request via the ContentsFinderMenu agent.
		/// Returns true if the event was sent.
		/// </summary>
		public static bool TryRequestLeaveDuty()
		{
			try
			{
				var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.ContentsFinderMenu);
				if (agent == null) return false;
				// Prepare event args: mirror minimal pattern used by HoardFarm
				var eventObject = (AtkValue*)System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(AtkValue));
				var atkValues = CreateSingleIntParam(0);
				if (atkValues == null)
				{
					System.Runtime.InteropServices.Marshal.FreeHGlobal(new IntPtr(eventObject));
					return false;
				}
				try
				{
					agent->ReceiveEvent(eventObject, atkValues, 1, 0);
					return true;
				}
				finally
				{
					System.Runtime.InteropServices.Marshal.FreeHGlobal(new IntPtr(atkValues));
					System.Runtime.InteropServices.Marshal.FreeHGlobal(new IntPtr(eventObject));
				}
			}
			catch
			{
				return false;
			}
		}

		private static AtkValue* CreateSingleIntParam(int value)
		{
			try
			{
				var ptr = (AtkValue*)System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(AtkValue));
				if (ptr == null) return null;
				ptr[0].Type = AtkValueType.Int;
				ptr[0].Int = value;
				return ptr;
			}
			catch
			{
				return null;
			}
		}
	}
}

