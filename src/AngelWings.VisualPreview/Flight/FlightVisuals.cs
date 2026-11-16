using Microsoft.Xna.Framework;
using StardewValley;

namespace AngelWings.VisualPreview.Flight;

/// <summary>Small, deterministic visual rules shared by the runtime and headless checks.</summary>
public static class FlightVisuals
{
    public static float ShadowScale(float lift) => 4f*(1-.32f*HeightFraction(lift));
    public static float ShadowOpacity(float lift) => 1-.42f*HeightFraction(lift);
    public static float WakeStrength(float lift) => 1-.2f*HeightFraction(Math.Max(0,lift-36));
    private static float HeightFraction(float lift) => float.IsFinite(lift) ? Math.Clamp(lift/124f,0,1) : 0;
    private const float SideLean = MathF.PI / 12f;

    public static int IdleFrame(int direction) => direction switch
    {
        0 => 12, // actual sprite frame, not the animation index accepted by setCurrentFrame
        1 or 3 => 6,
        _ => 0
    };

    public static float BodyLeanRadians(int direction) => direction switch
    {
        1 => SideLean,
        3 => -SideLean,
        _ => 0f
    };

    // Continuous in character space: head advances 1.5 pixels toward the viewer,
    // torso lowers, and the last ten leg pixels foreshorten to 6.5 pixels.
    // Every body/clothing/hair layer uses this same mapping, including at seams.
    public static float FrontY(float y, float amount = 1f)
    {
        float target = y <= 14 ? y + 1.5f
            : y <= 22 ? 15.5f + (y - 14) * .875f
            : 22.5f + (y - 22) * .65f;
        return y + (target - y) * Math.Clamp(amount, 0, 1);
    }

    public static float RelativeLayer(float reference, float offset, bool backToFront)
        => reference + offset * (backToFront ? -1 : 1);

    public static Vector2 WingSourceOrigin(bool butterfly, int direction)
        => butterfly ? new(direction switch { 1 => 38f, 3 => 42f, _ => 40f }, 42f)
                     : new(direction switch { 1 => 38f, 3 => 41f, _ => 39.5f }, 40f);

    public static FarmerRenderer.FarmerSpriteLayers WingForegroundLayer(bool butterfly, int direction, bool toolBusy)
        => butterfly && direction is 1 or 3
            ? FarmerRenderer.FarmerSpriteLayers.Arms : FarmerRenderer.FarmerSpriteLayers.Shirt;

    public static float HoverBob(double seconds, bool stationary)
        => HoverBob(seconds, stationary ? 0f : 1f);

    public static float HoverBob(double seconds, float movementLean)
    {
        if (!double.IsFinite(seconds) || !float.IsFinite(movementLean)) return 0f;
        // Independent continuous time: wrapping the wing phase caused a bob jump.
        double wave = Math.Sin(seconds / 4.8 * Math.PI * 2);
        // Idle peaks at five world pixels; movement eases toward the restrained
        // 0.35px motion without changing phase or snapping on start/stop.
        return (float)(wave * (5 - 4.65 * Math.Clamp(movementLean, 0, 1)));
    }
}
