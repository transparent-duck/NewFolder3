using System.Numerics;
using MasterPlayer.MechanicRuntime;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// The whole navigation package behind one verb: data representation,
/// pathfinder, and leg follower are impl-internal. Callers only ever ask
/// "get me to dest" — there is no common terrain algebra across bitmap,
/// vnav mesh, and baked time-graphs, so no terrain query is exposed.
/// </summary>
public interface INavSurface
{
    NavigationState Navigate(Vector3 dest, Vector3 playerPos, float arrivalRadius, NavigationPath? path = null);
    void Cancel();
}
