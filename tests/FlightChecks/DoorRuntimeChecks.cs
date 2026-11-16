using System.Reflection;
using System.Runtime.CompilerServices;
using AngelWings.VisualPreview;
using AngelWings.VisualPreview.Flight;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI.Events;
using StardewValley;
using xTile;

static class DoorRuntimeChecks
{
    static void Set(object instance,string field,object? value) => AccessTools.Field(instance.GetType(),field).SetValue(instance,value);
    static void Invoke(ModEntry mod,string name,params object?[] args) => AccessTools.Method(typeof(ModEntry),name).Invoke(mod,args);
    public static int Run(ModEntry mod,Farmer who,FlightController flight,GameLocation original)
    {
        int count=0;
        var failures=new List<string>();
        void Check(bool value,string message) {count++;if(!value)failures.Add(message);}
        using var content=new ContentManager(new GameServiceContainer(),Path.Combine(Environment.GetCommandLineArgs()[1],"Content"));
        var safety=(FlightSafety)AccessTools.Field(typeof(ModEntry),"Safety").GetValue(mod)!;
        var fold=(FlightDoorPassage)AccessTools.Field(typeof(ModEntry),"DoorPassage").GetValue(mod)!;
        var update=(UpdateTickingEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(UpdateTickingEventArgs));
        var oldDelayed=Game1.delayedActions;Game1.delayedActions=new List<DelayedAction>();
        try
        {
            foreach(string name in new[]{"Hospital","ScienceHouse","HaleyHouse","SeedShop"})
            {
                var location=new GameLocation();location.name.Value=name;location.Map=content.Load<Map>("Maps/"+name);
                location.interiorDoors.ResetSharedState();
                foreach(var door in location.interiorDoors.Doors)
                {
                    var tile=location.Map.GetLayer("Buildings").Tiles[door.Position.X,door.Position.Y];
                    if(tile==null || !FlightMap.Has(tile,"Action"))continue;
                    door.Tile=tile;
                    Game1.game1.instanceGameLocation=location;who.currentLocation=location;
                    who.Position=new Vector2(door.Position.X*64,door.Position.Y*64+64);
                    who.movementDirections.Clear();safety.LastSafe=null;
                    flight.Reset();flight.RetainForRecovery();
                    Set(mod,"Surface",new FlightMap(location,new Point(door.Position.X,door.Position.Y+1)));
                    safety.Remember(who);
                    fold.Reset();
                    who.movementDirections.Add(0);
                    for(int i=0;i<15;i++)Invoke(mod,"OnUpdate",null,update);
                    Check(fold.Amount==0,"Pushing toward a closed/locked door must not fold wings");
                    who.movementDirections.Clear();
                    // Native confirmed opening; avoid graphics-only sprite initialization.
                    location.openDoor(new xTile.Dimensions.Location(door.Position.X,door.Position.Y),false);
                    door.ApplyMapModifications();
                    Check(FlightMap.Has(location.Map.GetLayer("Back").Tiles[door.Position.X,door.Position.Y],"TemporaryBarrier"),
                        "Native interior door must keep its 400ms opening barrier");
                    who.movementDirections.Add(0);
                    Vector2 start=who.Position;
                    for(int i=0;i<10;i++)
                    {
                        Invoke(mod,"OnUpdate",null,update);
                        who.MovePosition(Game1.currentGameTime,new xTile.Dimensions.Rectangle(0,0,100*64,100*64),location);
                    }
                    Check(who.Position==start && fold.Amount==0,"Opening delay must retain native collision and must not prematurely fold");
                    foreach(var delayed in Game1.delayedActions.ToArray())if(delayed.update(new GameTime(TimeSpan.Zero,TimeSpan.FromMilliseconds(401))))Game1.delayedActions.Remove(delayed);
                    who.movementDirections.Clear();
                    for(int i=0;i<15;i++)Invoke(mod,"OnUpdate",null,update);
                    Check(fold.Amount==0,"Standing beside an already-open door must keep wings fully spread");
                    who.movementDirections.Add(1);
                    Invoke(mod,"OnUpdate",null,update);
                    Check(fold.Amount==0,"Moving parallel to an open door must not fold wings");
                    who.movementDirections.Clear();who.movementDirections.Add(0);
                    float peak=0;
                    for(int i=0;i<18;i++)
                    {
                        Invoke(mod,"OnUpdate",null,update);
                        peak=Math.Max(peak,fold.Amount);
                        who.MovePosition(Game1.currentGameTime,new xTile.Dimensions.Rectangle(0,0,100*64,100*64),location);
                    }
                    Check(who.Position.Y<door.Position.Y*64-16,$"Flying into the opened {name} door at {door.Position} must not use stale closed-door connectivity; actual={who.Position}");
                    Check(peak==1,"Committed door crossing must reach the narrowest wing pose");
                    who.movementDirections.Clear();
                    for(int i=0;i<45;i++)Invoke(mod,"OnUpdate",null,update);
                    Check(fold.Amount==0,"Wings must fully restore after passing the door");
                    // Return across the same native door, including from inside the room.
                    who.Position=new Vector2(door.Position.X*64,door.Position.Y*64-40);
                    who.movementDirections.Add(2);fold.Reset();
                    for(int i=0;i<18;i++)
                    {
                        Invoke(mod,"OnUpdate",null,update);
                        who.MovePosition(Game1.currentGameTime,new xTile.Dimensions.Rectangle(0,0,100*64,100*64),location);
                    }
                    Check(who.Position.Y>=door.Position.Y*64+64,"Leaving the open interior door must remain usable");
                    who.movementDirections.Clear();
                    door.Set(false);door.ApplyMapModifications();
                    Invoke(mod,"OnUpdate",null,update);
                    var surface=(FlightMap)AccessTools.Field(typeof(ModEntry),"Surface").GetValue(mod)!;
                    Check(!surface.CanTraverse(new Rectangle(door.Position.X*64+8,door.Position.Y*64+16,48,32)),
                        "Reclosing a door must restore its collision and connectivity restriction");
                }
            }

            var oldRequest=Game1.locationRequest;
            try
            {
                Game1.game1.instanceGameLocation=original;who.currentLocation=original;who.Position=new Vector2(320,320);
                Set(mod,"Surface",new FlightMap(original,new Point(5,5)));flight.RetainForRecovery();
                foreach(bool outdoors in new[]{false,true})
                {
                    fold.Reset();
                    var destination=new GameLocation();destination.name.Value="DoorWarpFixture";destination.IsOutdoors=outdoors;destination.Map=original.Map;
                    Game1.locationRequest=new LocationRequest(destination.Name,false,destination);
                    Invoke(mod,"WarpCommittedPostfix");
                    for(int i=0;i<10;i++)Invoke(mod,"OnUpdate",null,update);
                    Check(outdoors?fold.Amount==0:fold.Amount==1,"Only an accepted indoor transition should fold; outdoor edge travel should not");
                    double phase=flight.Phase;float height=flight.Height;
                    who.currentLocation=destination;Game1.game1.instanceGameLocation=destination;
                    Game1.locationRequest=null;
                    var warped=Activator.CreateInstance(typeof(WarpedEventArgs),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{who,original,destination},null);
                    Invoke(mod,"OnWarped",null,warped);
                    Check(flight.Active && flight.Phase==phase && flight.Height==height,"Door warp must keep flight phase/height and native arrival intact");
                    for(int i=0;i<40;i++)Invoke(mod,"OnUpdate",null,update);
                    Check(fold.Amount==0,"Accepted door warp must unfold after arrival");
                    who.currentLocation=original;Game1.game1.instanceGameLocation=original;Set(mod,"Surface",new FlightMap(original,new Point(5,5)));
                }
            }
            finally {Game1.locationRequest=oldRequest;}
        }
        finally
        {
            Game1.delayedActions=oldDelayed;flight.Reset();fold.Reset();who.movementDirections.Clear();safety.LastSafe=null;
            who.modData.Remove("AngelWingsProject.VisualPreview/Recovery");
            Game1.game1.instanceGameLocation=original;who.currentLocation=original;who.Position=new Vector2(320,320);
            Set(mod,"Surface",new FlightMap(original,new Point(5,5)));
        }
        foreach(string failure in failures)Console.WriteLine("REGRESSION FAIL: "+failure);
        if(failures.Count>0)throw new Exception($"Door regression failures: {failures.Count}");
        Console.WriteLine("PASS: 12 native indoor doors in both directions; closed/open/400ms barrier refresh; committed-only fold, nearby/parallel no-fold, warp arrival and unfold.");
        return count;
    }
}
