using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AngelWings.VisualPreview.Flight;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

static class PoseDrawingChecks
{
    record DrawRecord(float X,float Y,int SourceX,int SourceY,int Width,int Height,float ScaleX,float ScaleY,float Rotation,int Flip,float Depth,string Texture,uint Tint);
    static readonly List<DrawRecord> Calls = new();
    static int Count;
    static void Check(bool ok,string message) { if(!ok)throw new Exception(message);Count++; }
    static bool Capture(Texture2D texture,Color color,Vector2 position, Rectangle? sourceRectangle, Vector2 scale, float rotation, SpriteEffects effects,float layerDepth)
    {
        var r=sourceRectangle!.Value;
        Calls.Add(new(position.X,position.Y,r.X,r.Y,r.Width,r.Height,scale.X,scale.Y,rotation,(int)effects,layerDepth,texture?.Name??"fixture",color.PackedValue));
        return false;
    }
    static bool CaptureScalar(Texture2D texture,Color color,Vector2 position,Rectangle? sourceRectangle,float scale,float rotation,SpriteEffects effects,float layerDepth)
        => Capture(texture,color,position,sourceRectangle,new Vector2(scale),rotation,effects,layerDepth);

    public static int Run()
    {
        var harmony=new Harmony("AngelWingsProject.PoseDrawingChecks");
        foreach(Type scaleType in new[]{typeof(float),typeof(Vector2)})
        {
            var signature=new[]{typeof(Texture2D),typeof(Vector2),typeof(Rectangle?),typeof(Color),typeof(float),typeof(Vector2),scaleType,typeof(SpriteEffects),typeof(float)};
            harmony.Patch(AccessTools.Method(typeof(SpriteBatch),nameof(SpriteBatch.Draw),signature),
                prefix:new HarmonyMethod(typeof(PoseDrawingChecks),scaleType==typeof(float)?nameof(CaptureScalar):nameof(Capture)));
        }
        try { Exercise();ExerciseRenderer(harmony);ExerciseDoorFold();return Count; }
        finally { FlightPoseDrawing.Current=null;harmony.UnpatchAll(harmony.Id); }
    }

