using Microsoft.Xna.Framework;
using StardewValley;

namespace AngelWings.VisualPreview.Flight;

public sealed record SafePosition(string Location, float X, float Y);

public sealed class FlightSafety
{
    public SafePosition? LastSafe { get; set; }

    public static Rectangle BoxAt(Farmer who, Vector2 position)
    {
        Rectangle box=who.GetBoundingBox();
        box.Offset((int)MathF.Floor(position.X-who.Position.X),(int)MathF.Floor(position.Y-who.Position.Y));
        return box;
    }

    public bool CanStand(Farmer who, GameLocation location, Vector2 position, FlightMap map, bool requireAccess=true)
    {
        Rectangle box=BoxAt(who,position);
        if(box.Left<0||box.Top<0||box.Right>map.Width*64||box.Bottom>map.Height*64)return false;
        using var original=FlightCollision.OriginalRules();
        foreach(Point p in FlightMap.CoveredTiles(box))
        {
            if(!map.IsDryStandTile(p.X,p.Y) || (requireAccess && !map.IsReachableGround(p.X,p.Y)))return false;
            if(location.objects.TryGetValue(new Vector2(p.X,p.Y),out var obj) && obj is Fence)return false;
            CollisionMask obstacles=CollisionMask.All & ~CollisionMask.Farmers;
            if(map.IsReservedMineArrival(p.X,p.Y))obstacles &= ~CollisionMask.LocationSpecific;
            if(location.IsTileOccupiedBy(new Vector2(p.X,p.Y),obstacles,obstacles))return false;
        }
        return !location.isCollidingPosition(box,Game1.viewport,true,0,false,who,false,false,false,true);
    }

    public void Remember(Farmer who) => LastSafe=new(who.currentLocation.NameOrUniqueName,who.Position.X,who.Position.Y);

    public bool TryFindGround(Farmer who, GameLocation location, FlightMap map, out Vector2 ground)
    {
        ground=who.Position;
        if(CanStand(who,location,ground,map))return true;
        if(LastSafe is { } safe && safe.Location==location.NameOrUniqueName && float.IsFinite(safe.X) && float.IsFinite(safe.Y))
        {
            ground=new Vector2(safe.X,safe.Y);
            if(CanStand(who,location,ground,map))return true;
        }
        // Search only the previously accessible land component, never across a locked bridge.
        Point start=BoxAt(who,ground).Center;
        start=new Point(Math.Clamp(start.X/64,0,map.Width-1),Math.Clamp(start.Y/64,0,map.Height-1));
        Rectangle originalBox=who.GetBoundingBox();
        Vector2 boxOffset=originalBox.Center.ToVector2()-who.Position;
        var candidates=new List<Point>();
        for(int y=0;y<map.Height;y++)for(int x=0;x<map.Width;x++)
            if(map.IsReachableGround(x,y))candidates.Add(new Point(x,y));
        foreach(Point p in candidates.OrderBy(p=>Math.Abs(p.X-start.X)+Math.Abs(p.Y-start.Y)))
        {
            Vector2 candidate=new Vector2(p.X*64+32,p.Y*64+32)-boxOffset;
            if(CanStand(who,location,candidate,map)) { ground=candidate;return true; }
        }
        return false;
    }
}
