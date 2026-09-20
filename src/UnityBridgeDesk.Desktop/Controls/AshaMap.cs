using System.Windows;

namespace UnityBridgeDesk.Desktop.Controls;

public enum AshaEdge { None, Top, Bottom }

/// <summary>Opt-in footholds, independent of hit testing or application data.</summary>
public static class AshaSurface
{
    public static readonly DependencyProperty EdgeProperty = DependencyProperty.RegisterAttached("Edge", typeof(AshaEdge), typeof(AshaSurface), new PropertyMetadata(AshaEdge.None));
    public static AshaEdge GetEdge(DependencyObject element) => (AshaEdge)element.GetValue(EdgeProperty);
    public static void SetEdge(DependencyObject element, AshaEdge value) => element.SetValue(EdgeProperty, value);
}

public enum AshaLedgeKind { Edge, Button, TextLine, ChartBar, ChartFloor, InputField }
public readonly record struct AshaLedge(double Left, double Right, double Y, string Id = "", AshaLedgeKind Kind = AshaLedgeKind.Edge, string Group = "", double Reach = 250);
public readonly record struct AshaPerch(string Id, double Along, Point Fallback, bool FaceLeft);
public readonly record struct AshaStep(Point From, Point To, double Arc, bool IsDrop = false, bool Vault = false)
{
    public bool IsJump => Arc > 0 && !IsDrop;
    public bool IsAirborne => IsJump || IsDrop;
    public double Impact => Math.Clamp((Math.Max(0, To.Y - From.Y) + Arc * .5) / 225, .1, 1);
    public double LandingDuration => .16 + Impact * .18;
    public double PeekDuration => IsDrop ? .25 + Impact * .3 : 0;
    public Point At(double progress)
    {
        double t = Math.Clamp(progress, 0, 1);
        // A drop leaves the edge with a small push, then accelerates toward the lower ledge.
        double vertical = IsDrop ? t * t : t;
        double horizontal = Vault ? t * t * (3 - 2 * t) : t;
        return new(From.X + (To.X - From.X) * horizontal, From.Y + (To.Y - From.Y) * vertical - 4 * Arc * t * (1 - t));
    }
}