    static bool Skip() => false;
    static void ExerciseDoorFold()
    {
        var batch=(SpriteBatch)RuntimeHelpers.GetUninitializedObject(typeof(SpriteBatch));
        var texture=DummyTexture("wing",1280);
        var foldedTexture=DummyTexture("foldedWing",800);
        var fold=new FlightDoorPassage();
        var frames=new List<object>();
        foreach(int hz in new[]{30,60,120})
        {
            fold.Reset();
            for(int i=0;i<hz;i++)fold.Advance(1000d/hz,false,false,false);
            Check(fold.Amount==0,"Uncommitted proximity/idle time must never fold wings");
            fold.ConfirmWarp();
            for(int i=0;i<hz;i++)fold.Advance(1000d/hz,false,true,false);
            Check(fold.Amount==1,"Confirmed warp reaches the narrowest pose at every frame rate");
            fold.Arrive();
            for(int i=0;i<hz;i++)fold.Advance(1000d/hz,false,false,false);
            Check(fold.Amount==0,"Arrival unfolds fully at every frame rate");
        }
        foreach(int direction in new[]{0,1,2,3})
        {
            fold.Reset();
            for(int tick=0;tick<50;tick++)
            {
                if(tick==1)fold.ConfirmWarp();
                if(tick==15)fold.Arrive();
                fold.Advance(16,false,tick is >0 and <15,false);
                Calls.Clear();
                fold.Draw(batch,texture,new Vector2(160,160),new Rectangle(0,0,80,80),Color.White,0,
                    new Vector2(40,40),4,.5f,direction,foldedTexture);
                Check(Calls.All(c=>c.ScaleX>0 && c.ScaleY==4 && c.Depth==.5f),"Door fold preserves feather height and native draw layer");
                if(fold.Amount>0)
                {
                    var root=Calls[40];
                    Check(root.X==160 && root.Y==160,"Folding must keep the shoulder root fixed");
                    Check(Calls[0].Y>=160 && Calls[^1].Y>=160,"Outer feathers must sweep down behind the shoulder");
                }
                frames.Add(new {direction,tick,fold.Amount,Draws=Calls.ToArray()});
            }
            Check(fold.Amount==0,"Wing rendering transition finishes fully restored");
        }
        fold.ConfirmWarp();fold.Advance(100,true,true,false);float amount=fold.Amount;
        fold.Advance(100,false,false,true);
        Check(fold.Amount==amount,"Pausing must freeze the fold animation");
        for(int i=0;i<60;i++)fold.Advance(16,false,false,false);
        Check(fold.Amount==0,"Cancelled warp must not leave wings permanently folded");
        fold.ConfirmWarp();fold.Advance(100,false,true,false);fold.Reset();
        Check(fold.Amount==0,"Recovery/title reset must clear the passage pose");
        Directory.CreateDirectory("build/qa-0.3.5");
        File.WriteAllText("build/qa-0.3.5/door-fold-draws.json",JsonSerializer.Serialize(frames));
        Console.WriteLine("PASS: four-direction door-fold draw submissions, fixed shoulder roots, rearward feather sweep, frame rates, pause/cancel/reset and unfold.");
    }
    static bool FixtureShirtColor(ref Color __result) { __result=Color.White;return false; }
    static bool FixturePantsColor(ref Color __result) { __result=new Color(38,159,170);return false; }
    static Texture2D DummyTexture(string name,int width)
    {
        var texture=(Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
        texture.Name=name;
        var field=typeof(Texture2D).GetFields(BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance)
            .First(f=>f.FieldType==typeof(int)&&f.Name.Contains("width",StringComparison.OrdinalIgnoreCase));
        field.SetValue(texture,width);
        return texture;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ExerciseRenderer(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Farmer),"farmerInit"),prefix:new HarmonyMethod(typeof(PoseDrawingChecks),nameof(Skip)));
        harmony.Patch(AccessTools.Method(typeof(StardewValley.BellsAndWhistles.PlayerStatusList),"AddSpriteDefinition"),prefix:new HarmonyMethod(typeof(PoseDrawingChecks),nameof(Skip)));
        harmony.Patch(AccessTools.Method(typeof(AnimatedSprite),"LoadTexture"),prefix:new HarmonyMethod(typeof(PoseDrawingChecks),nameof(Skip)));
        harmony.Patch(AccessTools.Method(typeof(Farmer),nameof(Farmer.GetShirtColor)),prefix:new HarmonyMethod(typeof(PoseDrawingChecks),nameof(FixtureShirtColor)));
        harmony.Patch(AccessTools.Method(typeof(Farmer),nameof(Farmer.GetPantsColor)),prefix:new HarmonyMethod(typeof(PoseDrawingChecks),nameof(FixturePantsColor)));
        harmony.Patch(AccessTools.Method(typeof(FarmerRenderer),"executeRecolorActions"),prefix:new HarmonyMethod(typeof(PoseDrawingChecks),nameof(Skip)));
        FlightPoseDrawing.Install(harmony);
        var parameters=new[]{typeof(SpriteBatch),typeof(FarmerSprite.AnimationFrame),typeof(int),typeof(Rectangle),typeof(Vector2),typeof(Vector2),typeof(float),typeof(int),typeof(Color),typeof(float),typeof(float),typeof(Farmer)};
        harmony.Patch(AccessTools.Method(typeof(FarmerRenderer),"draw",parameters),
            transpiler:new HarmonyMethod(typeof(FlightPoseDrawing),nameof(FlightPoseDrawing.RedirectSpriteDraws)));
        InvokeRenderer();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void InvokeRenderer()
    {
        var batch=(SpriteBatch)RuntimeHelpers.GetUninitializedObject(typeof(SpriteBatch));
        var farmer=new Farmer();
        farmer.hair.Value=16;
        farmer.hairstyleColor.Value=new Color(155,46,33);
        var renderer=(FarmerRenderer)RuntimeHelpers.GetUninitializedObject(typeof(FarmerRenderer));
        foreach(string name in new[]{"skin","heightOffset"})
        {
            var field=AccessTools.Field(typeof(FarmerRenderer),name);
            field.SetValue(renderer,Activator.CreateInstance(field.FieldType));
        }
        AccessTools.Field(typeof(FarmerRenderer),"baseTexture").SetValue(renderer,DummyTexture("farmer_base",288));
        FarmerRenderer.shirtsTexture=DummyTexture("shirts",256);
        FarmerRenderer.pantsTexture=DummyTexture("pants",1920);
        FarmerRenderer.hairStylesTexture=DummyTexture("hairstyles",128);
        FarmerRenderer.accessoriesTexture=DummyTexture("accessories",128);
        Farmer.hairStyleMetadataFile=new();
        var recordings=new Dictionary<string,DrawRecord[]>();
        DrawRecord[] RenderPose(int direction,float amount)
        {
            farmer.FacingDirection=direction;
            int frame=FlightVisuals.IdleFrame(direction);
            Calls.Clear();FlightPoseDrawing.Current=new(new Vector2(100,100),4,direction,amount);
            renderer.draw(batch,new FarmerSprite.AnimationFrame(frame,100,false,direction==3),frame,
                new Rectangle(frame%6*16,frame/6*32,16,32),new Vector2(100,100),Vector2.Zero,.5f,direction,Color.White,0,1,farmer);
            Check(Calls.Any(c=>c.Texture=="farmer_base")&&Calls.Any(c=>c.Texture=="shirts")&&Calls.Any(c=>c.Texture=="pants")&&Calls.Any(c=>c.Texture=="hairstyles"),
                "Actual FarmerRenderer must draw all stock layers through the pose helper");
            if(direction==2) Check(Calls.All(c=>c.Rotation==0),"Actual front renderer must never roll sideways");
            if(amount==0) Check(Calls.All(c=>c.Rotation==0),"Stationary flight must remain upright through the actual renderer");
            return Calls.ToArray();
        }
        foreach(var (view,direction,amount) in new[]{("ground",2,0f),("front-idle",2,0f),("front",2,1f),("back",0,0f),("right-idle",1,0f),("right",1,1f),("left-idle",3,0f),("left",3,1f)})
            recordings[view]=RenderPose(direction,amount);
        File.WriteAllText("build/qa-0.2.8/actual-renderer-draws.json",JsonSerializer.Serialize(recordings,new JsonSerializerOptions{WriteIndented=true}));
        var flight=new FlightController();flight.Toggle(true,false);
        for(int i=0;i<70;i++)flight.Advance(10,true,false,false);
        var transitions=new List<object>();
        var wake=new FlightWake();var wakeLocation=new object();var ground=new Vector2(160,252);
        for(int i=0;i<120;i++)
        {
            bool moving=i>=20 && i<60;
            flight.Advance(60,true,false,moving);
            if(moving)ground.X+=9;
            wake.Advance(60,wakeLocation,ground,flight.CanTravel,_=>true,_=>WaterEffectKind.Lake);
            transitions.Add(new { Seconds=flight.VisualSeconds,flight.Frame,flight.Cycle,flight.PoseAmount,
                Bob=FlightVisuals.HoverBob(flight.VisualSeconds,flight.MovementLean),
                WaterSprites=wake.Samples(_=>true).Select(p=>new {X=p.Position.X-ground.X+160,Y=p.Position.Y,p.Kind,p.Frame,p.Opacity}).ToArray(),
                Front=RenderPose(2,flight.PoseAmount),Right=RenderPose(1,flight.PoseAmount),Back=RenderPose(0,flight.PoseAmount) });
        }
        File.WriteAllText("build/qa-0.2.8/motion-transition-draws.json",JsonSerializer.Serialize(transitions));
        Console.WriteLine("PASS: actual FarmerRenderer with idle/moving views and 120 start-stop transition frames; GPU submission/recolour mocked; no save loaded.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Exercise()
    {
        // The GPU submission boundary is intercepted; the real runtime draw helper runs.
        var batch=(SpriteBatch)RuntimeHelpers.GetUninitializedObject(typeof(SpriteBatch));
        var topLeft=new Vector2(100,100);
        var source=new Rectangle(0,0,16,32);
        var poses=new Dictionary<string,DrawRecord[]>();
        foreach(var (name,direction,amount) in new[]{("ground",2,0f),("front",2,1f),("right",1,1f),("left",3,1f)})
        {
            Calls.Clear();
            FlightPoseDrawing.Current = new(topLeft,4,direction,amount);
            FlightPoseDrawing.Draw(batch,null!,topLeft,source,Color.White,0,Vector2.Zero,4,SpriteEffects.None,.5f);
            poses[name]=Calls.ToArray();
        }
        var front=poses["front"];
        Check(front.Length==32 && front.All(c=>c.X==100 && c.Rotation==0),"Front flight must keep its centreline upright");
        Check(front[5].Y > poses["ground"][5].Y,"Head must advance toward the viewer");
        Check(front[18].Y > poses["ground"][18].Y,"Torso must lower independently");
        Check(front[31].Y+front[31].ScaleY < 100+32*4-8,"Legs must retract by more than two native pixels");
        Check(front.Zip(front.Skip(1),(a,b)=>Math.Abs(a.Y+a.ScaleY-b.Y)<1e-4).All(x=>x),"Front strips must have no seams or overlapping gaps");
        Calls.Clear();
        FlightPoseDrawing.Current=new(topLeft,4,2,1);
        FlightPoseDrawing.Draw(batch,null!,topLeft+new Vector2(16,56),new Rectangle(0,0,8,8),Color.White,0,Vector2.Zero,4,SpriteEffects.None,.500006f);
        Check(Math.Abs(Calls[0].Y-front[14].Y)<1e-4 && Math.Abs(Calls[7].Y-front[21].Y)<1e-4,"Shirt must follow the same body warp at both seams");
        foreach(int direction in new[]{1,3})
        {
            var pose=new FlightPoseDrawing.PoseContext(topLeft,4,direction,1);
            Vector2 head=pose.Map(topLeft+new Vector2(8,5)*4);
            Vector2 hips=pose.Map(topLeft+new Vector2(8,18)*4);
            Check(direction==1 ? head.X>hips.X : head.X<hips.X,"Side head must lean in the travel direction");
            Vector2 sleeve=topLeft+new Vector2(16,56);
            Calls.Clear();FlightPoseDrawing.Current=pose;
            FlightPoseDrawing.Draw(batch,null!,sleeve,new Rectangle(0,0,8,8),Color.White,0,Vector2.Zero,4,SpriteEffects.None,.5f);
            Check(Vector2.Distance(new(Calls[0].X,Calls[0].Y),pose.Map(sleeve))<1e-4,"Clothes must rotate around the same body pivot");
        }
        foreach(bool reverse in new[]{false,true})
        {
            bool old=Game1.isUsingBackToFrontSorting;
            try
            {
                Game1.isUsingBackToFrontSorting=reverse;
                float body=FarmerRenderer.GetLayerDepth(.5f,FarmerRenderer.FarmerSpriteLayers.Base);
                float shirt=FarmerRenderer.GetLayerDepth(.5f,FarmerRenderer.FarmerSpriteLayers.Shirt);
                float hair=FarmerRenderer.GetLayerDepth(.5f,FarmerRenderer.FarmerSpriteLayers.Hair);
                float back=FlightVisuals.RelativeLayer(body,-.0000005f,reverse);
                float root=FlightVisuals.RelativeLayer(shirt,.0000005f,reverse);
                Check(reverse ? back>body&&root<shirt&&root>hair : back<body&&root>shirt&&root<hair,
                    "Main wing behind body; root above shirt and below hair in both sort orders");
                foreach(int direction in new[]{0,1,2,3})
                {
                    Vector2 expected=new(direction switch{1=>38f,3=>42f,_=>40f},42f);
                    Check(FlightVisuals.WingSourceOrigin(true,direction)==expected,
                        "Butterfly source hinge must land exactly on the runtime shoulder, without left/Y drift");
                    Check(FlightVisuals.WingSourceOrigin(false,direction)==new Vector2(direction switch{1=>38f,3=>41f,_=>39.5f},40f),
                        "Existing angel attachment origin must not change");
                    var selected=FlightVisuals.WingForegroundLayer(true,direction,false);
                    Check(selected==(direction is 1 or 3 ? FarmerRenderer.FarmerSpriteLayers.Arms : FarmerRenderer.FarmerSpriteLayers.Shirt),
                        "Camera-near side butterfly wing must render above the arm, not disappear underneath it");
                    Check(FlightVisuals.WingForegroundLayer(true,direction,true)==selected &&
                          FlightVisuals.WingForegroundLayer(false,direction,false)==FarmerRenderer.FarmerSpriteLayers.Shirt,
                        "Near butterfly membrane stays opaque over all body layers even in tool poses; angel ordering unchanged");
                }
                float near=FlightVisuals.RelativeLayer(FarmerRenderer.GetLayerDepth(.5f,FlightVisuals.WingForegroundLayer(true,1,false)),.0000005f,reverse);
                float arm=FarmerRenderer.GetLayerDepth(.5f,FarmerRenderer.FarmerSpriteLayers.Arms);
                float tool=FarmerRenderer.GetLayerDepth(.5f,FarmerRenderer.FarmerSpriteLayers.ToolDown);
                Check(reverse ? near<arm&&near>tool : near>arm&&near<tool,
                    "Side membrane is above the near arm but below ToolDown in both world sort orders");
            }
            finally{Game1.isUsingBackToFrontSorting=old;}
        }
        Calls.Clear();FlightPoseDrawing.Current=null;
        FlightPoseDrawing.Draw(batch,null!,topLeft,source,Color.White,.1f,Vector2.Zero,4,SpriteEffects.None,.5f);
        Check(Calls.Count==1&&Calls[0].X==100&&Calls[0].Y==100&&Calls[0].Rotation==.1f,"Outside scope the original draw call is untouched");
        string output=Path.GetFullPath("build/qa-0.2.8");Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,"runtime-pose-draws.json"),JsonSerializer.Serialize(poses,new JsonSerializerOptions{WriteIndented=true}));
        var wingLayout=new Dictionary<string,object>();
        foreach(var (view,direction) in new[]{("back",0),("right",1),("front",2),("left",3)})
        {
            Vector2 origin=FlightVisuals.WingSourceOrigin(true,direction);
            wingLayout[view]=new {butterfly_origin=new[]{origin.X,origin.Y},
                foreground_depth=FlightVisuals.RelativeLayer(FarmerRenderer.GetLayerDepth(.5f,FlightVisuals.WingForegroundLayer(true,direction,false)),.0000005f,false),
                tool_foreground_depth=FlightVisuals.RelativeLayer(FarmerRenderer.GetLayerDepth(.5f,FlightVisuals.WingForegroundLayer(true,direction,true)),.0000005f,false)};
        }
        File.WriteAllText(Path.Combine(output,"runtime-wing-layout.json"),JsonSerializer.Serialize(wingLayout,new JsonSerializerOptions{WriteIndented=true}));
        return Count;
    }
}
