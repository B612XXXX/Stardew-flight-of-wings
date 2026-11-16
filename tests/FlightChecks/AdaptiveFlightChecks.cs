using AngelWings.VisualPreview;
using AngelWings.VisualPreview.Flight;
using Microsoft.Xna.Framework;
using System.Text.Json;

static class AdaptiveFlightChecks
{
    public static int Run()
    {
        int count=0;
        void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
        Check(Math.Abs(new ModConfig().FlightSpeedMultiplier-1.65f)<.0001,"Default flight speed multiplier must be 1.65");
        Check(new ModConfig().AutoClimb && new ModConfig().ObstacleLift==88 && new ModConfig().ShowLandingHint,"New options must work when upgrading a config that lacks these fields");
        var frames=new List<object>();
        foreach(float speed in new[]{648f,972f})
        foreach(double dt in new[]{1000d/120,1000d/60,1000d/30,100d})
        {
            var altitude=new FlightAltitude();float x=-100;
            float previous=0;bool crossed=false;
            for(double t=0;t<5000;t+=dt)
            {
                var box=new Rectangle((int)x,320,48,32);
                bool needs=FlightAltitude.NeedsClearance(box,new Vector2(speed,0),(tx,ty)=>ty==5&&(tx==8||tx==10));
                altitude.Advance(dt,needs,88,true);
                Check(altitude.ExtraHeight>=0&&altitude.ExtraHeight<=88.01,"Adaptive height must not overshoot");
                Check(Math.Abs(altitude.ExtraHeight-previous)<=dt*.40+.1,"Climb or descent snapped");
                if(box.Right>=512 && box.Left<704)
                {
                    crossed=true;
                    Check(altitude.ExtraHeight>80,"Clearance must be reached before crossing a fence, including speed buffs");
                }
                if(dt==1000d/60 && speed==648)frames.Add(new {Milliseconds=t,X=x,Clearance=needs,Extra=altitude.ExtraHeight,Lift=36+altitude.ExtraHeight,
                    ShadowScale=FlightVisuals.ShadowScale(36+altitude.ExtraHeight),ShadowOpacity=FlightVisuals.ShadowOpacity(36+altitude.ExtraHeight)});
                previous=altitude.ExtraHeight;x+=speed*(float)dt/1000;
            }
            Check(crossed&&altitude.ExtraHeight<.1,"After leaving the fence the player must return to cruise height");
        }
        var f=new FlightAltitude();
        for(int i=0;i<120;i++)f.Advance(16,true,88,true);
        float high=f.ExtraHeight;
        for(int i=0;i<20;i++)f.Advance(16,false,88,true);
        Check(f.ExtraHeight>=high-.1,"Short gaps in a row of fences must not cause bobbing");
        f.OnWarp();Check(Math.Abs(f.ExtraHeight-high)<.1,"Warp must not snap extra height");
        f.Advance(double.NaN,false,float.NaN,true);Check(float.IsFinite(f.ExtraHeight),"Bad parameters must not poison height");
        f.Advance(16,false,88,false);Check(f.ExtraHeight==0,"Landing must remove adaptive height");
        var stationary=new Rectangle(512+8,320+16,48,32);
        Check(FlightAltitude.NeedsClearance(stationary,Vector2.Zero,(x,y)=>x==8&&y==5),"Stopped above a fence must remain elevated");
        Check(!FlightAltitude.NeedsClearance(new Rectangle(128,320,48,32),new Vector2(-648,0),(x,y)=>x==8&&y==5),"Must not climb for obstacles behind movement");
        Check(!FlightAltitude.NeedsClearance(new Rectangle(128,320,48,32),Vector2.Zero,(x,y)=>x==8&&y==5),"No remote prediction while idle");
        foreach(float height in new[]{0f,36f,80f,124f,200f,float.NaN})
            Check(float.IsFinite(FlightVisuals.ShadowScale(height))&&FlightVisuals.ShadowScale(height)>0
                &&FlightVisuals.ShadowOpacity(height)>0&&FlightVisuals.ShadowOpacity(height)<=1,"Shadow feedback must remain bounded");
        Check(FlightVisuals.ShadowScale(124)<FlightVisuals.ShadowScale(36)&&FlightVisuals.ShadowOpacity(124)<FlightVisuals.ShadowOpacity(36),"Higher flight needs a smaller lighter shadow");
        Check(FlightVisuals.WakeStrength(124)<FlightVisuals.WakeStrength(36),"Higher flight should disturb less water");
        var hint=new FlightLandingHint();hint.Show(new Vector2(40,60),false);
        hint.Advance(100);Check(!hint.Safe&&hint.Position==new Vector2(40,60)&&hint.Opacity>0,"Rejected landing marker must stay at the requested ground position");
        for(int i=0;i<10;i++)hint.Advance(100);
        Check(hint.Opacity==0,"Landing marker must fade away without lingering UI clutter");
        hint.Show(Vector2.Zero,true);hint.Reset();Check(hint.Opacity==0,"Warp/reset must clear landing markers");
        Directory.CreateDirectory("build/qa-0.2.8");
        File.WriteAllText("build/qa-0.2.8/adaptive-clearance.json",JsonSerializer.Serialize(frames));
        Console.WriteLine("PASS: adaptive climb at 10/30/60/120 Hz, speed buffs, chained fences, stop/warp/landing, shadow/water/landing feedback.");
        return count;
    }
}
