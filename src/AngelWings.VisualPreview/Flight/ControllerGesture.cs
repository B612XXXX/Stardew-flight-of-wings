namespace AngelWings.VisualPreview.Flight;

public enum WingInputAction { None, Toggle, Recover }

/// <summary>A tap toggles on release; a hold recovers once and consumes the tap.</summary>
public sealed class ControllerGesture
{
    public const double RecoveryHoldMs = 1200;
    private bool WasDown;
    private bool Armed;
    private double HeldMs;

    public WingInputAction Advance(bool down, bool eligible, double milliseconds)
    {
        if (!eligible)
        {
            Armed = false;
            HeldMs = 0;
            WasDown = down;
            return WingInputAction.None;
        }
        if (down && !WasDown) { Armed = true; HeldMs = 0; }
        WasDown = down;
        if (!Armed) return WingInputAction.None;
        if (!down)
        {
            Armed = false;
            HeldMs = 0;
            return WingInputAction.Toggle;
        }
        HeldMs += double.IsFinite(milliseconds) ? Math.Clamp(milliseconds, 0, 100) : 0;
        if (HeldMs < RecoveryHoldMs) return WingInputAction.None;
        Armed = false;
        return WingInputAction.Recover;
    }

    // Never treat a button held across a warp, reload or menu as a fresh press.
    public void Cancel() { WasDown = true; Armed = false; HeldMs = 0; }
}
