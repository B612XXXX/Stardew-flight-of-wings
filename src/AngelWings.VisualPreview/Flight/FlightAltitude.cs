using Microsoft.Xna.Framework;

namespace AngelWings.VisualPreview.Flight;

/// <summary>Read-ahead clearance and a continuous, frame-rate-independent height spring.</summary>
public sealed class FlightAltitude
{
    private double height, velocity, holdMs;
    public float ExtraHeight => (float)Math.Max(0,height);
    public void Reset() { height=velocity=holdMs=0; }
    public void OnWarp() => holdMs=0; // retain visual height; stop predicting the previous room

    public void Advance(double milliseconds,bool clearance,float lift,bool active)
    {
        if(!active) { Reset();return; }
        if(!double.IsFinite(milliseconds) || !float.IsFinite(lift))return;
        double dt=Math.Clamp(milliseconds,0,100);
        if(clearance)holdMs=500;else holdMs=Math.Max(0,holdMs-dt);
        double target=clearance || holdMs>0 ? Math.Clamp(lift,0,128) : 0;
        double omega=target>height ? 12 : 4.5; // climb early; descend more slowly
        double seconds=dt/1000,error=height-target,impulse=velocity+omega*error,decay=Math.Exp(-omega*seconds);
        height=target+(error+impulse*seconds)*decay;
        velocity=(velocity-omega*impulse*seconds)*decay;
        if(height<0) { height=0;velocity=0; }
        if(Math.Abs(height-target)<.01 && Math.Abs(velocity)<.05) { height=target;velocity=0; }
    }

    public static bool NeedsClearance(Rectangle body,Vector2 velocityPerSecond,Func<int,int,bool> lowObstacle)
    {
        if(!float.IsFinite(velocityPerSecond.X)||!float.IsFinite(velocityPerSecond.Y))velocityPerSecond=Vector2.Zero;
        float speed=velocityPerSecond.Length();
        Vector2 direction=speed>1 ? velocityPerSecond/speed : Vector2.Zero;
        float distance=speed>1 ? Math.Clamp(speed*.60f,112,512) : 0;
        int steps=(int)MathF.Ceiling(distance/24);
        for(int step=0;step<=steps;step++)
        {
            var probe=body;
            Vector2 offset=steps==0 ? Vector2.Zero : direction*(distance*step/steps);
            probe.Offset((int)offset.X,(int)offset.Y);
            probe.Inflate(12,28); // include boots and a small margin; not the full wing span
            int left=(int)MathF.Floor(probe.Left/64f),top=(int)MathF.Floor(probe.Top/64f);
            int right=(int)MathF.Floor((probe.Right-1)/64f),bottom=(int)MathF.Floor((probe.Bottom-1)/64f);
            for(int y=top;y<=bottom;y++)for(int x=left;x<=right;x++)
                if(lowObstacle(x,y))return true;
        }
        return false;
    }
}
