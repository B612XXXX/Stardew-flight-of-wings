using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace AngelWings.VisualPreview.Flight;

/// <summary>A short, ground-space confirmation, inspired by the game's placement markers.</summary>
public sealed class FlightLandingHint
{
    public Vector2 Position {get;private set;}
    public bool Safe {get;private set;}
    public double RemainingMs {get;private set;}
    public float Opacity => (float)Math.Min(1,RemainingMs/250)*.7f;
    public void Show(Vector2 position,bool safe) { Position=position;Safe=safe;RemainingMs=850; }
    public void Reset() => RemainingMs=0;
    public void Advance(double dt) { if(double.IsFinite(dt))RemainingMs=Math.Max(0,RemainingMs-Math.Clamp(dt,0,100)); }
    public void Draw(SpriteBatch batch)
    {
        if(RemainingMs<=0)return;
        Vector2 p=Game1.GlobalToLocal(Game1.viewport,Position);
        Color color=(Safe ? new Color(162,226,175) : new Color(238,153,133))*Opacity;
        float depth=FlightVisuals.RelativeLayer(Position.Y/10000f,-.00004f,Game1.isUsingBackToFrontSorting);
        foreach(int sx in new[]{-1,1})foreach(int sy in new[]{-1,1})
        {
            Vector2 corner=p+new Vector2(sx*22,sy*12);
            batch.Draw(Game1.staminaRect,corner-new Vector2(sx>0?8:0,0),new Rectangle(0,0,1,1),color,
                0,Vector2.Zero,new Vector2(10,2),SpriteEffects.None,depth);
            batch.Draw(Game1.staminaRect,corner-new Vector2(0,sy>0?4:0),new Rectangle(0,0,1,1),color,
                0,Vector2.Zero,new Vector2(2,6),SpriteEffects.None,depth);
        }
    }
}
