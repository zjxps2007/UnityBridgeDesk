namespace UnityBridgeDesk.Desktop.Controls;

/// <summary>One full alternating-leg cycle per 48 DIP of travel at the 96 DIP reference size.</summary>
public static class FoxGait
{
    public const double Cycle = 48;
    public static double Advance(double phase, double distance, double size)
    {
        if (!double.IsFinite(distance) || !double.IsFinite(size) || distance <= 0 || size <= 0) return phase;
        return (phase + distance * 96 / size) % Cycle;
    }
    public static int Frame(double phase) => (int)(Math.Max(0, phase) / (Cycle / 8)) % 8;
}
