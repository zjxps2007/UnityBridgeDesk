using System.Windows;

namespace UnityBridgeDesk.Desktop.Controls;

public enum AshaFallContact { Air, Ledge, Blocked, Boundary }
public readonly record struct AshaFallResult(Point Position, double Speed, AshaFallContact Contact);

public sealed partial class AshaMap
{
    /// <summary>One-way platforms: sweep the feet, including fast falls; settle only where the body fits.</summary>
    public AshaFallResult Fall(Point from, double speed, double seconds)
    {
        const double gravity = 1400, terminal = 720;
        speed = Math.Clamp(speed, 0, terminal); seconds = Math.Clamp(seconds, 0, .08);
        double accelerating = Math.Min(seconds, (terminal - speed) / gravity);
        double distance = speed * accelerating + gravity * accelerating * accelerating / 2 + terminal * (seconds - accelerating);
        return TraceFall(from, from.Y + distance, Math.Min(terminal, speed + gravity * seconds));
    }

    public Point? LandingBelow(Point from)
    {
        var result = TraceFall(from, Bounds.Bottom - SpriteSize.Height, 0);
        return result.Contact == AshaFallContact.Ledge ? result.Position : null;
    }

    private AshaFallResult TraceFall(Point from, double requestedY, double speed)
    {
        double bottom = Bounds.Bottom - SpriteSize.Height;
        var next = new Point(from.X, Math.Min(requestedY, bottom));
        if (SweepLanding(from, next) is { } landing) return new(landing, 0, AshaFallContact.Ledge);
        return new(next, speed, requestedY >= bottom ? AshaFallContact.Boundary : AshaFallContact.Air);
    }
}
