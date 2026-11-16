using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using AngelWings.VisualPreview.Flight;
using Microsoft.Xna.Framework;
using StardewValley;
using xTile;
using xTile.Layers;
using xTile.Tiles;
using AngelWings.VisualPreview;
using StardewModdingAPI;
using StardewValley.Menus;
using System.Text.Json;
using System.Text.RegularExpressions;

static class Program
{
    static int Count;
    static void Main(string[] args)
    {
        string game=args[0];
        AssemblyLoadContext.Default.Resolving+=(c,n)=>{
            foreach(string d in new[]{game,Path.Combine(game,"smapi-internal")})
            { string p=Path.Combine(d,n.Name+".dll"); if(File.Exists(p))return c.LoadFromAssemblyPath(p); }
            return null;
        };
        Run();
        Console.WriteLine($"PASS: {Count} flight behavior checks; no game session or save opened.");
    }
    static void Check(bool value,string message)
    { if(!value)throw new Exception(message); Count++; }
    static void Advance(FlightController f,int ticks,bool safe=true,bool fishing=false)
    { for(int i=0;i<ticks;i++)f.Advance(10,safe,fishing,false); }
    static FlightController Flying()
    { var f=new FlightController();f.Toggle(true,false);Advance(f,70);return f; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        CheckControllerAndControls();
        CheckFlightVisualPose();
        CheckWaterWake();
        Count += AdaptiveFlightChecks.Run();
        Count += MovementRuntimeChecks.Run();
        Count += PoseDrawingChecks.Run();
        var f=new FlightController();
        Check(f.MovementSpeed(6,1.5f,false)==6,"Ground speed must remain original");
        f.Toggle(true,false);Advance(f,69);
        Check(f.Stage==FlightStage.TakingOff&&!f.CanTravel,"Takeoff must finish before water traversal");
        Advance(f,1);
        Check(f.Stage==FlightStage.Flying&&f.Height==1&&f.Opacity==1,"Takeoff must reach full height at 700 ms");
        Check(Math.Abs(f.Phase-.55)<1e-8,"Takeoff must join H at its exported end phase");
        Check(f.Toggle(false,false)==ToggleResult.CannotLand&&f.Stage==FlightStage.Flying&&f.Height==1,"Water landing must not remove flight");
        Check(f.MovementSpeed(6,1.5f,false)==9&&f.MovementSpeed(6,1.5f,false)==9,"Existing buffs preserved; repeated calls must not stack");
        Check(f.MovementSpeed(0,1.5f,false)==0,"A game-imposed zero speed must remain zero");
        Check(f.MovementSpeed(6,1.5f,true)==6,"No speed bonus while fishing");
        f.Toggle(false,true);Advance(f,200,false,true);
        Check(f.Stage==FlightStage.Flying&&f.LandingQueued&&!f.CanTravel,"Fishing landing request must wait");
        Check(f.Advance(10,false,false,false)&&!f.LandingQueued&&f.Stage==FlightStage.Flying,"Queued fishing landing on water must be rejected");
        f.Toggle(true,false);Advance(f,180);
        Check(f.Stage==FlightStage.Grounded&&f.Height==0&&f.Opacity==0,"Landing must return to ground and hide wings");
        Check(f.MovementSpeed(6,1.5f,false)==6,"Exit must remove only flight multiplication");
        f=new FlightController();f.Toggle(true,false);Advance(f,20);float before=f.Height;f.Toggle(true,false);
        Check(f.Height==before&&f.Stage==FlightStage.TakingOff,"Early toggle must not snap transition height");
        Advance(f,240);Check(!f.Active,"Queued takeoff cancellation must eventually land");
        f=Flying();f.Toggle(true,false);
        for(int i=0;i<100&&f.Stage!=FlightStage.Landing;i++)f.Advance(10,true,false,false);
        Check(f.Stage==FlightStage.Landing&&Math.Abs(f.Phase-.15)<1e-8,"Land at matching .15 wing phase");
        Advance(f,35);before=f.Height;f.Advance(10,false,false,false);
        Check(f.Stage==FlightStage.Flying&&f.Height==before&&f.Active,"Occupied landing target must abort without an instant height jump");
        Advance(f,40,false);Check(f.Height==1,"Aborted descent must recover altitude");
        f=Flying();f.Toggle(true,false);
        while(f.Stage!=FlightStage.Landing)f.Advance(10,true,false,false);
        Advance(f,25);before=f.Height;f.Toggle(true,false);
        Check(f.Height==before,"Restart request during landing must retain height");
        Advance(f,45);Check(f.Stage==FlightStage.TakingOff&&f.Height==0,"Restart only after grounded endpoint");
        Advance(f,70);Check(f.Stage==FlightStage.Flying,"Queued restart completes");
        f.Reset();Check(!f.Active&&!f.LandingQueued&&f.MovementSpeed(8,1.5f,false)==8,"Reset must clear all flight contributions");

        f=Flying();
        Check(!f.RequestLanding(false,false)&&f.CanTravel,"A ground action on water cannot queue landing");
        for(int i=0;i<15;i++)f.RequestLanding(true,false);
        Check(f.LandingQueued&&!f.DesiredFlight,"Repeated held input cannot cancel its landing request");
        Advance(f,180);Check(!f.Active,"A repeated ground action still reaches the ground");
        f=new FlightController();f.Toggle(true,false);Advance(f,15);
        f.RequestLanding(true,false);f.RequestLanding(true,false);Advance(f,260);
        Check(!f.Active,"Ground action during takeoff waits for a full safe landing");
        f.RetainForRecovery();
        Check(f.Active&&f.Height==1&&f.CanTravel,"Failed reload recovery must retain usable flight");
        f.RequestLanding(true,false);
        while(f.Stage!=FlightStage.Landing)f.Advance(10,true,false,false);
        Advance(f,20);before=f.Height;f.RetainForRecovery();
        Check(f.Height==before&&!f.LandingQueued&&f.CanTravel,"Failed warp recovery cancels descent without a height snap");

        var location=new GameLocation();
        var map=new Map();
        var sheet=new TileSheet("outdoors",map,"Maps/spring_outdoorsTileSheet",new xTile.Dimensions.Size(25,79),new xTile.Dimensions.Size(16,16));
        map.AddTileSheet(sheet);
        var back=new Layer("Back",map,new xTile.Dimensions.Size(9,7),new xTile.Dimensions.Size(16,16));
        var buildings=new Layer("Buildings",map,new xTile.Dimensions.Size(9,7),new xTile.Dimensions.Size(16,16));
        map.AddLayer(back);map.AddLayer(buildings);
        for(int y=0;y<7;y++)for(int x=0;x<9;x++)
        {
            var tile=new StaticTile(back,sheet,BlendMode.Alpha,0);
            if(x==4) { tile.Properties["Water"]="T";tile.Properties["Passable"]="T"; }
            back.Tiles[x,y]=tile;
        }
        location.Map=map;
        var surface=new FlightMap(location,new Point(1,3));
        Check(surface.IsReachableGround(3,3),"Connected shore should be reachable");
        Check(surface.CanCrossWater(4,3),"Marked water without an upper obstacle should be flyable");
        Check(!surface.IsReachableGround(5,3),"Flight must not unlock disconnected land beyond an unrepaired crossing");
        Check(!surface.CanTraverse(new Rectangle(5*64+8,3*64+16,48,32)),"Unopened ground remains blocked");
        Check(!surface.CanTraverse(new Rectangle(-1,64,48,32)),"Negative map bounds must remain blocked");
        Check(!surface.CanTraverse(new Rectangle(9*64-16,64,48,32)),"Far edge must remain blocked");
        buildings.Tiles[4,3]=new StaticTile(buildings,sheet,BlendMode.Alpha,183);
        Check(surface.CanCrossWater(4,3),"Identified shore lip must be traversable");
        buildings.Tiles[4,3]=new StaticTile(buildings,sheet,BlendMode.Alpha,300);
        Check(surface.CanCrossWater(4,3),"A non-interactive shoreline tile over marked water must not snag flight");
        buildings.Tiles[4,3]=new StaticTile(buildings,sheet,BlendMode.Alpha,183);
        buildings.Tiles[4,3].Properties["Action"]="LockedDoor";
        Check(!surface.CanCrossWater(4,3),"Action gates must not be bypassed by the water rule");
        buildings.Tiles[4,3]=null;
        back.Tiles[4,3].Properties["TemporaryBarrier"]="T";
        Check(!surface.CanCrossWater(4,3),"Temporary story barriers must survive");
        back.Tiles[4,3].Properties.Remove("TemporaryBarrier");
        buildings.Tiles[2,3]=new StaticTile(buildings,sheet,BlendMode.Alpha,383);
        surface=new FlightMap(location,new Point(1,3));
        Check(surface.CanCrossMapLowObstacle(2,3),"Identified map fence should be flyable");
        Check(!surface.IsDryStandTile(2,3),"Map fence cannot be a landing place");
        buildings.Tiles[2,3].Properties["Action"]="LockedGate";
        Check(!surface.CanCrossMapLowObstacle(2,3),"An interactive lock is not an ordinary low fence");
        back.Tiles[0,0]=null;
        Check(!surface.CanTraverse(new Rectangle(8,16,48,32)),"Void inside the map rectangle remains blocked");
        back.Tiles[4,2].Properties.Remove("Water");back.Tiles[4,2].Properties.Remove("Passable");
        surface=new FlightMap(location,new Point(1,3));
        Check(surface.IsReachableGround(5,3),"An opened crossing should admit the newly connected area");
        Check(!surface.CanCrossWater(3,3),"Ordinary land is not a water exception");
    }

