using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace DeepDungeon.Fsd.Dalamud.Utilities;

public static unsafe class DirectMovementCollision
{
    public static Vector3 LastBlockedPoint { get; private set; }
    public static Vector3 LastBlockedNormal { get; private set; }
    public static bool Ray(Vector3 origin, Vector3 direction, float distance, out RaycastHit hit)
    {
        hit = default;
        var framework = Framework.Instance();
        if (framework == null || framework->BGCollisionModule == null) return false;
        // 0x2000 is the character collision material; the convenience API uses 0x4000 and misses platforms.
        int* flags = stackalloc int[] { 0x2000, 0, 0x2000, 0 };
        fixed (RaycastHit* result = &hit)
            return framework->BGCollisionModule->RaycastMaterialFilter(result, &origin, &direction, distance, 1, flags);
    }
    public static bool Floor(Vector3 seed, out Vector3 floor, float rise = .6f, float drop = 4)
    {
        floor = default;
        if (!Ray(seed + Vector3.UnitY * rise, -Vector3.UnitY, rise + drop, out var hit)) return false;
        floor = hit.Point;
        return float.IsFinite(floor.Y);
    }
    public static bool FootPathClear(Vector3 from, Vector3 to)
    {
        var delta = to - from; float length = delta.Length();
        if (length < .0001f) return true;
        var direction = delta / length;
        // This is a foot-path query, not a claim about the player's collision primitive or dimensions.
        return !Blocks(from+Vector3.UnitY*.2f,direction,length);
    }
    public static bool FloorContact(Vector3 position)
    {
        if(!Floor(position,out var floor,.15f,.25f))return false;
        float offset=position.Y-floor.Y;
        // Platform edges can place the character slightly below the point ray.
        // A descending character 0.104m above a platform is still airborne.
        return offset>=-.12f&&offset<=.025f;
    }
    public static bool WalkConnector(Vector3 from,Vector3 to,float margin=.18f)
    {
        Vector3 delta=to-from;float length=delta.Length();
        int steps=Math.Max(1,(int)MathF.Ceiling(length/.15f));
        var lateral=new Vector3(-delta.Z,0,delta.X);
        if(lateral.LengthSquared()>.0001f)lateral=Vector3.Normalize(lateral)*margin;
        var prior=from;
        for(int i=0;i<=steps;++i)
        {
            var seed=Vector3.Lerp(from,to,(float)i/steps);
            if(!Floor(seed,out var floor,.35f,.4f)||MathF.Abs(floor.Y-seed.Y)>.3f)return false;
            if(i>0&&!FootPathClear(prior,floor))return false;
            for(int side=-1;side<=1;side+=2)
                if(!Floor(floor+lateral*side,out var support,.2f,.35f)||MathF.Abs(support.Y-floor.Y)>.25f)return false;
            prior=floor;
        }
        return true;
    }
    private static bool Blocks(Vector3 origin, Vector3 direction, float length)
    {
        if(!Ray(origin,direction,length,out var hit))return false;
        // Upper walkable faces are handled by the ground/landing intersection, not by the body outline.
        if(hit.Normal.LengthSquared()<.001f&&Floor(hit.Point,out var floor,.1f,.15f)&&MathF.Abs(floor.Y-hit.Point.Y)<.02f)return false;
        if(hit.Normal.Y>.64f||hit.Normal.LengthSquared()>.001f&&Vector3.Dot(hit.Normal,direction)>=-.001f)return false;
        LastBlockedPoint=hit.Point;LastBlockedNormal=hit.Normal;return true;
    }
}
