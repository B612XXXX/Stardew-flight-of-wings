using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace AngelWings.VisualPreview.Flight;

/// <summary>Scoped draw-only pose. It never changes player textures, animation or collision state.</summary>
public static class FlightPoseDrawing
{
    public readonly record struct PoseContext(Vector2 TopLeft, float PixelScale, int Direction, float Amount)
    {
        public float Rotation => FlightVisuals.BodyLeanRadians(Direction) * Amount;
        public Vector2 Map(Vector2 position)
        {
            Vector2 p = (position - TopLeft) / PixelScale;
            if (Direction == 2) p.Y = FlightVisuals.FrontY(p.Y, Amount);
            else
            {
                Vector2 pivot = new(8, 18);
                p = Vector2.Transform(p - pivot, Matrix.CreateRotationZ(Rotation)) + pivot;
            }
            return TopLeft + p * PixelScale;
        }
    }

    [ThreadStatic] public static PoseContext? Current;

    private static readonly Type[] DrawTypes = [typeof(Texture2D), typeof(Vector2), typeof(Rectangle?),
        typeof(Color), typeof(float), typeof(Vector2), typeof(float), typeof(SpriteEffects), typeof(float)];

    public static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(FarmerRenderer), nameof(FarmerRenderer.drawHairAndAccesories)),
            transpiler: new HarmonyMethod(typeof(FlightPoseDrawing), nameof(RedirectSpriteDraws)));
    }

    public static IEnumerable<CodeInstruction> RedirectSpriteDraws(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        MethodInfo draw = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Draw), DrawTypes);
        int count = 0;
        foreach (CodeInstruction code in instructions)
        {
            if (code.Calls(draw))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(FlightPoseDrawing), nameof(Draw));
                count++;
            }
            yield return code;
        }
        if (count == 0) throw new MissingMethodException($"No expected sprite drawing calls in {original}.");
    }

    public static void Draw(SpriteBatch batch, Texture2D texture, Vector2 position, Rectangle? sourceRectangle,
        Color color, float rotation, Vector2 origin, float scale, SpriteEffects effects, float layerDepth)
    {
        if (Current is not { } pose || FarmerRenderer.isDrawingForUI)
        {
            batch.Draw(texture, position, sourceRectangle, color, rotation, origin, scale, effects, layerDepth);
            return;
        }
        if (pose.Direction != 2)
        {
            // Rotate every layer's attachment position about the same body pivot.
            batch.Draw(texture, pose.Map(position), sourceRectangle, color, rotation + pose.Rotation,
                origin, scale, effects, layerDepth);
            return;
        }
        Rectangle source = sourceRectangle ?? texture.Bounds;
        Vector2 topLeft = position - origin * scale;
        // One source-pixel strip at a time keeps shirt, hair, face and pants aligned.
        // Vector-scale overload is deliberately not transpiled (no recursion).
        for (int row = 0; row < source.Height; row++)
        {
            int sourceRow = effects.HasFlag(SpriteEffects.FlipVertically) ? source.Height - 1 - row : row;
            float y = (topLeft.Y + row * scale - pose.TopLeft.Y) / pose.PixelScale;
            float top = FlightVisuals.FrontY(y, pose.Amount);
            float bottom = FlightVisuals.FrontY(y + scale / pose.PixelScale, pose.Amount);
            batch.Draw(texture, new Vector2(topLeft.X, pose.TopLeft.Y + top * pose.PixelScale),
                new Rectangle(source.X, source.Y + sourceRow, source.Width, 1), color, 0f, Vector2.Zero,
                new Vector2(scale, (bottom - top) * pose.PixelScale), effects & SpriteEffects.FlipHorizontally, layerDepth);
        }
    }
}