    static void CheckFlightVisualPose()
    {
        Check(FlightVisuals.IdleFrame(0)==12 && FlightVisuals.IdleFrame(1)==6
            && FlightVisuals.IdleFrame(2)==0 && FlightVisuals.IdleFrame(3)==6,
            "Flight pose must use the vanilla upright idle frame for each direction");
        Check(Math.Abs(FlightVisuals.BodyLeanRadians(1) - MathF.PI/12f) < 1e-6f
            && Math.Abs(FlightVisuals.BodyLeanRadians(3) + MathF.PI/12f) < 1e-6f
            && FlightVisuals.BodyLeanRadians(2)==0f
            && FlightVisuals.BodyLeanRadians(0)==0f,
            "Front pitch must not be a sideways screen-plane rotation; side lean stays 15 degrees");
        float stillPeak=Math.Abs(FlightVisuals.HoverBob(1.2, true));
        float movingPeak=Math.Abs(FlightVisuals.HoverBob(1.2, false));
        Check(Math.Abs(stillPeak-5)<1e-5 && movingPeak>0f && movingPeak<.5f,
            "Stationary flight bob must peak at exactly +/-5 world pixels and reduce while moving");
        var hover = Flying();
        double lastTime = hover.VisualSeconds;
        float lastBob = FlightVisuals.HoverBob(lastTime, true);
        for (int i=0;i<1000;i++)
        {
            hover.Advance(10,true,false,false);
            float bob = FlightVisuals.HoverBob(hover.VisualSeconds,true);
            Check(hover.VisualSeconds > lastTime && Math.Abs(bob-lastBob)<.07f,
                "Hover must remain continuous across wing-loop boundaries");
            lastTime=hover.VisualSeconds; lastBob=bob;
        }
        CheckMovementLean();
    }

