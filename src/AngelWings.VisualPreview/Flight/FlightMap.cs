using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using xTile;
using xTile.Tiles;

namespace AngelWings.VisualPreview.Flight;

/// <summary>Marked water, map boundaries and identified low map obstacles are classified for airborne movement.</summary>
public sealed class FlightMap
{
    private readonly GameLocation Location;
    private readonly Map Map;
    private readonly bool[,] ReachableGround;
    private readonly bool[,]? ForestPond;
    private readonly string MapName;
    private readonly Dictionary<Point,bool> DoorRoutes;
    public int Width => ReachableGround.GetLength(0);
    public int Height => ReachableGround.GetLength(1);
    public bool Matches(GameLocation location) => ReferenceEquals(Location, location) && ReferenceEquals(Map, location.Map);

    // Identified in the installed 1.6.15 tilesheets. Never treat all Buildings as low obstacles.
    // Forest north of Leah's cottage: 434 is a fence junction, 232 a small decorative rock.
    // This does not include mineable objects, resource clumps, tree stumps or buildings.
    private static readonly HashSet<int> OutdoorLowObstacles = new()
    {232,358,359,360,361,383,384,385,386,411,434,436,637,638,639,640,641,642,643,644};

    public FlightMap(GameLocation location, Point seed)
    {
        Location = location;
        Map = location.Map;
        string sourceName=string.IsNullOrWhiteSpace(location.mapPath.Value) ? Map.assetPath : location.mapPath.Value;
        if(string.IsNullOrWhiteSpace(sourceName))sourceName=Map.Id;
        MapName = Path.GetFileNameWithoutExtension((sourceName ?? "").Replace('\\','/')).ToLowerInvariant();
        var back = Map.GetLayer("Back") ?? throw new InvalidDataException("Map has no Back layer.");
        ReachableGround = new bool[back.LayerWidth, back.LayerHeight];
        // A water shortcut must not open an island/area whose normal bridge is still locked.
        // Identified low map obstacles are ignored in this ground-connectivity graph.
        var queue = new Queue<Point>();
        if (OnMap(seed.X, seed.Y) && IsGroundRoute(seed.X, seed.Y))
        {
            ReachableGround[seed.X, seed.Y] = true;
            queue.Enqueue(seed);
        }
        while (queue.TryDequeue(out Point p))
        foreach (Point d in new[] { new Point(1,0), new Point(-1,0), new Point(0,1), new Point(0,-1) })
        {
            Point q = p + d;
            if (!OnMap(q.X,q.Y) || ReachableGround[q.X,q.Y] || !IsGroundRoute(q.X,q.Y)) continue;
            ReachableGround[q.X,q.Y] = true;
            queue.Enqueue(q);
        }
        if(MapName=="forest")ForestPond=WaterComponent(new Point(35,29));
        DoorRoutes = Location.interiorDoors.Doors.ToDictionary(d=>d.Position,d=>IsGroundRoute(d.Position.X,d.Position.Y));
    }

    // Native interior doors modify this same Map instance and remove their
    // TemporaryBarrier after 400ms. Map identity alone cannot detect that change.
    public bool DoorsChanged => Location.interiorDoors.Doors.Count() != DoorRoutes.Count
        || Location.interiorDoors.Doors.Any(d=>!DoorRoutes.TryGetValue(d.Position,out bool route)
            || route != IsGroundRoute(d.Position.X,d.Position.Y));

