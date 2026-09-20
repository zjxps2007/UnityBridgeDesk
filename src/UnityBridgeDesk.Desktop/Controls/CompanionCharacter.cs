using System.Windows;

namespace UnityBridgeDesk.Desktop.Controls;

public enum CompanionKind { Cat, Fox }

/// <summary>Species preferences affect decisions and gait, never the benchmark or platform safety.</summary>
public static class CompanionCharacter
{
    public static string Name(CompanionKind kind) => kind == CompanionKind.Fox ? "아샤" : "아라";
    public static string Atlas(CompanionKind kind) => kind == CompanionKind.Fox ? "asha-fox-distinct-sprites.png" : "asha-sprites.png";
    public static double Speed(CompanionKind kind, int activity) => (activity switch { 0 => 45, 2 => 85, _ => 65 }) * (kind == CompanionKind.Fox ? 1.28 : 1);
    public static double DestinationBias(CompanionKind kind, Point from, Point to, AshaLedge? ledge, bool preferLower)
    {
        if (kind == CompanionKind.Cat) return preferLower ? to.Y > from.Y + 20 ? 24 : 0 : to.Y < from.Y - 20 ? 55 : 0;
        // Foxes patrol wide, lower ledges, with occasional climbs when novelty wins.
        double width = ledge is { } support ? support.Right - support.Left : 0;
        return Math.Min(65, width * .18) + Math.Min(70, Math.Abs(to.X - from.X) * .18)
            + (to.Y > from.Y + 20 ? 60 : to.Y < from.Y - 20 ? -55 : 45);
    }
}