    static void CheckMovementLean()
    {
        var f = Flying();
        Check(f.PoseAmount==0, "Taking off in place must end perfectly upright");
        Advance(f,200);
        Check(f.MovementLean==0, "Idle flight must not gradually acquire a tilt");
        float previous=0;
        for(int i=0;i<80;i++)
        {
            f.Advance(10,true,false,true);
            Check(f.MovementLean>=previous && f.MovementLean-previous<.07f && f.PoseAmount<=1,
                "Starting movement must smoothly and monotonically approach the flight lean");
            previous=f.MovementLean;
        }
        Check(f.PoseAmount==1, "Sustained movement must reach the complete forward pose");
        for(int i=0;i<100;i++)
        {
            f.Advance(10,true,false,false);
            Check(f.MovementLean<=previous && previous-f.MovementLean<.06f,
                "Stopping must ease upright without an angular snap");
            previous=f.MovementLean;
        }
        Check(f.PoseAmount==0, "Stopping must finish at exactly upright");
        var fine=Flying();var coarse=Flying();
        for(int i=0;i<30;i++)fine.Advance(10,true,false,true);
        for(int i=0;i<3;i++)coarse.Advance(100,true,false,true);
        Check(Math.Abs(fine.MovementLean-coarse.MovementLean)<1e-6,
            "Lean interpolation must not depend on update rate");
        previous=fine.MovementLean;
        fine.Advance(0,true,false,false);
        Check(fine.MovementLean==previous, "Reversing a target with zero elapsed time must not jump");
        for(int i=0;i<15;i++)
        {
            previous=fine.MovementLean;fine.Advance(10,true,false,i%2==0);
            Check(Math.Abs(fine.MovementLean-previous)<.07f, "Rapid start-stop input must stay continuous");
        }
        for(int i=0;i<100;i++)fine.Advance(10,true,true,true);
        Check(fine.PoseAmount==0, "Fishing must settle upright even if movement input remains held");
        fine.Reset();Check(fine.PoseAmount==0 && fine.MovementLean==0,"Reset must remove lean and velocity");
        Check(FlightVisuals.HoverBob(1.2,0f)==5 && Math.Abs(FlightVisuals.HoverBob(1.2,.5f)-2.675f)<1e-5,
            "Bob amplitude must interpolate with the same continuous movement blend");
    }

