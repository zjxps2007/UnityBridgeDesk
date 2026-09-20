namespace UnityBridgeDesk.Desktop.Controls;

public enum AshaIntent { Explore, Climb, Descend }

/// <summary>Distance-driven gait with finite acceleration at the ends of a continuous walk.</summary>
public readonly record struct AshaWalk(double Length, double Speed, bool Start, bool Stop)
{
    private double Ramp => Math.Min(.22, Length / Speed);
    private double Up => Start ? Ramp : 0;
    private double Down => Stop ? Ramp : 0;
    public double Duration => Length / Speed + (Up + Down) / 2;
    public double Distance(double seconds)
    {
        double t = Math.Clamp(seconds, 0, Duration);
        // Integral of smoothstep velocity: acceleration is zero at each end of the ramp.
        static double RampDistance(double time, double ramp) { double u = time / ramp; return ramp * (u * u * u - .5 * u * u * u * u); }
        if (Up > 0 && t < Up) return Speed * RampDistance(t, Up);
        if (Down > 0 && t > Duration - Down)
        { double remaining = Duration - t; return Length - Speed * RampDistance(remaining, Down); }
        return Math.Clamp(Speed * (t - Up / 2), 0, Length);
    }
    public static int Frame(double distance) => (int)(Math.Max(0, distance) / 9.75) % 4;
}

/// <summary>Small, bounded reactions; repeated input never creates an animation queue.</summary>
public sealed class AshaInteraction
{
    private double last = double.NegativeInfinity;
    private int taps;
    public AshaAction? Tap(double now)
    {
        if (now - last < (taps >= 3 ? 1.8 : .25)) return null;
        taps = now - last > 5 || now < last ? 1 : Math.Min(3, taps + 1);
        last = now;
        return taps switch { 1 => AshaAction.Glance, 2 => AshaAction.Play, _ => AshaAction.Dismiss };
    }
    public void Reset() { last = double.NegativeInfinity; taps = 0; }
}
