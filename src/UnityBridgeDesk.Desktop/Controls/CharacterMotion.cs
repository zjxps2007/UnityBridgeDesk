namespace UnityBridgeDesk.Desktop.Controls;

/// <summary>Finite, foot-anchored motion envelopes; sampling never starts a new action.</summary>
public static class CharacterMotion
{
    public static double Smooth(double value)
    {
        double t = Math.Clamp(value, 0, 1);
        return t * t * t * (10 + t * (-15 + 6 * t));
    }
    public static double Pulse(double seconds, double duration)
    {
        double t = Math.Clamp(seconds / duration, 0, 1);
        return Math.Pow(Math.Sin(t * Math.PI), 2);
    }
    public static double Landing(double seconds, double impact)
    {
        double attack = .065, end = .16 + .18 * Math.Clamp(impact, .1, 1);
        return seconds < attack ? Smooth(seconds / attack) : 1 - Smooth((seconds - attack) / (end - attack));
    }
    public static double Follow(double current, double target, double seconds, double response)
        => current + (target - current) * (1 - Math.Exp(-Math.Clamp(seconds, 0, .08) / response));
}
