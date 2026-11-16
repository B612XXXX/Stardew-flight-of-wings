using System.Reflection;
using System.Runtime.CompilerServices;
using AngelWings.VisualPreview;
using AngelWings.VisualPreview.Flight;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Tools;
using StardewModdingAPI.Events;
using Microsoft.Xna.Framework.Content;
using xTile;
using xTile.Layers;
using xTile.Tiles;

// Actual Farmer.MovePosition -> nextPosition -> game collision -> mod update loop.
// Only graphics/input-device/content setup is substituted; no user save is loaded.
static class MovementRuntimeChecks
{
    static int Count;
    static int NativeInputs;
    static bool ContextMultiplayer;
    static bool ActualMultiplayer;
    static bool MainPlayer = true;
    static int ScreenId;
    static bool ProbeNativeInput() { NativeInputs++;return false; }
    static bool NoChat(ref bool __result) { __result=false;return false; }
    static bool Zero(ref int __result) { __result=0;return false; }
    static bool Skip() => false;
    static bool Yes(ref bool __result) { __result=true;return false; }
    static bool No(ref bool __result) { __result=false;return false; }
    static bool ReadContextMultiplayer(ref bool __result) { __result=ContextMultiplayer;return false; }
    static bool ReadActualMultiplayer(ref bool __result) { __result=ActualMultiplayer;return false; }
    static bool ReadMainPlayer(ref bool __result) { __result=MainPlayer;return false; }
    static bool ReadScreenId(ref int __result) { __result=ScreenId;return false; }
    static bool NativeSpeed(ref float __result) { __result=6;return false; }
    static bool NoSpeedBuff(ref float __result) { __result=0;return false; }
    static void Check(bool ok,string message) { if(!ok)throw new Exception(message);Count++; }
    static void Set(object instance,string field,object? value) => AccessTools.Field(instance.GetType(),field).SetValue(instance,value);

