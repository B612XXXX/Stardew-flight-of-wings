using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AngelWings.VisualPreview;
using AngelWings.VisualPreview.Flight;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Objects;
using StardewValley.Locations;
using xTile;

static class ArrivalRuntimeChecks
{
    const string RecoveryKey="AngelWingsProject.VisualPreview/Recovery";
    static void Set(object instance,string field,object? value) => AccessTools.Field(instance.GetType(),field).SetValue(instance,value);
    static void Invoke(ModEntry mod,string name,params object?[] args) => AccessTools.Method(typeof(ModEntry),name).Invoke(mod,args);

    public static int Run(Harmony harmony,ModEntry mod,Farmer who,FlightController flight,GameLocation original)
    {
        int count=0;
        var failures=new List<string>();
        void Check(bool value,string message) {count++;if(!value)failures.Add(message);}
        using var content=new LocalizedContentManager(new GameServiceContainer(),Path.Combine(Environment.GetCommandLineArgs()[1],"Content"));
        var oldContent=Game1.content;Game1.content=content;
        var oldTileContent=Game1.game1.xTileContent;Game1.game1.xTileContent=content;
        var safety=(FlightSafety)AccessTools.Field(typeof(ModEntry),"Safety").GetValue(mod)!;
        // Exercise the real marker writer as well as the real load/warp handlers.
        harmony.Unpatch(AccessTools.Method(typeof(ModEntry),"SaveRecoveryMarker"),HarmonyPatchType.Prefix,harmony.Id);
        var update=(UpdateTickingEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(UpdateTickingEventArgs));
        void Tick() => Invoke(mod,"OnUpdate",null,update);
        try
        {
            var house=new GameLocation();house.name.Value="FarmHouse";house.Map=content.Load<Map>("Maps/FarmHouse2");
            var bed=new BedFurniture {bedType=BedFurniture.BedType.Double,ItemId="2052"};
            bed.TileLocation=new Vector2(42,22);bed.boundingBox.Value=new Rectangle(42*64,22*64,3*64,3*64);
            house.furniture.Add(bed);
            Game1.game1.instanceGameLocation=house;who.currentLocation=house;
            // Read-only evidence from the affected save: a bed moved into the upgraded
            // house, with a recovery marker still pointing at the old room coordinates.
            who.Position=new Vector2(42*64,23*64+32);
            string stale=JsonSerializer.Serialize(new SafePosition("FarmHouse",776.4229f,575.45105f));
            who.modData[RecoveryKey]=stale;
            Vector2 wake=who.Position;
            Invoke(mod,"OnSaveLoaded",null,null);
            Check(!flight.Active,"Loading at the native bed spawn with a stale recovery record must stay grounded");
            Check(who.Position==wake,"Bed-load cleanup must preserve the native wake-up position");
            Check(!who.modData.ContainsKey(RecoveryKey),"Bed-load cleanup must remove the stale recovery record");
            who.movementDirections.Clear();who.movementDirections.Add(3);
            for(int i=0;i<12;i++)who.MovePosition(Game1.currentGameTime,new xTile.Dimensions.Rectangle(0,0,80*64,80*64),house);
            Check(who.Position.X<wake.X-32,"Actual native movement must be able to leave the bed after loading");
            who.movementDirections.Clear();
            foreach(string trigger in new[]{"OnDayStarted","OnUpdate","saving","key"})
            {
                who.Position=wake;who.modData[RecoveryKey]=stale;flight.RetainForRecovery();
                Set(mod,"RecoveryRetryMs",0d);
                if(trigger=="OnUpdate")Tick();
                else if(trigger=="OnDayStarted")Invoke(mod,trigger,null,null);
                else Invoke(mod,"Recover",trigger);
                Check(!flight.Active && who.Position==wake && !who.modData.ContainsKey(RecoveryKey),
                    $"{trigger} must release flight at a bed without moving the native spawn or retaining its marker");
            }
            Invoke(mod,"ToggleFlight");
            Check(!flight.Active,"Takeoff must not capture the native bed/sleep position");

            // Stale room coordinates may also be encountered after a non-bed load.
            who.Position=new Vector2(41*64,23*64);who.modData[RecoveryKey]=stale;
            Vector2 floor=who.Position;
            Invoke(mod,"OnSaveLoaded",null,null);
            Check(!flight.Active && who.Position==floor && !who.modData.ContainsKey(RecoveryKey),
                "An invalid old map seed must fall back to verified current ground, without teleporting");

            // Loading over water is still a recovery, never an unconditional reset.
            Game1.game1.instanceGameLocation=original;who.currentLocation=original;
            who.Position=new Vector2(7*64,5*64);
            who.modData[RecoveryKey]=JsonSerializer.Serialize(new SafePosition(original.NameOrUniqueName,5*64,5*64));
            Invoke(mod,"OnSaveLoaded",null,null);
            Check(!flight.Active && who.Position==new Vector2(5*64,5*64) && !who.modData.ContainsKey(RecoveryKey),
                "A load over water must still return to verified ground");

            var mountain=content.Load<Map>("Maps/Mountain");
            string[] warps=mountain.Properties["Warp"].ToString().Split(' ',StringSplitOptions.RemoveEmptyEntries);
            var mine=new GameLocation();mine.name.Value="Mine";mine.Map=content.Load<Map>("Maps/Mine");
            for(int i=0;i+4<warps.Length;i+=5)
            {
                if(warps[i+2]!="Mine")continue;
                var arrival=new Point(int.Parse(warps[i+3]),int.Parse(warps[i+4]));
                who.Position=arrival.ToVector2()*64+new Vector2(0,16);
                Game1.game1.instanceGameLocation=mine;who.currentLocation=mine;
                safety.LastSafe=null;
                flight.Reset();flight.RetainForRecovery();
                var warped=Activator.CreateInstance(typeof(WarpedEventArgs),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{who,original,mine},null);
                Invoke(mod,"OnWarped",null,warped);
                var surface=(FlightMap)AccessTools.Field(typeof(ModEntry),"Surface").GetValue(mod)!;
                Vector2 start=who.Position;
                who.movementDirections.Clear();who.movementDirections.Add(0);
                for(int n=0;n<12;n++)who.MovePosition(Game1.currentGameTime,new xTile.Dimensions.Rectangle(0,0,80*64,80*64),mine);
                Check(flight.CanTravel && who.Position.Y<start.Y-32,"Native flight must move away from the real Mountain -> Mine entrance");
                Check(safety.CanStand(who,mine,who.Position,surface),"Mine entrance must allow landing on native walkable floor");
            }

            foreach(int level in new[]{1,10,20})
            {
                var shaft=new MineShaft(level);shaft.Map=content.Load<Map>($"Maps/Mines/{level}");
                var buildings=shaft.Map.GetLayer("Buildings");
                for(int y=0;y<buildings.LayerHeight;y++)for(int x=0;x<buildings.LayerWidth;x++)
                {
                    int index=buildings.Tiles[x,y]?.TileIndex??-1;
                    if(index==115)shaft.tileBeneathLadder=new Vector2(x,y+1);
                    if(index==112)shaft.tileBeneathElevator=new Vector2(x,y+1);
                }
                foreach(Vector2 arrival in new[]{shaft.tileBeneathLadder,shaft.tileBeneathElevator}.Where(p=>p!=Vector2.Zero))
                {
                    Game1.game1.instanceGameLocation=shaft;who.currentLocation=shaft;
                    who.Position=arrival*64+new Vector2(0,16);safety.LastSafe=null;
                    flight.Reset();flight.RetainForRecovery();
                    var warped=Activator.CreateInstance(typeof(WarpedEventArgs),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{who,original,shaft},null);
                    Invoke(mod,"OnWarped",null,warped);
                    var surface=(FlightMap)AccessTools.Field(typeof(ModEntry),"Surface").GetValue(mod)!;
                    Check(shaft.IsLocationSpecificOccupantOnTile(arrival),"Fixture must exercise the real MineShaft entrance reservation");
                    Check(safety.CanStand(who,shaft,who.Position,surface),$"Mine level {level} arrival {arrival} must allow landing immediately");
                    Vector2 start=who.Position;
                    who.movementDirections.Clear();
                    Invoke(mod,"ToggleFlight");
                    for(int n=0;n<130;n++)Tick();
                    Check(!flight.Active && who.Position==start && !who.modData.ContainsKey(RecoveryKey),
                        $"Manual landing must finish at level {level} arrival {arrival} without emergency recovery");
                    flight.RetainForRecovery();
                    who.movementDirections.Clear();who.movementDirections.Add(2);
                    for(int n=0;n<12;n++) { Tick();who.MovePosition(Game1.currentGameTime,new xTile.Dimensions.Rectangle(0,0,80*64,80*64),shaft); }
                    Check(flight.CanTravel && who.Position.Y>start.Y+32,$"Native flight must leave level {level} entrance {arrival}");
                    who.Position=start;who.movementDirections.Clear();
                    var back=shaft.Map.GetLayer("Back").Tiles[(int)arrival.X,(int)arrival.Y];
                    back.Properties["AngelWingsNoFly"]="T";
                    var restricted=new FlightMap(shaft,new Point((int)arrival.X,(int)arrival.Y+1));
                    Check(!restricted.CanTraverse(who.GetBoundingBox()) && !safety.CanStand(who,shaft,start,restricted),
                        "Reserved mine arrival must still honor an explicit flight barrier");
                    back.Properties.Remove("AngelWingsNoFly");
                    var obstruction=new StardewValley.Object();obstruction.ItemId="130";obstruction.bigCraftable.Value=true;
                    shaft.objects.Add(arrival,obstruction);
                    Check(!safety.CanStand(who,shaft,start,surface),"A real object on a mine arrival must still prevent landing");
                    shaft.objects.Remove(arrival);
                    Check(!surface.IsDryStandTile((int)arrival.X,(int)arrival.Y-1),
                        "The actual ladder/elevator wall above its arrival must remain unsuitable for landing");
                }
            }
        }
        finally
        {
            Game1.content=oldContent;
            Game1.game1.xTileContent=oldTileContent;
            who.movementDirections.Clear();who.modData.Remove(RecoveryKey);flight.Reset();safety.LastSafe=null;
            Game1.game1.instanceGameLocation=original;who.currentLocation=original;who.Position=new Vector2(320,320);
            Set(mod,"Surface",new FlightMap(original,new Point(5,5)));
        }
        foreach(string failure in failures)Console.WriteLine("REGRESSION FAIL: "+failure);
        if(failures.Count>0)throw new Exception($"Arrival regression failures: {failures.Count}");
        Console.WriteLine("PASS: stale bed load, wake/save/update recovery, water recovery, native mine ladder/elevator travel and manual landing; barriers/objects retained.");
        return count;
    }
}