    static void CheckWaterWake()
    {
        var wake=new FlightWake();object location=new();Vector2 p=new(100,100);
        bool Water(Vector2 point)=>point.X>=64 && point.X<640 && point.Y>=64 && point.Y<640;
        wake.Advance(16,location,p,true,Water);
        for(int i=0;i<100;i++)wake.Advance(16,location,p,true,Water);
        Check(wake.Ripples.Count==0,"Stationary hover must not emit water effects");
        for(int i=0;i<35;i++){p.X+=4;wake.Advance(16,location,p,true,Water);}
        Check(wake.Ripples.Count>=2 && wake.Ripples.Count<=10,"Actual water movement must emit a bounded ripple trail");
        var samples=wake.Samples(Water).ToArray();
        Check(samples.Length>0 && samples.All(q=>q.Opacity>0 && q.Opacity<=1 && Water(q.Position)),"Texture wakes must fade and remain anchored to open water");
        for(int i=0;i<50;i++)wake.Advance(16,location,p,true,Water);
        Check(wake.Ripples.Count==0,"Stopping emits nothing new; the last ripples fade within 700ms");
        for(int i=0;i<40;i++){p.Y+=4;wake.Advance(16,location,p,false,Water);}
        Check(wake.Ripples.Count==0,"Grounded, taking-off, landing and fishing states must not emit");
        for(int i=0;i<40;i++){p.Y+=4;wake.Advance(16,location,p,true,_=>false);}
        Check(wake.Ripples.Count==0,"Movement on land must never emit water effects");
        for(int i=0;i<10;i++){p.X+=4;wake.Advance(16,location,p,true,_=>true);}
        Check(wake.Ripples.Count>0,"Re-entering water resumes ripples");
        wake.Advance(16,new object(),p,true,_=>true);
        Check(wake.Ripples.Count==0,"Warping must clear effects rather than carry them to another map");
        wake.Advance(double.NaN,location,p,true,Water);
        Check(wake.Ripples.Count==0,"Invalid time must not emit or corrupt the effect state");
        var splash=new FlightWake();p=new Vector2(256,256);
        splash.Advance(16,location,p,true,_=>true,_=>WaterEffectKind.Ocean);p.X+=20;
        splash.Advance(16,location,p,true,_=>true,_=>WaterEffectKind.Ocean);
        Check(splash.Ripples.Count==1 && splash.Ripples[0].Kind==WaterEffectKind.Ocean,
            "Ocean water must select the dedicated high-to-low sprite strip");
        Check(splash.Samples(_=>true).Single().Frame==0,"A new ocean wake must begin at its tallest painted frame");
        for(int i=0;i<4;i++)splash.Advance(100,location,p,false,_=>true,_=>WaterEffectKind.Ocean);
        Check(splash.Samples(_=>true).Single().Frame==4,"Ocean wake timing must progress through painted frames without generated geometry");
        Check(FlightWake.Spec(WaterEffectKind.Ocean).LifetimeMs==600
            && FlightWake.Spec(WaterEffectKind.River).LifetimeMs==450
            && FlightWake.Spec(WaterEffectKind.Lake).LifetimeMs==720,
            "Ocean, river and lake texture timing must remain intentionally distinct");
        Check(new ModConfig().HoverHeight==36,"New installations should hover higher at 36 world pixels");
    }

