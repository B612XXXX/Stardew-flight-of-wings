using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace AngelWings.VisualPreview.Flight;

/// <summary>Visual-only folding for a committed warp or movement into an open door.</summary>
public sealed class FlightDoorPassage
{
    public float Amount { get; private set; }
    private bool Warping;
    private double HoldMs;

    public void ConfirmWarp() { Warping = true; HoldMs = 220; }
    public void Arrive() { if (Warping) { Warping = false; HoldMs = 220; } }
    public void Reset() { Amount = 0; HoldMs = 0; Warping = false; }

    public static bool CrossingOpenDoor(Farmer who)
    {
        if (!who.CanMove || who.UsingTool || who.isEating || who.controller != null || who.movementDirections.Count == 0)
            return false;
        Rectangle body = who.GetBoundingBox();
        foreach (InteriorDoor door in who.currentLocation.interiorDoors.Doors)
        {
            if (!door.Value) continue;
            Point p = door.Position;
            var back = who.currentLocation.Map.GetLayer("Back")?.Tiles[p.X,p.Y];
            var building = who.currentLocation.Map.GetLayer("Buildings")?.Tiles[p.X,p.Y];
            if (back == null || FlightMap.Barrier(back) || FlightMap.Barrier(building)
                || (building != null && !FlightMap.BuildingPassable(building))) continue;
            Rectangle threshold = new(p.X*64,p.Y*64,64,64);
            foreach (int direction in who.movementDirections)
            {
                if (direction is < 0 or > 3) continue;
                Rectangle next = who.nextPosition(direction);
                // A perpendicular step must actually enter/cross the threshold;
                // distance, facing and parallel movement are not commitments.
                if (direction is 0 or 2 && next.Intersects(threshold)
                    && (body.Intersects(threshold) || (direction == 0 ? body.Top >= threshold.Bottom : body.Bottom <= threshold.Top)))
                {
                    using var native = FlightCollision.OriginalRules();
                    if (!who.currentLocation.isCollidingPosition(next,Game1.viewport,true,0,false,who,false,false,false,true))
                        return true;
                }
            }
        }
        return false;
    }

    public void Advance(double milliseconds, bool crossing, bool warpPending, bool paused)
    {
        if (paused) return;
        double dt = double.IsFinite(milliseconds) ? Math.Clamp(milliseconds,0,100) : 0;
        if (Warping && !warpPending) Warping = false; // cancelled/failed warp cannot leave wings folded
        if (crossing) HoldMs = 160;
        bool fold = Warping || crossing || HoldMs > 0;
        if (!Warping && !crossing) HoldMs = Math.Max(0,HoldMs-dt);
        float target = fold ? 1 : 0;
        float step = (float)(dt / (fold ? 120 : 280));
        Amount = Math.Clamp(Amount + Math.Clamp(target-Amount,-step,step),0,1);
    }

    // The shoulder origin stays fixed while the same feather texture narrows
    // and its outer feathers sweep down along the back. This is a draw-only
    // deformation: animation phase, collision and movement speed remain native.
    public void Draw(SpriteBatch batch, Texture2D texture, Vector2 anchor, Rectangle source, Color color,
        float rotation, Vector2 origin, float scale, float depth, int direction, Texture2D? foldedTexture = null)
    {
        float fold = FlightController.Ease(Amount);
        if (fold <= 0)
        {
            batch.Draw(texture,anchor,source,color,rotation,origin,scale,SpriteEffects.None,depth);
            return;
        }
        // Blend into the existing takeoff animation's closed-wing endpoint,
        // so a high flap cannot become two upright spikes when squeezed shut.
        if (foldedTexture != null)
        {
            if (fold < 1) DrawShape(batch,texture,anchor,source,color*(1-fold),rotation,origin,scale,depth,direction,fold);
            DrawShape(batch,foldedTexture,anchor,new Rectangle(0,0,80,80),color*fold,rotation,origin,scale,depth,direction,fold);
        }
        else DrawShape(batch,texture,anchor,source,color,rotation,origin,scale,depth,direction,fold);
    }

    private static void DrawShape(SpriteBatch batch, Texture2D texture, Vector2 anchor, Rectangle source, Color color,
        float rotation, Vector2 origin, float scale, float depth, int direction, float fold)
    {
        float width = 1 - .60f*fold;
        // Column strips retain feather detail instead of replacing the approved
        // art. Side views trail behind the shoulder; front/back stay symmetric.
        for (int x=0;x<source.Width;x++)
        {
            float dx=x-origin.X;
            float trail=Math.Abs(dx)*fold;
            float side=direction switch {1=>-.18f,3=>.18f,_=>0};
            float sideways=side*trail;
            float columnWidth=width+side*(Math.Abs(dx+1)-Math.Abs(dx))*fold;
            Vector2 offset=new(dx*width+sideways,.62f*trail);
            Vector2 position=anchor+Vector2.Transform(offset*scale,Matrix.CreateRotationZ(rotation));
            batch.Draw(texture,position,new Rectangle(source.X+x,source.Y,1,source.Height),color,
                rotation,new Vector2(0,origin.Y),new Vector2(scale*columnWidth,scale),SpriteEffects.None,depth);
        }
    }
}
