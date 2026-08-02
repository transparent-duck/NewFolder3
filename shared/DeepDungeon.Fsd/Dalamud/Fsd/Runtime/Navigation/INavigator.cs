using System.Numerics;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation
{
	public interface INavigator
	{
		/// <summary>
		/// Start navigating to destination. Returns false if a path couldn't be issued.
		/// </summary>
		bool PathfindAndMoveTo(Vector3 dest, bool alignCamera);

		/// <summary>
		/// Attempt to rebuild the current path to a new destination.
		/// </summary>
		bool RepathTo(Vector3 dest, bool alignCamera);

		/// <summary>
		/// Cancel all active navigation requests.
		/// </summary>
		void CancelAll();
	}
}