/// <summary>A platform graph: feet need a designated ledge, and the complete body must clear every jump.</summary>
public sealed partial class AshaMap
{
    private readonly Rect[] obstacles;
    private readonly AshaLedge[] ledges;
    private readonly Point[] positions;
    private readonly Dictionary<(Point From, Point To), AshaStep?> connections = [];
    private readonly double maxReach;
    public Rect Bounds { get; }
    public Size SpriteSize { get; }
    // The atlas has transparent room below its shoes. Match the visual foot rather than the control bottom.
    public double Feet => SpriteSize.Height * .89;
    public IReadOnlyList<Point> Positions => positions;
    public AshaMap(Rect bounds, Size spriteSize, IEnumerable<Rect> obstacles, IEnumerable<AshaLedge> ledges)
    {
        Bounds = bounds; SpriteSize = spriteSize; this.obstacles = obstacles.Where(r => !r.IsEmpty).ToArray();
        this.ledges = ledges.Where(l => double.IsFinite(l.Left) && double.IsFinite(l.Right) && double.IsFinite(l.Y) && l.Right >= l.Left).ToArray();
        maxReach = Math.Max(250, this.ledges.Select(l => l.Reach).DefaultIfEmpty(250).Max());
        standingSpans = !bounds.IsEmpty && spriteSize.Width > 0 && spriteSize.Height > 0 ? BuildStandingSpans() : [];
        var points = new List<Point>();
        foreach (var span in standingSpans)
        {
            double left = span.Left, right = span.Right;
            int divisions = Math.Max(1, (int)Math.Ceiling((right - left) / 40));
            for (int i = 0; i <= divisions; i++)
            {
                Point p = new(left + (right - left) * i / divisions, span.Top);
                if (IsSafe(p) && !points.Any(q => (q - p).Length < 2)) points.Add(p);
            }
        }
        positions = points.ToArray();
    }
    private static bool Overlaps(Rect a, Rect b) => a.Left < b.Right - .01 && a.Right > b.Left + .01 && a.Top < b.Bottom - .01 && a.Bottom > b.Top + .01;
    public bool IsClear(Point p) => Bounds.Contains(new Rect(p, SpriteSize)) && !obstacles.Any(r => Overlaps(r, new Rect(p, new Size(SpriteSize.Width, Feet))));
    public bool IsSupported(Point p) => ledges.Any(l => FeetFit(p, l));
    public bool IsSafe(Point p) => IsClear(p) && IsSupported(p);
    public Point? Nearest(Point wanted) => standingSpans.Select(span => span.Closest(wanted)).Where(IsSafe)
        .OrderBy(p => (p - wanted).LengthSquared).Cast<Point?>().FirstOrDefault();
    public AshaLedge? Support(Point p) => ledges.Where(l => FeetFit(p, l))
        .OrderBy(l => l.Right - l.Left).Cast<AshaLedge?>().FirstOrDefault();
    public AshaPerch? Remember(Point p, bool faceLeft) => IsSafe(p) && Support(p) is { } ledge
        ? new(ledge.Id, Math.Clamp((p.X + SpriteSize.Width * .5 - ledge.Left) / Math.Max(1, ledge.Right - ledge.Left), 0, 1), p, faceLeft) : null;
    public Point? Restore(AshaPerch perch)
    {
        if (perch.Id.Length > 0 && ledges.Cast<AshaLedge?>().FirstOrDefault(l => l!.Value.Id == perch.Id) is { } ledge)
        {
            Point wanted = new(ledge.Left + perch.Along * (ledge.Right - ledge.Left) - SpriteSize.Width * .5, ledge.Y - Feet);
            if ((wanted - perch.Fallback).Length < .000001 && IsSafe(perch.Fallback)) return perch.Fallback;
            var matches = standingSpans.Where(s => s.Ledge.Id == ledge.Id).Select(s => s.Closest(wanted)).Where(IsSafe).ToArray();
            if (matches.Length > 0) return matches.MinBy(p => (p - wanted).LengthSquared);
        }
        return Nearest(perch.Fallback);
    }
    private bool SweepClear(Point from, Point to)
    {
        var sweep = new Rect(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Abs(from.X - to.X) + SpriteSize.Width, Math.Abs(from.Y - to.Y) + Feet);
        return IsClear(from) && IsClear(to) && !obstacles.Any(r => Overlaps(r, sweep));
    }
    public bool CanFollow(AshaStep step)
    {
        if (!IsSafe(step.From) || !IsSafe(step.To) || step.IsDrop && step.To.Y <= step.From.Y) return false;
        if (!step.IsAirborne) return CanWalk(step.From, step.To) && SweepClear(step.From, step.To);
        int count = Math.Max(2, (int)Math.Ceiling(((step.To - step.From).Length + step.Arc * 2) / 4));
        Point previous = step.From;
        for (int i = 1; i <= count; i++)
        {
            Point p = step.At((double)i / count);
            if (!SweepClear(previous, p) || !step.IsAirborne && !IsSupported(p)) return false;
            previous = p;
        }
        return true;
    }
    public AshaStep? Connection(Point from, Point to)
    {
        var key = (from, to);
        if (!connections.TryGetValue(key, out var step)) connections[key] = step = FindConnection(from, to);
        return step;
    }
    private AshaStep? FindConnection(Point from, Point to)
    {
        double dx = Math.Abs(to.X - from.X), dy = to.Y - from.Y;
        if ((to - from).Length < .5) return null;
        var walk = new AshaStep(from, to, 0);
        if (Math.Abs(dy) < .5 && CanFollow(walk)) return walk;
        var a = Support(from); var b = Support(to);
        bool chartHop = a is { Group.Length: > 0 } && b is { Group.Length: > 0 } && a.Value.Group == b.Value.Group;
        bool chartApproach = a is { Kind: not (AshaLedgeKind.ChartBar or AshaLedgeKind.ChartFloor) } && b is { Kind: AshaLedgeKind.ChartBar } && dy >= 0;
        double reach = chartHop ? Math.Max(a!.Value.Reach, b!.Value.Reach) : chartApproach ? b!.Value.Reach : 250;
        if (dx > reach || dy < (chartHop ? -300 : -175) || dy > (chartHop || chartApproach ? 300 : 225)) return null;
        if (dy > 20)
        {
            // Try stepping off first; only add enough lift to clear text at the take-off edge.
            foreach (double push in new[] { 0d, 8, 16, 24, 40, 56 })
            {
                var drop = new AshaStep(from, to, push, IsDrop: true);
                if (CanFollow(drop)) return drop;
            }
            return null;
        }
        double arc = chartHop ? Math.Max(18 + Math.Min(dx, 180) * .07, Math.Abs(dy) * .2 + 8) : Math.Max(24 + dx * .12, Math.Abs(dy) * .32 + 12);
        // A higher arc can clear a ledge's front instead of passing through its text.
        foreach (double lift in chartHop ? new[] { 0d, 24, 48, 72, 112, 160 } : new[] { 0d, 24, 48, 72 })
        {
            var jump = new AshaStep(from, to, arc + lift, Vault: chartHop);
            if (CanFollow(jump)) return jump;
        }
        return null;
    }
    public AshaStep[] Route(Point from, Point wanted)
    {
        if (!IsSafe(from) || Nearest(wanted) is not { } goal || (goal - wanted).Length > 100) return [];
        return Search(from, goal).GetValueOrDefault(goal, []);
    }
    public IReadOnlyDictionary<Point, AshaStep[]> ReachableRoutes(Point from) => IsSafe(from) ? Search(from, null) : new Dictionary<Point, AshaStep[]>();
    private Dictionary<Point, AshaStep[]> Search(Point from, Point? goal)
    {
        var nodes = goal is { } exact ? positions.Append(exact).Distinct().ToArray() : positions;
        var frontier = new PriorityQueue<Point, double>(); frontier.Enqueue(from, 0);
        var cost = new Dictionary<Point, double> { [from] = 0 };
        var parents = new Dictionary<Point, AshaStep>();
        var visited = new HashSet<Point>();
        while (frontier.TryDequeue(out Point at, out _))
        {
            if (!visited.Add(at)) continue;
            if (goal is { } target && at == target) break;
            // Nearest nodes on each ledge keep the graph bounded without losing narrow stepping stones.
            var nearby = nodes.Where(p => !visited.Contains(p) && Math.Abs(p.X - at.X) <= maxReach && p.Y - at.Y is >= -300 and <= 300)
                .GroupBy(p => Math.Round(p.Y, 1)).SelectMany(g => g.OrderBy(p => (p - at).LengthSquared).Take(8));
            foreach (Point next in nearby)
            {
                if (Connection(at, next) is not { } step) continue;
                double distance = cost[at] + (next - at).Length + (step.IsDrop ? 45 : step.IsJump ? 65 : 0);
                if (cost.TryGetValue(next, out double known) && known <= distance) continue;
                cost[next] = distance; parents[next] = step;
                frontier.Enqueue(next, distance + (goal is { } destination ? (destination - next).Length : 0));
            }
        }
        var routes = new Dictionary<Point, AshaStep[]>();
        foreach (Point end in visited.Where(p => p != from && (goal is null || p == goal)))
        {
            var path = new List<AshaStep>(); Point at = end;
            while (parents.TryGetValue(at, out var step)) { path.Add(step); at = step.From; }
            path.Reverse(); routes[end] = ApproachDrops(path);
        }
        return routes;
    }
    private AshaStep[] ApproachDrops(IEnumerable<AshaStep> path)
    {
        var result = new List<AshaStep>();
        foreach (var step in path)
        {
            if (step.IsDrop)
            {
                // Move along the supported lip before looking over it. Short/narrow ledges
                // retain an in-place peek when there is no safe walking room.
                int direction = Math.Sign(step.To.X - step.From.X);
                var options = positions.Where(p => Math.Abs(p.Y - step.From.Y) < .5 && Math.Abs(p.X - step.From.X) is >= 24 and <= 72)
                    .OrderBy(p => direction != 0 && Math.Sign(p.X - step.From.X) != direction ? 1 : 0)
                    .ThenBy(p => Math.Abs(p.X - step.To.X));
                bool approached = false;
                foreach (Point p in options)
                {
                    if (Connection(step.From, p) is { IsAirborne: false } walk && Connection(p, step.To) is { IsDrop: true } drop)
                    { result.Add(walk); result.Add(drop); approached = true; break; }
                }
                if (approached) continue;
            }
            result.Add(step);
        }
        return result.ToArray();
    }
}
