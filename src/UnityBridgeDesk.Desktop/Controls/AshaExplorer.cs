using System.Windows;

namespace UnityBridgeDesk.Desktop.Controls;

/// <summary>Buttons and text lines have their own visit memory; structural paths use spatial neighbourhoods.</summary>
public sealed class AshaExplorer
{
    private readonly Dictionary<(string Id, int X, int Y), (int Count, int Last)> visits = [];
    private readonly Queue<Point> recent = new();
    private readonly Random random;
    private int journey;
    private bool preferLower;
    private readonly CompanionKind kind;
    public AshaExplorer(int seed, CompanionKind kind = CompanionKind.Cat) { random = new(seed); this.kind = kind; }
    private static (string Id, int X, int Y) Region(Point point, string? id = null) =>
        string.IsNullOrEmpty(id) ? ("", (int)Math.Floor(point.X / 180), (int)Math.Round(point.Y / 28)) : (id, 0, 0);
    public void Arrived(Point from, Point to, string? surfaceId = null)
    {
        journey++;
        var key = Region(to, surfaceId); var old = visits.GetValueOrDefault(key);
        visits[key] = (Math.Min(12, old.Count + 1), journey);
        recent.Enqueue(to); while (recent.Count > 10) recent.Dequeue();
        if (to.Y < from.Y - 20) preferLower = true;
        else if (to.Y > from.Y + 20) preferLower = false;
        // Remember a bounded session history; layout changes do not erase visits to nearby places.
        if (visits.Count > 256) visits.Remove(visits.MinBy(pair => pair.Value.Last).Key);
    }
    public AshaStep[] Choose(AshaMap map, Point from, Func<Point, bool>? available = null)
    {
        var routes = map.ReachableRoutes(from).Where(pair => pair.Value.Length > 0 && (pair.Key - from).Length > 24 && (available?.Invoke(pair.Key) ?? true)).ToArray();
        if (routes.Length == 0) return [];
        var ordinary = routes.Where(pair => map.Support(pair.Key)?.Kind != AshaLedgeKind.ChartBar).ToArray();
        if (ordinary.Length > 0) routes = ordinary;
        string? current = map.Support(from)?.Id;
        var details = routes.Where(pair => map.Support(pair.Key) is { Kind: AshaLedgeKind.Button or AshaLedgeKind.TextLine or AshaLedgeKind.InputField } ledge && ledge.Id != current).ToArray();
        // Structural lines may connect a route, but a reachable individual control/line is the destination.
        if (details.Length > 0) routes = details;
        // Prefer a visibly different destination; keep short moves when the usable area is tiny.
        var wider = routes.Where(pair => (pair.Key - from).Length >= 100).ToArray();
        if (wider.Length > 0) routes = wider;
        double Score(Point p)
        {
            var support = map.Support(p);
            string? id = support is { Kind: not AshaLedgeKind.Edge } ledge ? ledge.Id : null;
            var visit = visits.GetValueOrDefault(Region(p, id));
            double age = visit.Count == 0 ? 10 : Math.Min(10, journey - visit.Last);
            double nearRecent = recent.Reverse().Select((r, i) => Math.Max(0, 1 - (p - r).Length / 160) * (180 - i * 14)).DefaultIfEmpty().Max();
            double vertical = CompanionCharacter.DestinationBias(kind, from, p, support, preferLower);
            return (visit.Count == 0 ? 230 : 0) + age * 18 - visit.Count * 8 - nearRecent
                + Math.Min(125, (p - from).Length * .22) + vertical + random.NextDouble() * 18;
        }
        return routes.OrderByDescending(pair => Score(pair.Key)).First().Value;
    }
}
