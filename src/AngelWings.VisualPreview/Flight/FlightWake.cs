using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace AngelWings.VisualPreview.Flight;

public enum WaterEffectKind { Lake, River, Ocean }

/// <summary>Local cosmetic water sprites at ground coordinates; never collision, map, or player coordinates.</summary>
public sealed class FlightWake
{
    public readonly record struct Wake(Vector2 Position, double AgeMs, WaterEffectKind Kind, float Strength=1);
    public readonly record struct SpriteSample(Vector2 Position, WaterEffectKind Kind, int Frame, float Opacity);
    public readonly record struct AnimationSpec(int Frames, double FrameMs, double EmitMs, float EmitDistance, float HalfWidth, float HalfHeight, float OriginY)
    {
        public double LifetimeMs => Frames * FrameMs;
    }

    private readonly List<Wake> wakes = new();
    private object? lastLocation;
    private Vector2 lastPosition;
    private double cooldown;
    private float distanceSinceWake;
    private Texture2D? oceanTexture;
    private Texture2D? riverTexture;
    private Texture2D? lakeTexture;
    public IReadOnlyList<Wake> Ripples => wakes;

    public static AnimationSpec Spec(WaterEffectKind kind) => kind switch
    {
        WaterEffectKind.Ocean => new(6, 100, 150, 18, 27, 11, 51),
        WaterEffectKind.River => new(5, 90, 180, 20, 21, 8, 48),
        _ => new(8, 90, 220, 24, 28, 10, 32)
    };

    public void ConfigureTextures(Texture2D ocean, Texture2D river, Texture2D lake)
    {
        Validate(ocean, WaterEffectKind.Ocean);
        Validate(river, WaterEffectKind.River);
        Validate(lake, WaterEffectKind.Lake);
        oceanTexture=ocean;riverTexture=river;lakeTexture=lake;
    }

    public void Reset() { wakes.Clear();lastLocation=null;cooldown=0;distanceSinceWake=0; }

    public void Advance(double milliseconds,object location,Vector2 groundPosition,bool canEmit,
        Func<Vector2,bool> openWater,Func<Vector2,WaterEffectKind>? classify=null,float height=36)
    {
        if (!double.IsFinite(milliseconds) || milliseconds<=0 || !float.IsFinite(groundPosition.X) || !float.IsFinite(groundPosition.Y))return;
        double dt=Math.Clamp(milliseconds,0,100);
        if (!ReferenceEquals(location,lastLocation) || Vector2.DistanceSquared(lastPosition,groundPosition)>128*128)
        { Reset();lastLocation=location;lastPosition=groundPosition;return; }
        for(int i=wakes.Count-1;i>=0;i--)
        {
            Wake wake=wakes[i] with {AgeMs=wakes[i].AgeMs+dt};
            if(wake.AgeMs>=Spec(wake.Kind).LifetimeMs)wakes.RemoveAt(i);else wakes[i]=wake;
        }
        cooldown=Math.Max(0,cooldown-dt);
        float moved=Vector2.Distance(lastPosition,groundPosition);
        lastPosition=groundPosition;
        if(!canEmit || moved<.25f || !openWater(groundPosition))
        { distanceSinceWake=0;return; }
        WaterEffectKind kind=classify?.Invoke(groundPosition) ?? WaterEffectKind.Lake;
        AnimationSpec spec=Spec(kind);
        if(!FitsOnWater(groundPosition,spec,openWater))
        { distanceSinceWake=0;return; }
        distanceSinceWake+=moved;
        if(cooldown>0 || distanceSinceWake<spec.EmitDistance)return;
        wakes.Add(new(groundPosition,0,kind,FlightVisuals.WakeStrength(height)));
        if(wakes.Count>8)wakes.RemoveAt(0);
        cooldown=spec.EmitMs;distanceSinceWake=0;
    }

    public IEnumerable<SpriteSample> Samples(Func<Vector2,bool> openWater)
    {
        foreach(Wake wake in wakes)
        {
            if(!openWater(wake.Position))continue;
            AnimationSpec spec=Spec(wake.Kind);
            int frame=Math.Min(spec.Frames-1,(int)(wake.AgeMs/spec.FrameMs));
            float tail=(float)Math.Clamp((spec.LifetimeMs-wake.AgeMs)/spec.FrameMs,0,1);
            float opacity=wake.Strength*(frame==spec.Frames-1?tail:1);
            if(opacity>0)yield return new(wake.Position,wake.Kind,frame,opacity);
        }
    }

    public void Draw(SpriteBatch batch,FlightMap surface)
    {
        if(oceanTexture==null || riverTexture==null || lakeTexture==null)return;
        foreach(SpriteSample sample in Samples(surface.IsOpenWaterAt))
        {
            AnimationSpec spec=Spec(sample.Kind);
            Texture2D texture=sample.Kind switch
            {
                WaterEffectKind.Ocean=>oceanTexture,
                WaterEffectKind.River=>riverTexture,
                _=>lakeTexture
            };
            float depth=FlightVisuals.RelativeLayer(sample.Position.Y/10000f,-.00005f,Game1.isUsingBackToFrontSorting);
            batch.Draw(texture,Game1.GlobalToLocal(Game1.viewport,sample.Position),new Rectangle(sample.Frame*64,0,64,64),
                Color.White*sample.Opacity,0,new Vector2(32,spec.OriginY),1,SpriteEffects.None,depth);
        }
    }

    private static bool FitsOnWater(Vector2 center,AnimationSpec spec,Func<Vector2,bool> openWater)
        => openWater(center-new Vector2(spec.HalfWidth,0)) && openWater(center+new Vector2(spec.HalfWidth,0))
        && openWater(center-new Vector2(0,spec.HalfHeight)) && openWater(center+new Vector2(0,spec.HalfHeight));

    private static void Validate(Texture2D texture,WaterEffectKind kind)
    {
        AnimationSpec spec=Spec(kind);
        if(texture.Width!=spec.Frames*64 || texture.Height!=64)
            throw new InvalidDataException($"Wrong {kind} water-effect dimensions: {texture.Width}x{texture.Height}");
    }
}