    public static int Run()
    {
        FixtureMonitor.Errors.Clear();
        var h=new Harmony("AngelWingsProject.MovementRuntimeChecks");
        object? oldGame=Game1.game1,oldPlayer=Game1.player;
        try
        {
            void Prefix(Type type,string name,string callback) => h.Patch(AccessTools.Method(type,name),prefix:new HarmonyMethod(typeof(MovementRuntimeChecks),callback));
            Prefix(typeof(Farmer),"farmerInit",nameof(Skip));
            Prefix(typeof(StardewValley.BellsAndWhistles.PlayerStatusList),"AddSpriteDefinition",nameof(Skip));
            Prefix(typeof(AnimatedSprite),"LoadTexture",nameof(Skip));
            Prefix(typeof(StardewValley.Objects.Furniture),"timeToTurnOnLights",nameof(No));
            foreach(string name in new[]{"get_IsWorldReady","get_IsPlayerFree"})Prefix(typeof(Context),name,nameof(Yes));
            Prefix(typeof(Context),"get_IsMultiplayer",nameof(ReadContextMultiplayer));
            Prefix(typeof(Context),"get_IsMainPlayer",nameof(ReadMainPlayer));
            Prefix(typeof(Context),"get_ScreenId",nameof(ReadScreenId));
            Prefix(typeof(Game1),"get_IsMultiplayer",nameof(ReadActualMultiplayer));
            Prefix(typeof(Game1),"get_HostPaused",nameof(No));
            Prefix(typeof(Game1),nameof(Game1.shouldTimePass),nameof(Yes));
            Prefix(typeof(ModEntry),"UpdateController",nameof(Skip));
            Prefix(typeof(ModEntry),"SaveRecoveryMarker",nameof(Skip));
            Prefix(typeof(ModEntry),"Notice",nameof(Skip));
            Prefix(typeof(Game1),"get_IsChatting",nameof(NoChat));
            Prefix(typeof(Game1),nameof(Game1.getOldMouseX),nameof(Zero));
            Prefix(typeof(Game1),nameof(Game1.getOldMouseY),nameof(Zero));
            Prefix(typeof(Game1),nameof(Game1.didPlayerJustLeftClick),nameof(No));
            Prefix(typeof(Game1),nameof(Game1.didPlayerJustRightClick),nameof(No));
            Prefix(typeof(Farmer),nameof(Farmer.getMovementSpeed),nameof(NativeSpeed));
            Prefix(typeof(Farmer),"get_addedSpeed",nameof(NoSpeedBuff));
            Prefix(typeof(GameLocation),nameof(GameLocation.isFarmerCollidingWithAnyCharacter),nameof(No));
            Prefix(typeof(StardewModdingAPI.Events.UpdateTickingEventArgs),"get_IsOneSecond",nameof(No));
            Game1.game1=(Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
            var who=new Farmer();
            who.movementDirections=new List<int>();
            AccessTools.Field(typeof(Game1),"_player").SetValue(null,who);
            who.Sprite=new FarmerSprite(null);
            who.CanMove=true;who.health=100;
            var location=new GameLocation();location.name.Value="FlightFixture";location.IsOutdoors=true;
            Game1.game1.instanceGameLocation=location;who.currentLocation=location;
            var map=new Map();
            var sheet=new TileSheet("outdoors",map,"Maps/spring_outdoorsTileSheet",new xTile.Dimensions.Size(25,79),new xTile.Dimensions.Size(16,16));map.AddTileSheet(sheet);
            var back=new Layer("Back",map,new xTile.Dimensions.Size(20,20),new xTile.Dimensions.Size(16,16));
            map.AddLayer(back);map.AddLayer(new Layer("Buildings",map,back.LayerSize,back.TileSize));
            for(int y=0;y<20;y++)for(int x=0;x<20;x++)back.Tiles[x,y]=new StaticTile(back,sheet,BlendMode.Alpha,0);
            for(int y=3;y<=8;y++)for(int x=7;x<=8;x++)
            {back.Tiles[x,y].Properties["Water"]="T";back.Tiles[x,y].Properties["Passable"]="T";}
            location.Map=map;
            var mod=new ModEntry();Set(mod,"Ready",true);Set(mod,"Config",new ModConfig());
            AccessTools.Property(typeof(Mod),"Monitor").SetValue(mod,DispatchProxy.Create<IMonitor,FixtureMonitor>());
            AccessTools.Field(typeof(ModEntry),"Instance").SetValue(null,mod);
            var flight=(FlightController)AccessTools.Field(typeof(ModEntry),"Flight").GetValue(mod)!;
            Set(mod,"Surface",new FlightMap(location,new Point(5,5)));
            ModEntry.InstallPatches(h);
            Count += WingFeatureChecks.Run(who);
            Count += AcquisitionChecks.Run(h,mod,who,flight);
            Prefix(typeof(Game1),"get_fadeToBlack",nameof(No));
            ExerciseHostOnlyServer(mod,flight);
            Exercise(mod,who,flight,location);
            ExerciseAltitude(mod,who,flight,location);
            ProbeBridge(mod,who,flight,location);
            ProbeShoreAndMapEdge(mod,who,flight,location);
            Count+=ForestFenceRuntimeChecks.Run(mod,who,flight,location);
            // Stop at the native input boundary: the mod must neither swallow nor replay it.
            foreach(var method in new[]{AccessTools.Method(typeof(Game1),nameof(Game1.pressUseToolButton)),
                AccessTools.Method(typeof(Game1),nameof(Game1.pressActionButton)),AccessTools.Method(typeof(Farmer),nameof(Farmer.BeginUsingTool))})
                h.Patch(method,prefix:new HarmonyMethod(typeof(MovementRuntimeChecks),nameof(ProbeNativeInput)){priority=Priority.Last});
            ExerciseActionsAndWarps(mod,who,flight,location);
            ExerciseEventsAndMenus(mod,who,flight,location);
            Count+=FlightFeedbackRuntimeChecks.Run(h,mod,who,flight);
            Count+=ArrivalRuntimeChecks.Run(h,mod,who,flight,location);
            Count+=DoorRuntimeChecks.Run(mod,who,flight,location);
            Check(FixtureMonitor.Errors.Count==0,"Runtime logged an error: "+string.Join(";",FixtureMonitor.Errors));
            Console.WriteLine("PASS: actual MovePosition and mod update in four directions including shore/water traversal.");
            return Count;
        }
        finally
        {
            h.UnpatchAll(h.Id);
            AccessTools.Field(typeof(ModEntry),"Instance").SetValue(null,null);
            Game1.game1=(Game1?)oldGame;
            AccessTools.Field(typeof(Game1),"_player").SetValue(null,oldPlayer);
        }
    }

    static void ExerciseHostOnlyServer(ModEntry mod,FlightController flight)
    {
        ContextMultiplayer=true;
        ActualMultiplayer=false;
        MainPlayer=true;
        ScreenId=0;
        flight.Reset();
        AccessTools.Method(typeof(ModEntry),"ToggleFlight").Invoke(mod,null);
        Check(flight.Stage==FlightStage.TakingOff,
            "A host-only local multiplayer server with no second player must not disable flight");

        ScreenId=1;
        AccessTools.Method(typeof(ModEntry),"OnReturnedToTitle").Invoke(mod,new object?[]{null,null});
        Check(flight.Stage==FlightStage.TakingOff,
            "Closing a temporary local-coop picker must not reset the main player's flight state");
        ScreenId=0;

        ActualMultiplayer=true;
        flight.Reset();
        AccessTools.Method(typeof(ModEntry),"ToggleFlight").Invoke(mod,null);
        Check(flight.Stage==FlightStage.Grounded,
            "Flight must stay disabled when another player is actually connected");

        ContextMultiplayer=ActualMultiplayer=false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ProbeShoreAndMapEdge(ModEntry mod,Farmer who,FlightController flight,GameLocation original)
    {
        string game=Environment.GetCommandLineArgs()[1];
        using var content=new ContentManager(new GameServiceContainer(),Path.Combine(game,"Content"));
        var beach=new GameLocation();beach.name.Value="BeachShoreFixture";beach.IsOutdoors=true;
        beach.Map=content.Load<Map>("Maps/Beach");Game1.game1.instanceGameLocation=beach;who.currentLocation=beach;
        who.Position=new Vector2(27*64,25*64);var surface=new FlightMap(beach,new Point(27,25));Set(mod,"Surface",surface);
        var lip=new Rectangle(28*64+8,25*64+16,48,32);
        Check(surface.CanCrossWater(28,25)&&surface.CanTraverse(lip),"Native Beach dock lip over water must be classified as an airborne shoreline transition");
        Check(!beach.isCollidingPosition(lip,Game1.viewport,true,0,false,who,false,false,false,true),"Native Beach water/dock boundary must not block flight");
        Check(surface.WaterEffectAt(new Vector2(28*64+32,25*64+32))==WaterEffectKind.Ocean,
            "Beach water must select the ocean wake texture");
        using(FlightCollision.OriginalRules())Check(beach.isCollidingPosition(lip,Game1.viewport,true,0,false,who,false,false,false,true),"The same dock lip must retain the game's walking collision");

        var forest=new GameLocation();forest.name.Value="ForestShoreFixture";forest.IsOutdoors=true;
        forest.Map=content.Load<Map>("Maps/Forest");Game1.game1.instanceGameLocation=forest;who.currentLocation=forest;
        who.Position=new Vector2(27*64,17*64);surface=new FlightMap(forest,new Point(27,17));Set(mod,"Surface",surface);
        var winterShore=new Rectangle(28*64+8,19*64+16,48,32);
        Check(surface.CanCrossWater(28,19)&&surface.CanTraverse(winterShore),"Native Forest shoreline cover must be flyable without a seasonal tile whitelist");
        Check(!forest.isCollidingPosition(winterShore,Game1.viewport,true,0,false,who,false,false,false,true),"Native Forest land/water boundary must not snag flight");
        Check(surface.WaterEffectAt(new Vector2(35*64+32,29*64+32))==WaterEffectKind.Lake,
            "Cindersap northwest pond must select the ring-ripple texture");
        Check(surface.WaterEffectAt(new Vector2(80*64+32,60*64+32))==WaterEffectKind.River,
            "Cindersap river must select the small river-wake texture");

        Game1.game1.instanceGameLocation=original;who.currentLocation=original;who.Position=new Vector2(0,320);
        Set(mod,"Surface",new FlightMap(original,new Point(0,5)));
        var outside=new Rectangle(-4,who.GetBoundingBox().Y,48,32);
        using(FlightCollision.OriginalRules())Check(!original.isCollidingPosition(outside,Game1.viewport,true,0,false,who,false,false,false,true),"Fixture must confirm native map-edge movement is open for warp handling");
        Check(!original.isCollidingPosition(outside,Game1.viewport,true,0,false,who,false,false,false,true),"Flight must defer map-edge movement to the native warp/bounds rules");
        who.Position=new Vector2(320,320);Set(mod,"Surface",new FlightMap(original,new Point(5,5)));
        Console.WriteLine("PASS: actual Beach/Forest shoreline transitions and native map-edge handoff; walking collision retained.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ExerciseAltitude(ModEntry mod,Farmer who,FlightController flight,GameLocation location)
    {
        var layer=location.Map.GetLayer("Buildings");var sheet=location.Map.TileSheets[0];
        for(int y=2;y<12;y++)foreach(int x in new[]{8,10})layer.Tiles[x,y]=new StaticTile(layer,sheet,BlendMode.Alpha,383);
        Set(mod,"Surface",new FlightMap(location,new Point(2,5)));
        var altitude=(FlightAltitude)AccessTools.Field(typeof(ModEntry),"Altitude").GetValue(mod)!;
        altitude.Reset();flight.Reset();flight.Toggle(true,false);
        for(int k=0;k<7;k++)flight.Advance(100,true,false,false);
        who.Position=new Vector2(100,320);who.movementDirections.Clear();who.movementDirections.Add(1);
        var args=(UpdateTickingEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(UpdateTickingEventArgs));
        var rows=new List<object>();float peak=0;
        Check(Math.Abs(who.getMovementSpeed()-9.9f)<.001,"Installed native speed modifier must produce 6 * 1.65");
        for(int i=0;i<245;i++)
        {
            AccessTools.Method(typeof(ModEntry),"OnUpdate").Invoke(mod,new object?[]{null,args});
            var box=who.GetBoundingBox();
            if(box.Right>=512 && box.Left<704)Check(altitude.ExtraHeight>80,"Actual native movement reached fences before gaining clearance");
            if(i%3==0)rows.Add(new {Frame=i,X=who.Position.X,BodyBottom=box.Bottom,Extra=altitude.ExtraHeight});
            peak=Math.Max(peak,altitude.ExtraHeight);
            who.MovePosition(Game1.currentGameTime,new xTile.Dimensions.Rectangle(0,0,1280,1280),location);
            Check(flight.CanTravel,"Climbing must never cancel flight or normal movement");
        }
        Check(peak>87 && altitude.ExtraHeight<.1,"Actual mod update must climb then smoothly settle back to cruise");
        who.Position=new Vector2(512,320);who.movementDirections.Clear();
        for(int i=0;i<90;i++)AccessTools.Method(typeof(ModEntry),"OnUpdate").Invoke(mod,new object?[]{null,args});
        Check(altitude.ExtraHeight>87 && flight.MovementLean==0,"Stopped above a fence must hover high and upright");
        // Starting immediately beside a fence has no look-ahead runway.
        altitude.Reset();who.Position=new Vector2(440,320);who.movementDirections.Add(1);
        bool crossedClose=false;
        for(int i=0;i<70;i++)
        {
            AccessTools.Method(typeof(ModEntry),"OnUpdate").Invoke(mod,new object?[]{null,args});
            var before=who.GetBoundingBox();
            who.MovePosition(Game1.currentGameTime,new xTile.Dimensions.Rectangle(0,0,1280,1280),location);
            if(before.Right<512 && who.GetBoundingBox().Right>=512)
            {Check(altitude.ExtraHeight>80,"Close-start movement must gain clearance before crossing the fence");crossedClose=true;}
        }
        Check(crossedClose&&flight.CanTravel,"Close-start climb must resume movement, not cancel flight");
        altitude.Reset();who.Position=new Vector2(440,320);who.movementDirections.Clear();who.movementDirections.Add(3);
        who.MovePosition(Game1.currentGameTime,new xTile.Dimensions.Rectangle(0,0,1280,1280),location);
        Check(who.Position.X<440,"Low-altitude movement away from a fence must not be held");
        who.Position=new Vector2(440,320);who.movementDirections.Clear();who.movementDirections.Add(1);
        altitude.Reset();flight.Reset();flight.Toggle(true,false);
        for(int i=0;i<50;i++)
        {
            AccessTools.Method(typeof(ModEntry),"OnUpdate").Invoke(mod,new object?[]{null,args});
            who.MovePosition(Game1.currentGameTime,new xTile.Dimensions.Rectangle(0,0,1280,1280),location);
        }
        Check(flight.CanTravel && altitude.ExtraHeight>80 && who.Position.X>440,"Takeoff beside a fence should gain clearance before native travel resumes");
        File.WriteAllText("build/qa-0.2.8/native-clearance.json",System.Text.Json.JsonSerializer.Serialize(rows));
        for(int y=2;y<12;y++)foreach(int x in new[]{8,10})layer.Tiles[x,y]=null;
        who.Position=new Vector2(320,320);altitude.Reset();Set(mod,"Surface",new FlightMap(location,new Point(5,5)));
        Console.WriteLine("PASS: native movement + OnUpdate predicts the map fence, climbs before crossing, stays high when stopped, and settles after chained fences.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ProbeBridge(ModEntry mod,Farmer who,FlightController flight,GameLocation original)
    {
        string game=Environment.GetCommandLineArgs()[1];
        using var content=new ContentManager(new GameServiceContainer(),Path.Combine(game,"Content"));
        var location=new GameLocation();location.name.Value="RiverlandBridgeFixture";location.IsOutdoors=true;
        location.Map=content.Load<Map>("Maps/Farm_Fishing");
        Game1.game1.instanceGameLocation=location;who.currentLocation=location;
        var surface=new FlightMap(location,new Point(60,20));Set(mod,"Surface",surface);
        Check(surface.WaterEffectAt(new Vector2(22*64+32,25*64+32))==WaterEffectKind.River,
            "Riverland farm water must select the small river-wake texture");
        var rows=new List<object>();
        foreach(int x in new[]{22,23,24,25,26,27})foreach(int y in new[]{24,25,26})
        {
            var box=new Rectangle(x*64+8,y*64+16,48,32);
            who.Position=new Vector2((x-1)*64,y*64);
            bool allowed=surface.CanTraverse(box);
            bool collision=location.isCollidingPosition(box,Game1.viewport,true,0,false,who,false,false,false,true);
            rows.Add(new {X=x,Y=y,allowed,collision,water=surface.CanCrossWater(x,y),dry=surface.IsDryStandTile(x,y),
                layers=location.Map.Layers.Select(l=>new {layer=l.Id,tile=l.Tiles[x,y]}).Where(v=>v.tile!=null).Select(v=>new {v.layer,index=v.tile.TileIndex,
                    props=v.tile.Properties.Concat(v.tile.TileIndexProperties).Select(p=>p.Key+"="+p.Value)})});
        }
        Directory.CreateDirectory("build/qa-0.2.8");
        File.WriteAllText("build/qa-0.2.8/riverland-bridge-probe.json",System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        var testBox=new Rectangle(23*64+8,25*64+16,48,32);
        who.Position=new Vector2(22*64,25*64);
        bool Collides()=>location.isCollidingPosition(testBox,Game1.viewport,true,0,false,who,false,false,false,true);
        Check(surface.CanTraverse(testBox) && !Collides(),"Recognized bridge side must now allow airborne traversal");
        using(FlightCollision.OriginalRules())Check(Collides(),"Walking must still collide with the native bridge-side tile");
        Set(mod,"Surface",new FlightMap(location,new Point(24,25)));
        Check(!Collides(),"Flight from the wood deck must also pass the bridge side");
        Check(who.bridge==null&&!who.onBridge.Value,"Bridge diagnosis must not depend on suspension-bridge state");
        var layer=location.Map.GetLayer("Buildings");var barrier=layer.Tiles[23,25];
        barrier.Properties["AngelWingsNoFly"]="T";
        Check(Collides(),"Explicit no-fly markers must override the bridge exception");
        barrier.Properties.Remove("AngelWingsNoFly");barrier.Properties["Action"]="LockedDoor";
        Check(Collides(),"An interactive gate must not become a bridge-side exception");
        barrier.Properties.Remove("Action");
        var under=location.Map.GetLayer("Back").Tiles[23,25];under.Properties["TouchAction"]="Warp SecretRoom";
        Check(Collides(),"Touch-action barriers must not be bypassed");under.Properties.Remove("TouchAction");
        Set(mod,"Surface",new FlightMap(location,new Point(22,25)));
        Check(!Collides(),"A water-side bridge cover must stay flyable from either approach component");
        Set(mod,"Surface",new FlightMap(location,new Point(24,25)));
        layer.Tiles[22,25]=new StaticTile(layer,barrier.TileSheet,BlendMode.Alpha,16);
        var currentMap=(FlightMap)AccessTools.Field(typeof(ModEntry),"Surface").GetValue(mod)!;
        Check(currentMap.CanCrossWater(22,25),"A non-interactive visual cover over marked water must remain flyable without an index whitelist");
        layer.Tiles[22,25]=null;
        who.Position=new Vector2(22*64,25*64);who.movementDirections.Clear();who.movementDirections.Add(1);
        for(int i=0;i<40;i++)who.MovePosition(Game1.currentGameTime,new xTile.Dimensions.Rectangle(0,0,5120,5120),location);
        Check(who.Position.X>27*64,"Actual MovePosition must cross water, left barrier, bridge, right barrier and water");
        who.movementDirections.Clear();who.movementDirections.Add(3);
        for(int i=0;i<40;i++)who.MovePosition(Game1.currentGameTime,new xTile.Dimensions.Rectangle(0,0,5120,5120),location);
        Check(who.Position.X<23*64,"Actual reverse bridge crossing must remain usable");
        who.movementDirections.Clear();
        Console.WriteLine("PASS: actual Riverland bridge movement both ways; walking, no-fly and locked-gate rules retained.");
        Game1.game1.instanceGameLocation=original;who.currentLocation=original;
        who.Position=new Vector2(320,320);Set(mod,"Surface",new FlightMap(original,new Point(5,5)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ExerciseActionsAndWarps(ModEntry mod,Farmer who,FlightController flight,GameLocation location)
    {
        var args=(UpdateTickingEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(UpdateTickingEventArgs));
        var failures=new List<string>();
        void Expect(bool ok,string message) { if(!ok)failures.Add(message);else Count++; }
        void Start() { Set(mod,"RecoveryRetryMs",0d);flight.Reset();flight.Toggle(true,false);for(int k=0;k<7;k++)flight.Advance(100,true,false,false); }
        void Tick() => AccessTools.Method(typeof(ModEntry),"OnUpdate").Invoke(mod,new object?[]{null,args});
        who.movementDirections.Clear();
        foreach(Tool tool in new Tool[]{new Axe(),new Pickaxe(),new MeleeWeapon(),new FishingRod()})
        {
            who.Items.Clear();who.Items.Add(tool);who.CurrentToolIndex=0;
            Start();NativeInputs=0;
            Game1.pressUseToolButton();who.BeginUsingTool();
            Expect(NativeInputs==2 && !flight.LandingQueued,"Native tool input blocked/queued landing: "+tool.GetType().Name);
            who.UsingTool=true;Tick();
            Expect(flight.Active && flight.Height==1,"Tool update cancelled flight: "+tool.GetType().Name);
            for(int i=0;i<90;i++)Tick();
            Expect(flight.Active && !flight.LandingQueued && flight.Height==1,"Long tool action ended flight: "+tool.GetType().Name);
            Expect(Math.Abs(who.getMovementSpeed()-6)<.001,"Tool action must not receive a flight speed bonus");
            AccessTools.Method(typeof(ModEntry),"ToggleFlight").Invoke(mod,null);
            for(int i=0;i<90;i++)Tick();
            Expect(flight.Stage==FlightStage.Flying && flight.LandingQueued,"Explicit landing must wait for the native action to end");
            who.UsingTool=false;Tick();
            for(int i=0;i<130;i++)Tick();
            Expect(!flight.Active,"Explicit landing on safe ground must finish after the action");
        }
        Start();NativeInputs=0;
        Game1.pressActionButton(default,default,default);
        Expect(NativeInputs==1&&!flight.LandingQueued,"Native NPC/chest action input was blocked");
        Game1.dialogueUp=true;Tick();Expect(flight.Active,"Dialogue ended flight");Game1.dialogueUp=false;
        Start();who.isEating=true;Tick();Expect(flight.Active,"Eating ended flight");who.isEating=false;
        foreach(bool outdoors in new[]{true,false})
        {
            Start();flight.Advance(32,true,false,true);
            var altitude=(FlightAltitude)AccessTools.Field(typeof(ModEntry),"Altitude").GetValue(mod)!;
            for(int i=0;i<120;i++)altitude.Advance(16,true,88,true);
            float extra=altitude.ExtraHeight;
            var destination=new GameLocation();destination.name.Value=outdoors?"BusStopFixture":"IndoorFixture";
            destination.IsOutdoors=outdoors;destination.Map=location.Map;
            Game1.game1.instanceGameLocation=destination;who.currentLocation=destination;who.Position=new Vector2(384,384);
            who.modData["AngelWingsProject.VisualPreview/Recovery"]="fixture-existing-safe-marker";
            double phase=flight.Phase,seconds=flight.VisualSeconds;float height=flight.Height;Vector2 position=who.Position;
            var warped=Activator.CreateInstance(typeof(WarpedEventArgs),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{who,location,destination},null);
            AccessTools.Method(typeof(ModEntry),"OnWarped").Invoke(mod,new[]{null,warped});
            Expect(flight.Active && flight.CanTravel && flight.Phase==phase && flight.Height==height && flight.VisualSeconds==seconds,
                "Warp must retain continuous flight: outdoors="+outdoors);
            Expect(who.Position==position,"Warp handler moved the native arrival position");
            Expect(altitude.ExtraHeight==extra,"Warp must not snap adaptive height");
            Tick();Expect(flight.Active,"Post-warp tick cancelled flight: outdoors="+outdoors);
            who.movementDirections.Add(1);Vector2 start=who.Position;
            for(int k=0;k<8;k++){who.MovePosition(Game1.currentGameTime,new xTile.Dimensions.Rectangle(0,0,1280,1280),destination);Tick();}
            Expect(flight.CanTravel && who.Position.X>start.X+50,"Native movement after warp must remain usable: outdoors="+outdoors);
            who.movementDirections.Clear();
            Game1.game1.instanceGameLocation=location;who.currentLocation=location;
            Set(mod,"Surface",new FlightMap(location,new Point(5,5)));
        }
        // Recovery-initiated warps are deliberately different from ordinary travel.
        Start();
        var safety=(FlightSafety)AccessTools.Field(typeof(ModEntry),"Safety").GetValue(mod)!;
        safety.LastSafe=new SafePosition(location.NameOrUniqueName,384,384);
        Set(mod,"PendingRecovery",safety.LastSafe);
        who.Position=new Vector2(384,384);
        var recoveryArgs=Activator.CreateInstance(typeof(WarpedEventArgs),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{who,location,location},null);
        AccessTools.Method(typeof(ModEntry),"OnWarped").Invoke(mod,new[]{null,recoveryArgs});
        Expect(!flight.Active && !who.modData.ContainsKey("AngelWingsProject.VisualPreview/Recovery"),"Emergency recovery warp must still finish safely grounded");
        foreach(string failure in failures)Console.WriteLine("REGRESSION FAIL: "+failure);
        Check(failures.Count==0,"Airborne actions/scene retention regression failures: "+failures.Count);
        Console.WriteLine("PASS: native action dispatch, axe/pickaxe/weapon/rod update and deferred manual landing, dialogue/eating, outdoor/indoor warp plus post-warp movement, emergency warp.");
    }

    static void ExerciseEventsAndMenus(ModEntry mod,Farmer who,FlightController flight,GameLocation location)
    {
        var args=(UpdateTickingEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(UpdateTickingEventArgs));
        var safety=(FlightSafety)AccessTools.Field(typeof(ModEntry),"Safety").GetValue(mod)!;
        void Tick() => AccessTools.Method(typeof(ModEntry),"OnUpdate").Invoke(mod,new object?[]{null,args});
        void StartOverWater()
        {
            Game1.eventUp=false;
            Game1.eventOver=false;
            location.currentEvent=null;
            Game1.game1.instanceGameLocation=location;
            who.currentLocation=location;
            who.CanMove=true;
            who.Position=new Vector2(7*64,5*64);
            safety.LastSafe=new SafePosition(location.NameOrUniqueName,5*64,5*64);
            Set(mod,"Surface",new FlightMap(location,new Point(5,5)));
            flight.Reset();
            flight.Toggle(true,false);
            for(int i=0;i<7;i++)flight.Advance(100,true,false,false);
        }

        StartOverWater();
        Vector2 eventEntry=who.Position;
        AccessTools.Method(typeof(ModEntry),"EventPrefix").Invoke(null,new object[]{location});
        Check(!flight.Active,"A native event must immediately remove flight collision, speed and drawing state");
        Check(who.Position==eventEntry,"Event startup must not relocate the player before the vanilla event captures its entry state");
        Check(who.CanMove,"The event prefix must leave CanMove ownership to the vanilla event");

        Game1.eventUp=true;
        who.CanMove=false;
        Tick();
        Check(who.Position==eventEntry&&!flight.Active&&!who.CanMove,
            "An active cutscene must retain its entry position and native movement lock without recovery retries");
        Game1.eventUp=false;
        who.CanMove=true;
        Tick();
        Check(!flight.Active&&who.Position!=eventEntry&&who.CanMove,
            "After the event releases control, an unsafe airborne entry must recover to verified ground without retaining the lock");

        StartOverWater();
        MethodInfo? localCoopPrefix=AccessTools.Method(typeof(ModEntry),"LocalCoopJoinPrefix");
        Check(localCoopPrefix!=null,"Flight must guard the vanilla local-coop entry which emits the no-slots HUD message");
        object?[] prefixArgs={true};
        bool runOriginal=(bool)localCoopPrefix!.Invoke(null,prefixArgs)!;
        Check(!runOriginal&&prefixArgs[0] is false,
            "Opening options while flying must not enter the vanilla no-slots notification path");
        flight.Reset();
        prefixArgs[0]=false;
        runOriginal=(bool)localCoopPrefix.Invoke(null,prefixArgs)!;
        Check(runOriginal,"Grounded players must retain the original local-coop menu behavior");
        Console.WriteLine("PASS: cutscene handoff preserves native position/control and flying options cannot emit the vanilla no-slots prompt.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Exercise(ModEntry mod,Farmer who,FlightController flight,GameLocation location)
    {
        var elapsed=new GameTime(TimeSpan.Zero,TimeSpan.FromMilliseconds(16));Game1.currentGameTime=elapsed;
        var args=(StardewModdingAPI.Events.UpdateTickingEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(StardewModdingAPI.Events.UpdateTickingEventArgs));
            foreach(int direction in new[]{0,1,2,3})
            {
                who.Position=new Vector2(320,320);
                who.movementDirections.Clear();
                who.movementDirections.Add(direction);
                flight.Reset();flight.Toggle(true,false);
                for(int k=0;k<7;k++)flight.Advance(100,true,false,false);
                Vector2 start=who.Position;
                for(int i=0;i<24;i++)
                {
                    who.MovePosition(elapsed,new xTile.Dimensions.Rectangle(0,0,1280,1280),location);
                    AccessTools.Method(typeof(ModEntry),"OnUpdate").Invoke(mod,new object?[]{null,args});
                    Check(flight.Active && flight.CanTravel,"Native movement cancelled flight in direction "+direction+"; "+string.Join("; ",FixtureMonitor.Errors));
                }
                Check(Vector2.Distance(start,who.Position)>150,"Native movement did not move the player across ground/water");
            }
    }
}

public class FixtureMonitor : DispatchProxy
{
    public static readonly List<string> Errors=new();
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if (args?.Any(a=>a is LogLevel level && level>=LogLevel.Warn)==true)
            Errors.Add(string.Join(" ",args));
        return method?.ReturnType==typeof(bool)?false:null;
    }
}
