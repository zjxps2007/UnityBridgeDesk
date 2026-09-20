using System.Windows;

namespace UnityBridgeDesk.Desktop.Controls;

/// <summary>A short, interruptible play bout using the actual visible chart platforms.</summary>
public sealed class AshaChartPlay
{
    private readonly Random random;
    private readonly Dictionary<string, int> visits = [];
    private string group = "", previous = "";
    private bool climbNext;
    private int remaining;
    public bool Active { get; private set; }
    public double NextEligible { get; private set; }
    public AshaChartPlay(int seed) { random = new(seed); Reset(0); }
    public void Reset(double now) { Active = false; visits.Clear(); group = previous = ""; remaining = 0; NextEligible = now + Between(5, 12); }
    public void Cancel(double now) { Active = false; remaining = 0; NextEligible = now + Between(18, 42); }
    private double Between(double low, double high) => low + random.NextDouble() * (high - low);
    public double Pause(int activity) => (random.NextDouble() < .15 ? Between(1.1, 1.8) : Between(.22, .85)) * (activity == 0 ? 1.3 : activity == 2 ? .8 : 1);
    public void Arrived(AshaLedge? support, double now)
    {
        if (!Active || support is not { Kind: AshaLedgeKind.ChartBar } bar || bar.Group != group) return;
        visits[bar.Id] = visits.GetValueOrDefault(bar.Id) + 1;
        previous = bar.Id;
        if (--remaining <= 0) Cancel(now);
    }
    public AshaStep[] Choose(AshaMap map, Point from, double now)
    {
        if (!Active && now < NextEligible) return [];
        var current = map.Support(from);
        var choices = map.ReachableRoutes(from)
            .Where(pair => pair.Value.Length > 0 && map.Support(pair.Key) is { Kind: AshaLedgeKind.ChartBar } bar &&
                bar.Id != current?.Id && (!Active || bar.Group == group))
            .Select(pair => (At: pair.Key, Path: pair.Value, Bar: map.Support(pair.Key)!.Value))
            .GroupBy(c => c.Bar.Id).Select(g => g.MinBy(c => c.Path.Sum(s => (s.To - s.From).Length))).ToArray();
        if (!Active)
        {
            // Interest starts nearby, rather than pulling the cat across the entire application.
            choices = choices.Where(c => (c.At - from).Length < 680 && Math.Abs(c.At.Y - from.Y) < 330).ToArray();
            if (choices.Length == 0) { NextEligible = now + Between(3, 7); return []; }
            var entry = choices.OrderByDescending(c => c.At.Y).ThenBy(c => (c.At - from).Length).First();
            group = entry.Bar.Group; visits.Clear(); previous = ""; remaining = random.Next(5, 10); climbNext = true; Active = true;
            return entry.Path;
        }
        // A play hop stays in the same chart; surrounding UI is used only for approach/departure.
        choices = choices.Where(c => c.Path.All(s => map.Support(s.From)?.Group == group && map.Support(s.To)?.Group == group)).ToArray();
        if (choices.Length == 0) { Cancel(now); return []; }
        if (climbNext)
        {
            climbNext = false;
            var higher = choices.Where(c => c.At.Y < from.Y - 3).OrderByDescending(c => c.At.Y).ToArray();
            if (higher.Length > 0) return higher[0].Path;
        }
        // Explore new bars first, then vary near/far hops. Two bars may alternate; larger sets do not get stuck on a pair.
        return choices.OrderByDescending(c => (visits.GetValueOrDefault(c.Bar.Id) == 0 ? 150 : 0)
            - visits.GetValueOrDefault(c.Bar.Id) * 35 - (c.Bar.Id == previous ? 50 : 0)
            - c.Path.Length * 3 + random.NextDouble() * 45).First().Path;
    }
}