    static void CheckControllerAndControls()
    {
        var gesture = new ControllerGesture();
        Check(gesture.Advance(true,true,100)==WingInputAction.None,"A tap must not also fire before its release");
        Check(gesture.Advance(false,true,10)==WingInputAction.Toggle,"A short stick click must toggle once on release");
        Check(gesture.Advance(false,true,10)==WingInputAction.None,"Idle ticks must not repeat the tap");
        bool quietBeforeHold=true;
        for(int i=0;i<11;i++) quietBeforeHold &= gesture.Advance(true,true,100)==WingInputAction.None;
        Check(quietBeforeHold,"Holding before 1.2s must not toggle");
        Check(gesture.Advance(true,true,100)==WingInputAction.Recover,"A 1.2s hold must request recovery");
        Check(gesture.Advance(true,true,100)==WingInputAction.None,"A continued hold must not repeat recovery");
        Check(gesture.Advance(false,true,10)==WingInputAction.None,"Release after recovery must not toggle again");
        gesture.Advance(true,true,100);
        gesture.Advance(true,false,100); // menu, text entry, disconnection or loss of focus
        gesture.Advance(true,true,100);
        Check(gesture.Advance(false,true,10)==WingInputAction.None,"An interrupted hold must not become a tap when play resumes");
        gesture.Advance(true,true,100);
        Check(gesture.Advance(false,false,10)==WingInputAction.None,"Disconnect/release outside gameplay must not toggle");
        gesture.Advance(true,true,100);
        gesture.Cancel();
        Check(gesture.Advance(true,true,100)==WingInputAction.None,"A key held across a warp must not rearm");
        Check(gesture.Advance(false,true,10)==WingInputAction.None,"Release after cancellation must be ignored");
        gesture.Advance(true,true,100);
        Check(gesture.Advance(false,true,10)==WingInputAction.Toggle,"A fresh press after cancellation must work");
        gesture.Advance(true,true,0);
        Check(gesture.Advance(true,true,double.NaN)==WingInputAction.None,"Invalid tick duration must not fire recovery");
        gesture.Cancel();

        var config = new ModConfig();
        Check(config.ToggleKey==SButton.F8 && config.EmergencyLandKey==SButton.F10 && config.ControllerToggleKey==SButton.LeftStick,
            "Controller support must retain both existing keyboard defaults");
        foreach (string language in new[]{"default","zh"})
        {
            var strings=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"i18n",language+".json")))!;
            string Translate(string key,object? tokens)
            {
                string value=strings[key];
                return Regex.Replace(value,@"\{\{(\w+)\}\}",m=>tokens?.GetType().GetProperty(m.Groups[1].Value)?.GetValue(tokens)?.ToString() ?? throw new Exception("Missing translation token"));
            }
            config.ToggleKey=SButton.F6;
            var hints=FlightControlsOptions.Describe(config,Translate);
            Check(hints.Length==4 && hints.All(h=>h.Title.Length>0&&h.Detail.Length>0&&!h.Detail.Contains("{{")),"All four controls instructions must be localized with resolved tokens");
            Check(hints[0].Detail.Contains("F6") && !hints[0].Detail.Contains("F8") && hints[0].Detail.Contains("L3"),"Options must describe the current configured keys, not hardcoded defaults");
            Check(hints[1].Detail.Contains("F10") && hints[1].Detail.Contains("1.2"),"Options must expose both emergency controls and the hold duration");
            var row=new WingControlHint(()=>hints[0]);
            Check(row.ScreenReaderText==hints[0].Title && row.ScreenReaderDescription==hints[0].Detail,"Controller instructions must also have screen reader text");
            Check((OptionsElement)row is not OptionsInputListener,"Instruction rows must not act as native rebind/reset controls");
            config.ControllerToggleKey=SButton.None;
            Check(!FlightControlsOptions.Describe(config,Translate)[0].Detail.Contains("L3"),"A disabled controller binding must not advertise L3");
            config.ControllerToggleKey=SButton.LeftStick;
        }
    }
}
