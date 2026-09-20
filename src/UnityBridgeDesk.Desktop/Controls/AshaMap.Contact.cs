using System.Windows;

namespace UnityBridgeDesk.Desktop.Controls;

/// <summary>Continuous allowed range of the actor's left coordinate, not a set of landing samples.</summary>
public readonly record struct AshaStandingSpan(double Left, double Right, double Top, AshaLedge Ledge)
{
    public Point Closest(Point wanted) => new(Math.Clamp(wanted.X, Left, Right), Top);
    public bool Contains(double x) => x >= Left - .000001 && x <= Right + .000001;
}

public sealed partial class AshaMap
{
    private readonly AshaStandingSpan[] standingSpans;
    public IReadOnlyList<AshaStandingSpan> StandingSpans => standingSpans;
    private bool FeetFit(Point p, AshaLedge ledge) => Math.Abs(p.Y + Feet - ledge.Y) < .6 &&
        p.X + SpriteSize.Width * .35 >= ledge.Left - .000001 && p.X + SpriteSize.Width * .65 <= ledge.Right + .000001;

    private AshaStandingSpan[] BuildStandingSpans()
    {
        var result = new List<AshaStandingSpan>();
        foreach (var ledge in ledges)
        {
            double top = ledge.Y - Feet;
            if (top < Bounds.Top || top + SpriteSize.Height > Bounds.Bottom) continue;
            double left = Math.Max(Bounds.Left, ledge.Left - SpriteSize.Width * .35);
            double right = Math.Min(Bounds.Right - SpriteSize.Width, ledge.Right - SpriteSize.Width * .65);
            if (right < left) continue;
            var ranges = new List<(double Left, double Right)> { (left, right) };
            foreach (var obstacle in obstacles)
            {
                if (obstacle.Top >= ledge.Y - .01 || obstacle.Bottom <= top + .01) continue;
                // Minkowski expansion: exclude actor origins whose body intersects this obstacle.
                double cutLeft = obstacle.Left - SpriteSize.Width + .01, cutRight = obstacle.Right - .01;
                var next = new List<(double Left, double Right)>();
                foreach (var range in ranges)
                {
                    if (cutRight <= range.Left || cutLeft >= range.Right) { next.Add(range); continue; }
                    if (cutLeft >= range.Left) next.Add((range.Left, Math.Min(range.Right, cutLeft)));
                    if (cutRight <= range.Right) next.Add((Math.Max(range.Left, cutRight), range.Right));
                }
                ranges = next;
                if (ranges.Count == 0) break;
            }
            foreach (var range in ranges)
                if (range.Right >= range.Left) result.Add(new(range.Left, range.Right, top, ledge));
        }
        return result.OrderBy(span => span.Top).ThenBy(span => span.Left).ToArray();
    }

    public bool CanWalk(Point from, Point to)
    {
        if (Math.Abs(from.Y - to.Y) > .01 || !IsSafe(from) || !IsSafe(to)) return false;
        double covered = Math.Min(from.X, to.X), end = Math.Max(from.X, to.X);
        foreach (var span in standingSpans.Where(s => Math.Abs(s.Top - from.Y) < .01).OrderBy(s => s.Left))
        {
            if (span.Right < covered) continue;
            if (span.Left > covered + .000001) return false;
            covered = Math.Max(covered, span.Right);
            if (covered >= end - .000001) return true;
        }
        return false;
    }

    /// <summary>First supported crossing of a descending foot segment, including horizontal travel.</summary>
    public Point? SweepLanding(Point from, Point to, bool includeStart = true)
    {
        double dy = to.Y - from.Y;
        if (dy < 0) return null;
        foreach (var span in standingSpans)
        {
            if (span.Top < from.Y - .000001 || span.Top > to.Y + .000001) continue;
            if (!includeStart && span.Top <= from.Y + .000001) continue;
            double t = dy < .000001 ? 0 : Math.Clamp((span.Top - from.Y) / dy, 0, 1);
            var contact = new Point(from.X + (to.X - from.X) * t, span.Top);
            if (span.Contains(contact.X) && IsSafe(contact)) return contact;
        }
        return null;
    }
}
