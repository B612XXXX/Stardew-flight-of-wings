using StardewModdingAPI;

namespace AngelWings.VisualPreview;

public sealed class ModConfig
{
    public SButton ToggleKey { get; set; } = SButton.F8;
    public SButton EmergencyLandKey { get; set; } = SButton.F10;
    public SButton ControllerToggleKey { get; set; } = SButton.LeftStick;
    public float WingScale { get; set; } = 1f;
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }
    public float HoverHeight { get; set; } = 36f;
    public float FlightSpeedMultiplier { get; set; } = 1.65f;
    public bool AutoClimb { get; set; } = true;
    public float ObstacleLift { get; set; } = 88f;
    public bool ShowLandingHint { get; set; } = true;
}