    public bool OnMap(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
    public bool CrossesMapBoundary(Rectangle box) => box.Left < 0 || box.Top < 0 || box.Right > Width*64 || box.Bottom > Height*64;
    public bool IsReachableGround(int x, int y) => OnMap(x,y) && ReachableGround[x,y];

    // MineShaft reserves these walkable spawn tiles against object placement.
    // IsLocationSpecificOccupantOnTile reports that reservation as occupancy,
    // even though the native movement collision allows standing/walking here.
    public bool IsReservedMineArrival(int x, int y) => Location is MineShaft mine
        && (mine.tileBeneathLadder == new Vector2(x,y)
            || (mine.tileBeneathElevator != Vector2.Zero && mine.tileBeneathElevator == new Vector2(x,y)));

    public bool IsOpenWaterAt(Vector2 worldPosition)
    {
        if(!float.IsFinite(worldPosition.X) || !float.IsFinite(worldPosition.Y))return false;
        int x=(int)MathF.Floor(worldPosition.X/64),y=(int)MathF.Floor(worldPosition.Y/64);
        return CanCrossWater(x,y) && !IsDryStandTile(x,y);
    }

    public WaterEffectKind WaterEffectAt(Vector2 worldPosition)
    {
        if(!float.IsFinite(worldPosition.X) || !float.IsFinite(worldPosition.Y))return WaterEffectKind.Lake;
        int x=(int)MathF.Floor(worldPosition.X/64),y=(int)MathF.Floor(worldPosition.Y/64);
        foreach(Tile? tile in new[]{At(x,y,"Buildings"),At(x,y,"Back")})
        {
            string value=Get(tile,"AngelWingsWaterEffect");
            if(Enum.TryParse(value,true,out WaterEffectKind configured))return configured;
        }
        if(MapName.Contains("beach") || MapName.StartsWith("island"))return WaterEffectKind.Ocean;
        if(MapName=="forest")return ForestPond!=null && OnMap(x,y) && ForestPond[x,y]
            ? WaterEffectKind.Lake : WaterEffectKind.River;
        if(MapName.Contains("town") || MapName.Contains("fishing") || MapName.Contains("river")
            || MapName.Contains("sewer") || MapName.Contains("bugland"))return WaterEffectKind.River;
        return WaterEffectKind.Lake;
    }

    public bool CanTraverse(Rectangle box)
    {
        if (CrossesMapBoundary(box)) return false;
        foreach (Point p in CoveredTiles(box))
        {
            Tile? back = At(p.X,p.Y,"Back");
            if (back == null || Barrier(back) || Barrier(At(p.X,p.Y,"Buildings"))) return false;
            if (!IsReachableGround(p.X,p.Y) && !CanCrossWater(p.X,p.Y)) return false;
        }
        return true;
    }

    public bool CanCrossWater(int x, int y)
    {
        if (!OnMap(x,y)) return false;
        Tile? back = At(x,y,"Back"), building = At(x,y,"Buildings");
        if (back == null || !Has(back,"Water") || Barrier(back) || Barrier(building)) return false;
        // Shore, cliff and dock edge art varies across maps and seasons. Over an
        // explicitly marked water tile it is visual cover, unless the map author
        // attached interaction/transition semantics or an explicit flight barrier.
        return !Has(back,"Action") && !Has(back,"TouchAction")
            && !Has(building,"Action") && !Has(building,"TouchAction");
    }

    public bool IsLowObstacle(int x,int y)
    {
        if(!OnMap(x,y) || Barrier(At(x,y,"Back")) || Barrier(At(x,y,"Buildings")))return false;
        if(Location.objects.TryGetValue(new Vector2(x,y),out var obj) && obj is Fence fence)
            return (fence.health.Value>1 || fence.repairQueued.Value) && !(fence.isGate.Value && fence.gatePosition.Value>=88);
        return CanCrossMapLowObstacle(x,y);
    }

    public bool ContainsLowObstacle(Rectangle box) => CoveredTiles(box).Any(p=>IsLowObstacle(p.X,p.Y));

    public bool CanCrossMapLowObstacle(int x, int y) => OnMap(x,y) && IsReachableGround(x,y)
        && !Barrier(At(x,y,"Back")) && !Barrier(At(x,y,"Buildings")) && IsMapLowObstacle(At(x,y,"Buildings"));

    public bool IsDryStandTile(int x, int y)
    {
        if (!OnMap(x,y)) return false;
        Tile? back = At(x,y,"Back"), building = At(x,y,"Buildings");
        if (back == null || Barrier(back) || Barrier(building)) return false;
        // Back.Passable is an inverted collision marker in the game; on Buildings it allows walking.
        if (Has(back,"Passable") || (building != null && !BuildingPassable(building))) return false;
        return !Has(back,"Water") || (building != null && Has(building,"Passable") && Has(building,"Type","Wood"));
    }

    private bool IsGroundRoute(int x, int y)
    {
        if (!IsDryStandTile(x,y) && !IsMapLowObstacle(At(x,y,"Buildings"))) return false;
        if (Barrier(At(x,y,"Back")) || Barrier(At(x,y,"Buildings"))) return false;
        // Resource clumps can be progression gates, unlike ordinary placed objects/fences.
        if (Location.resourceClumps.Any(c=>c.occupiesTile(x,y))) return false;
        return IsReservedMineArrival(x,y) || !Location.IsLocationSpecificOccupantOnTile(new Vector2(x,y));
    }

    private Tile? At(int x, int y, string layer)
    {
        var l = Map.GetLayer(layer);
        return l != null && x>=0 && y>=0 && x<l.LayerWidth && y<l.LayerHeight ? l.Tiles[x,y] : null;
    }

    private bool[,] WaterComponent(Point seed)
    {
        var result=new bool[Width,Height];
        if(!CanCrossWater(seed.X,seed.Y))return result;
        var queue=new Queue<Point>();queue.Enqueue(seed);result[seed.X,seed.Y]=true;
        while(queue.TryDequeue(out Point p))
        foreach(Point d in new[]{new Point(1,0),new Point(-1,0),new Point(0,1),new Point(0,-1)})
        {
            Point q=p+d;
            if(!OnMap(q.X,q.Y) || result[q.X,q.Y] || !CanCrossWater(q.X,q.Y))continue;
            result[q.X,q.Y]=true;queue.Enqueue(q);
        }
        return result;
    }

    public static bool IsMapLowObstacle(Tile? tile) => tile != null && !Has(tile,"Action") && !Has(tile,"TouchAction")
        && ((SheetName(tile)=="outdoorstilesheet" && OutdoorLowObstacles.Contains(tile.TileIndex)) || Has(tile,"AngelWingsFence","T"));
    public static bool BuildingPassable(Tile tile) => Has(tile,"Passable") || Has(tile,"Shadow");
    public static bool Barrier(Tile? tile) => Has(tile,"TemporaryBarrier") || Has(tile,"AngelWingsNoFly");
    public static bool Has(Tile? tile, string property, string? value = null)
    {
        if (tile == null) return false;
        if (!tile.Properties.TryGetValue(property,out var p) && !tile.TileIndexProperties.TryGetValue(property,out p)) return false;
        return value == null || string.Equals(p.ToString(),value,StringComparison.OrdinalIgnoreCase);
    }

    private static string Get(Tile? tile,string property)
    {
        if(tile==null)return "";
        if(tile.Properties.TryGetValue(property,out var value) || tile.TileIndexProperties.TryGetValue(property,out value))
            return value?.ToString() ?? "";
        return "";
    }

    private static string SheetName(Tile tile)
    {
        string name = Path.GetFileNameWithoutExtension(tile.TileSheet.ImageSource.Replace('\\','/')).ToLowerInvariant();
        foreach(string season in new[]{"spring_","summer_","fall_","winter_"})
            if(name.StartsWith(season))return name[season.Length..];
        return name;
    }

    public static IEnumerable<Point> CoveredTiles(Rectangle box)
    {
        for(int y=box.Top/64;y<=(box.Bottom-1)/64;y++)
        for(int x=box.Left/64;x<=(box.Right-1)/64;x++)yield return new Point(x,y);
    }
}
