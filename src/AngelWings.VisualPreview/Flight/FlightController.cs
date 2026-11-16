namespace AngelWings.VisualPreview.Flight;

public enum FlightStage { Grounded, TakingOff, Flying, Landing }
public enum ToggleResult { Started, Queued, Cancelled, CannotLand }

/// <summary>Time-driven flight state; no game globals or persistent speed mutations.</summary>
public sealed class FlightController
{
    public FlightStage Stage { get; private set; }
    public bool DesiredFlight { get; private set; }
    public bool LandingQueued { get; private set; }
    public double Phase { get; private set; }
    public double VisualSeconds { get; private set; }
    public double TransitionMs { get; private set; }
    public float Height { get; private set; }
    public float Opacity { get; private set; }
    private double leanPosition, leanVelocity;
    public float MovementLean => (float)Math.Clamp(leanPosition, 0, 1);
    public float PoseAmount => Height * MovementLean;
    public string Cycle { get; private set; } = "H";
    public bool Active => Stage != FlightStage.Grounded;
    public bool CanTravel => Stage == FlightStage.Flying && !LandingQueued;
    public string Animation => Stage switch { FlightStage.TakingOff => "T", FlightStage.Landing => "L", _ => Cycle };
    public int Frame => Stage is FlightStage.TakingOff or FlightStage.Landing
        ? Math.Clamp((int)Math.Round(TransitionMs / 700 * 9), 0, 9)
        : Math.Clamp((int)(Phase * 16), 0, 15);

    public float MovementSpeed(float nativeSpeed, float multiplier, bool fishing) => CanTravel && !fishing
        && float.IsFinite(nativeSpeed) && nativeSpeed > 0 && float.IsFinite(multiplier)
        ? nativeSpeed * Math.Clamp(multiplier, 1, 2) : nativeSpeed;

    public ToggleResult Toggle(bool canLand, bool fishing)
    {
        if (Stage == FlightStage.Grounded)
        {
            DesiredFlight = true;
            StartTakeoff();
            return ToggleResult.Started;
        }
        if (Stage is FlightStage.TakingOff or FlightStage.Landing)
        {
            DesiredFlight = !DesiredFlight;
            return DesiredFlight ? ToggleResult.Cancelled : ToggleResult.Queued;
        }
        if (LandingQueued)
        {
            LandingQueued = false;
            DesiredFlight = true;
            return ToggleResult.Cancelled;
        }
        if (!canLand && !fishing) return ToggleResult.CannotLand;
        DesiredFlight = false;
        LandingQueued = true;
        return ToggleResult.Queued;
    }

    // An input held across several ticks must request landing without toggling it off.
    public bool RequestLanding(bool canLand, bool fishing)
    {
        if (!Active || (!canLand && !fishing)) return false;
        DesiredFlight = false;
        if (Stage == FlightStage.Flying) LandingQueued = true;
        return true;
    }

    // A warp/load must not remove flight until a destination has actually been validated.
    public void RetainForRecovery()
    {
        if (!Active) { Height = Opacity = 1; Phase = .55; }
        Stage = FlightStage.Flying;
        DesiredFlight = true;
        LandingQueued = false;
    }

    /// <returns>True when a queued landing was rejected because the ground is unsafe.</returns>
    public bool Advance(double milliseconds, bool canLand, bool fishing, bool moving)
    {
        double dt = double.IsFinite(milliseconds) ? Math.Clamp(milliseconds, 0, 100) : 0;
        Cycle = fishing ? "Q" : moving ? "M" : "H";
        if (Stage == FlightStage.Grounded) return false;
        AdvanceLean(dt / 1000, CanTravel && moving && !fishing);
        VisualSeconds += dt / 1000;
        if (Stage == FlightStage.TakingOff)
        {
            TransitionMs = Math.Min(700, TransitionMs + dt);
            float t = (float)(TransitionMs / 700);
            Height = Ease((t - .30f) / .70f);
            Opacity = Ease(t / .18f);
            Phase = Wrap(.55 - (1 - t) * 700 / 960d);
            if (TransitionMs >= 700)
            {
                Stage = FlightStage.Flying;
                Phase = .55;
                LandingQueued = !DesiredFlight;
            }
            return false;
        }
        if (Stage == FlightStage.Landing)
        {
            // An animal or map change can occupy the landing place mid-descent.
            if (!canLand)
            {
                Stage = FlightStage.Flying;
                DesiredFlight = true;
                LandingQueued = false;
                return true;
            }
            TransitionMs = Math.Min(700, TransitionMs + dt);
            float t = (float)(TransitionMs / 700);
            Height = 1 - Ease((t - .08f) / .77f);
            Opacity = 1 - Ease((t - .86f) / .14f);
            Phase = Wrap(.15 + t * 700 / 960d);
            if (TransitionMs >= 700)
            {
                bool restart = DesiredFlight;
                Reset();
                if (restart) { DesiredFlight = true; StartTakeoff(); }
            }
            return false;
        }
        Height = Math.Min(1, Height + (float)dt / 350);
        Opacity = Math.Min(1, Opacity + (float)dt / 180);
        double period = Cycle switch { "M" => 840, "Q" => 1200, _ => 960 };
        double previous = Phase;
        Phase = Wrap(Phase + dt / period);
        if (LandingQueued && !fishing)
        {
            if (!canLand)
            {
                DesiredFlight = true;
                LandingQueued = false;
                return true;
            }
            // L begins at phase .15. Wait for that pose instead of snapping wings.
            if (Crossed(previous, Phase, .15))
            {
                Stage = FlightStage.Landing;
                TransitionMs = 0;
                LandingQueued = false;
                Phase = .15;
            }
        }
        return false;
    }

    public void Reset()
    {
        Stage = FlightStage.Grounded;
        DesiredFlight = LandingQueued = false;
        Phase = TransitionMs = VisualSeconds = 0;
        Height = Opacity = 0;
        leanPosition = leanVelocity = 0;
        Cycle = "H";
    }

    private void StartTakeoff() { Stage = FlightStage.TakingOff; TransitionMs = 0; Height = Opacity = 0; }
    private void AdvanceLean(double seconds, bool moving)
    {
        // Exact critically damped solution: continuous position AND velocity on
        // start/stop/reversal, independent of frame rate. No overshoot or bob reset.
        double target = moving ? 1 : 0;
        double omega = moving ? 18 : 14;
        double error = leanPosition - target;
        double impulse = leanVelocity + omega * error;
        double decay = Math.Exp(-omega * seconds);
        leanPosition = target + (error + impulse * seconds) * decay;
        leanVelocity = (leanVelocity - omega * impulse * seconds) * decay;
        if (Math.Abs(leanPosition - target) < .0005 && Math.Abs(leanVelocity) < .005)
        { leanPosition = target; leanVelocity = 0; }
    }
    public static float Ease(float t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
    private static double Wrap(double value) => value - Math.Floor(value);
    private static bool Crossed(double old, double next, double target) => next >= old
        ? old < target && next >= target : target > old || target <= next;
}
