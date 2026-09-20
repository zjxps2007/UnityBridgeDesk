using System.Windows;
using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop.Tests;

[TestClass]
public sealed class AshaChartPlayTests
{
    private static AshaMap Playground(int count, double spacing = 135)
    {
        var ledges = new List<AshaLedge> { new(10, count * spacing + 100, 580, "floor", AshaLedgeKind.ChartFloor, "chart", spacing + 110) };
        var obstacles = new List<Rect>();
        for (int i = 0; i < count; i++)
        {
            double x = 75 + i * spacing, y = 505 - (i % 4) * 48;
            ledges.Add(new(x, x + 62, y - 3, "bar:" + i, AshaLedgeKind.ChartBar, "chart", spacing + 110));
            obstacles.Add(new(x - 2, y - 2, 66, 600 - y));
        }
        return new(new(0, 0, count * spacing + 180, 680), new(96, 96), obstacles, ledges);
    }
    [TestMethod]
    public void ApproachesLowestReachableBarThenJumpsToAHigherBar()
    {
        var map = Playground(5); var play = new AshaChartPlay(7);
        Point at = map.Positions.First(p => map.Support(p)?.Kind == AshaLedgeKind.ChartFloor);
        double now = play.NextEligible + .1;
        var entry = play.Choose(map, at, now); Assert.IsNotEmpty(entry); Assert.IsTrue(entry.All(map.CanFollow));
        at = entry[^1].To; Assert.AreEqual(map.Positions.Where(p => map.Support(p)?.Kind == AshaLedgeKind.ChartBar).Max(p => p.Y), at.Y, .01);
        play.Arrived(map.Support(at), ++now);
        var climb = play.Choose(map, at, now + 1); Assert.IsNotEmpty(climb); Assert.IsTrue(climb.All(map.CanFollow));
        Assert.IsTrue(climb[^1].To.Y < at.Y); Assert.IsTrue(climb.Any(s => s.IsJump));
    }
    [TestMethod]
    public void TwoBarsAlternateAndLargerChartsVisitSeveralBars()
    {
        foreach (int count in new[] { 2, 5, 8 })
        foreach (int seed in new[] { 7, 23, 99 })
        {
            var map = Playground(count, count == 2 ? 480 : 135); var play = new AshaChartPlay(seed);
            Point at = map.Positions.First(p => map.Support(p)?.Kind == AshaLedgeKind.ChartFloor);
            var visited = new HashSet<string>(); double now = play.NextEligible + 1; int trips = 0;
            for (int i = 0; i < 20; i++)
            {
                if (!play.Active) now = play.NextEligible + .1;
                var path = play.Choose(map, at, now);
                Assert.IsNotEmpty(path, $"{count} bars, seed {seed}, trip {i}"); Assert.IsTrue(path.All(map.CanFollow));
                at = path[^1].To; visited.Add(map.Support(at)!.Value.Id); play.Arrived(map.Support(at), now); trips++;
                now += play.Pause(1) + 2;
            }
            Assert.AreEqual(20, trips); Assert.IsGreaterThanOrEqualTo(Math.Min(count, 5), visited.Count);
        }
    }
    [TestMethod]
    public void CooldownsVaryAndCancellationDoesNotQueueOldHops()
    {
        var map = Playground(3); var play = new AshaChartPlay(31); var waits = new HashSet<double>();
        var at = map.Positions.First(p => map.Support(p)?.Kind == AshaLedgeKind.ChartFloor);
        Assert.IsEmpty(play.Choose(map, at, 0));
        for (int i = 0; i < 12; i++)
        {
            double now = play.NextEligible + .1;
            Assert.IsNotEmpty(play.Choose(map, at, now)); Assert.IsTrue(play.Active);
            waits.Add(play.Pause(1)); play.Cancel(now);
            Assert.IsFalse(play.Active); Assert.IsTrue(play.NextEligible - now is >= 18 and <= 42);
            Assert.IsEmpty(play.Choose(map, at, now + 1));
        }
        Assert.AreEqual(12, waits.Count);
        var empty = new AshaMap(new(0, 0, 900, 680), new(96, 96), [], [new(0, 800, 580)]);
        play.Reset(0); Assert.IsEmpty(play.Choose(empty, empty.Positions[0], 20)); Assert.IsFalse(play.Active);
    }
    [TestMethod]
    public void WiderChartHopStillCannotCrossAnObstacleOrLeaveTheWindow()
    {
        var ledges = new AshaLedge[] { new(70, 132, 500, "a", AshaLedgeKind.ChartBar, "c", 620), new(570, 632, 430, "b", AshaLedgeKind.ChartBar, "c", 620) };
        var map = new AshaMap(new(0, 0, 800, 650), new(96, 96), [new(350, 0, 5, 650)], ledges);
        Point Perch(AshaMap terrain, string id) => terrain.Positions.First(p => terrain.Support(p)?.Id == id);
        Assert.IsEmpty(map.Route(Perch(map, "a"), Perch(map, "b")));
        var clear = new AshaMap(new(0, 0, 800, 650), new(96, 96), [], ledges);
        Assert.IsTrue(clear.Route(Perch(clear, "a"), Perch(clear, "b")).Any(s => s.IsJump));
        var otherChart = new AshaMap(new(0, 0, 800, 650), new(96, 96), [], [ledges[0], ledges[1] with { Group = "different" }]);
        Assert.IsEmpty(otherChart.Route(Perch(otherChart, "a"), Perch(otherChart, "b")));
    }
}
