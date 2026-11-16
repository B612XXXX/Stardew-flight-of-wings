using System.Runtime.CompilerServices;
using AngelWings.VisualPreview;
using AngelWings.VisualPreview.Flight;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI.Events;
using StardewValley;
using xTile;
using xTile.Tiles;

static class ForestFenceRuntimeChecks
{
    static int Count;
    static void Check(bool value,string message) { if(!value)throw new Exception(message);Count++; }
    static void Set(object instance,string field,object? value) => AccessTools.Field(instance.GetType(),field).SetValue(instance,value);

    public static int Run(ModEntry mod,Farmer who,FlightController flight,GameLocation original)
    {
        Count=0;
        using var content=new ContentManager(new GameServiceContainer(),Path.Combine(Environment.GetCommandLineArgs()[1],"Content"));
        var forest=new GameLocation();forest.name.Value="ForestFenceFixture";forest.IsOutdoors=true;
        forest.Map=content.Load<Map>("Maps/Forest");
        Game1.game1.instanceGameLocation=forest;who.currentLocation=forest;
        var surface=new FlightMap(forest,new Point(108,26));Set(mod,"Surface",surface);
        var altitude=(FlightAltitude)AccessTools.Field(typeof(ModEntry),"Altitude").GetValue(mod)!;
        try
        {
            var tile=forest.Map.GetLayer("Buildings").Tiles[108,28];
            Check(tile.TileIndex==434,"Regression must use the actual fence junction north of Leah's cottage");
            Check(forest.Map.GetLayer("Buildings").Tiles[109,27].TileIndex==232,
                "Regression must include the small decorative rock directly beside the fence junction");
            flight.Reset();flight.Toggle(true,false);
            for(int i=0;i<7;i++)flight.Advance(100,true,false,false);
            var target=new Rectangle(108*64+8,28*64+16,48,32);
            who.Position=new Vector2(107*64,28*64-16);
            bool airborne=forest.isCollidingPosition(target,Game1.viewport,true,0,false,who,false,false,false,true);
            bool walking;
            using(FlightCollision.OriginalRules())walking=forest.isCollidingPosition(target,Game1.viewport,true,0,false,who,false,false,false,true);
            Check(walking,"The same fence junction must still block walking");
            Check(!surface.IsDryStandTile(108,28),"A fence junction must never become a landing tile");
            var update=(UpdateTickingEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(UpdateTickingEventArgs));
            var outcomes=new List<(int Direction,Vector2 Position)>();
            foreach(int direction in new[]{1,3})
            {
                altitude.Reset();
                // The native 48x32 foot box spans rows 27 and 28 here, touching both obstacles.
                who.Position=new Vector2((direction==1?106:110)*64,28*64-16);
                who.movementDirections.Clear();who.movementDirections.Add(direction);
                for(int tick=0;tick<45;tick++)
                {
                    AccessTools.Method(typeof(ModEntry),"OnUpdate").Invoke(mod,new object?[]{null,update});
                    who.MovePosition(Game1.currentGameTime,new xTile.Dimensions.Rectangle(0,0,120*64,120*64),forest);
                }
                outcomes.Add((direction,who.Position));
            }
            Check(!airborne,"Flight is blocked by the fence junction at Forest (108,28), directly north of Leah's cottage");
            foreach(var outcome in outcomes)
                Check(outcome.Direction==1?outcome.Position.X+8>110*64:outcome.Position.X+56<108*64,
                    $"Native airborne movement must cross the Forest fence junction and adjacent rock in direction {outcome.Direction}; actual={outcome.Position}");

            who.Position=new Vector2(107*64,28*64-16);
            string originalSheet=tile.TileSheet.ImageSource;
            foreach(string season in new[]{"spring","summer","fall","winter"})
            {
                tile.TileSheet.ImageSource=$"Maps/{season}_outdoorsTileSheet";
                var fresh=new FlightMap(forest,new Point(110,26));Set(mod,"Surface",fresh);
                foreach(var point in new[]{new Point(108,28),new Point(109,27)})
                {
                    var box=new Rectangle(point.X*64+8,point.Y*64+16,48,32);
                    Check(fresh.CanCrossMapLowObstacle(point.X,point.Y)&&fresh.IsLowObstacle(point.X,point.Y)
                        && !forest.isCollidingPosition(box,Game1.viewport,true,0,false,who,false,false,false,true),
                        $"The low obstacle at {point} must be flyable after a fresh surface rebuild in {season}");
                    Check(!fresh.IsDryStandTile(point.X,point.Y),$"The low obstacle at {point} must not become a landing tile");
                    using(FlightCollision.OriginalRules())
                        Check(forest.isCollidingPosition(box,Game1.viewport,true,0,false,who,false,false,false,true),
                            $"Walking must still collide with the low obstacle at {point}");
                }
            }
            tile.TileSheet.ImageSource=originalSheet;
            foreach(var point in new[]{new Point(108,28),new Point(109,27)})
            foreach(var marker in new[]{("Back","AngelWingsNoFly","T"),("Buildings","TemporaryBarrier","T"),
                ("Buildings","Action","LockedDoor"),("Buildings","TouchAction","Warp SecretRoom")})
            {
                Tile marked=forest.Map.GetLayer(marker.Item1).Tiles[point.X,point.Y];
                marked.Properties[marker.Item2]=marker.Item3;
                var fresh=new FlightMap(forest,new Point(108,26));Set(mod,"Surface",fresh);
                var box=new Rectangle(point.X*64+8,point.Y*64+16,48,32);
                Check(!fresh.CanCrossMapLowObstacle(point.X,point.Y)
                    && forest.isCollidingPosition(box,Game1.viewport,true,0,false,who,false,false,false,true),
                    $"Low obstacle at {point} must preserve {marker.Item1}.{marker.Item2}");
                marked.Properties.Remove(marker.Item2);
            }
            surface=new FlightMap(forest,new Point(108,26));Set(mod,"Surface",surface);
            foreach(var obstacle in new[]{new Point(109,24),new Point(106,24),new Point(108,29)})
            {
                var box=new Rectangle(obstacle.X*64+8,obstacle.Y*64+16,48,32);
                Check(!surface.CanCrossMapLowObstacle(obstacle.X,obstacle.Y)
                    && forest.isCollidingPosition(box,Game1.viewport,true,0,false,who,false,false,false,true),
                    $"Nearby trunk, sign and house must retain their native collision at {obstacle}");
            }
            var otherSheet=new TileSheet("unrelated",forest.Map,"Maps/unrelated",new xTile.Dimensions.Size(25,79),new xTile.Dimensions.Size(16,16));
            forest.Map.AddTileSheet(otherSheet);
            foreach(int index in new[]{232,434})
            {
                var unrelated=new StaticTile(forest.Map.GetLayer("Buildings"),otherSheet,BlendMode.Alpha,index);
                Check(!FlightMap.IsMapLowObstacle(unrelated),$"Tile {index} from an unrelated tilesheet must not become a low obstacle");
            }
            Console.WriteLine("PASS: native Forest fence junction and decorative rock crossed in both directions; all seasons, walking/landing and nearby obstacles verified.");
            return Count;
        }
        finally
        {
            altitude.Reset();who.movementDirections.Clear();
            Game1.game1.instanceGameLocation=original;who.currentLocation=original;who.Position=new Vector2(320,320);
            Set(mod,"Surface",new FlightMap(original,new Point(5,5)));
        }
    }
}
