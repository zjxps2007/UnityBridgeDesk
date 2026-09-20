using System.Windows;
using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop.Tests;

[TestClass]
public sealed class AshaExplorerTests
{
    [TestMethod]
    public void IndividualSurfacesTakePriorityAndRememberSeparateVisitsInTheSameRegion()
    {
        var map = new AshaMap(new(0, 0, 700, 400), new(40, 40), [], [new(0, 650, 260, "edge"),
            new(205, 255, 150, "first-line", AshaLedgeKind.TextLine), new(270, 320, 150, "button", AshaLedgeKind.Button)]);
        Point at = map.Positions.First(p => map.Support(p)?.Id == "edge");
        var explorer = new AshaExplorer(5);
        for (int i = 0; i < 10; i++) explorer.Arrived(new(0, 0), new(220, 114.4), "first-line");
        var route = explorer.Choose(map, at); Assert.IsNotEmpty(route);
        Assert.AreEqual("button", map.Support(route[^1].To)?.Id, "Neighbouring controls have separate visit histories, even inside one old spatial block.");
        Assert.IsTrue(route.All(map.CanFollow));
    }
    [TestMethod]
    public void ExplorationLeavesTheInitialNeighbourhoodAndUsesDifferentLevels()
    {
        foreach (int seed in new[] { 7, 23, 99 })
        {
            var map = new AshaMap(new(0, 0, 1200, 600), new(40, 40), [], [new(0, 1150, 120), new(0, 1150, 230), new(450, 650, 350)]);
            var explorer = new AshaExplorer(seed); Point at = map.Nearest(new(500, 310))!.Value;
            explorer.Arrived(at, at); var destinations = new List<Point>();
            for (int i = 0; i < 12; i++)
            {
                var route = explorer.Choose(map, at); Assert.IsNotEmpty(route);
                foreach (var step in route)
                {
                    Assert.AreEqual(at, step.From); Assert.IsTrue(map.CanFollow(step));
                    explorer.Arrived(step.From, step.To); at = step.To;
                }
                destinations.Add(at);
            }
            Assert.IsGreaterThan(700d, destinations.Max(p => p.X) - destinations.Min(p => p.X), "Do not orbit the starting title.");
            Assert.IsGreaterThanOrEqualTo(6, destinations.Select(p => ((int)(p.X / 180), (int)(p.Y / 28))).Distinct().Count());
            Assert.IsGreaterThanOrEqualTo(2, destinations.Select(p => Math.Round(p.Y)).Distinct().Count());
        }
    }
    [TestMethod]
    public void SmallPerchStillAllowsMovementWhenEveryPlaceWasVisited()
    {
        var map = new AshaMap(new(0, 0, 300, 300), new(40, 40), [], [new(50, 130, 180)]);
        var explorer = new AshaExplorer(1); Point at = map.Positions[0];
        for (int i = 0; i < 20; i++)
        {
            explorer.Arrived(at, at); var path = explorer.Choose(map, at);
            Assert.IsNotEmpty(path); Assert.IsTrue(path.All(map.CanFollow)); at = path[^1].To;
        }
    }
    [TestMethod]
    public void NoveltyNeverSelectsAnUnreachableRegion()
    {
        var map = new AshaMap(new(0, 0, 800, 600), new(40, 40), [new(390, 0, 20, 600)], [new(0, 350, 250), new(450, 800, 250)]);
        var explorer = new AshaExplorer(2); Point at = map.Positions[0];
        for (int i = 0; i < 10; i++)
        {
            var path = explorer.Choose(map, at); Assert.IsNotEmpty(path);
            Assert.IsTrue(path.All(step => step.To.X < 390 && map.CanFollow(step)));
            foreach (var step in path) explorer.Arrived(step.From, step.To);
            at = path[^1].To;
        }
    }
    [TestMethod]
    public void DropApproachStaysSupportedAndLandingReflectsHeight()
    {
        var map = new AshaMap(new(0, 0, 800, 600), new(40, 40), [], [new(0, 700, 180), new(0, 700, 350)]);
        Point at = map.Nearest(new(180, 144))!.Value, to = map.Nearest(new(230, 314))!.Value;
        var path = map.Route(at, to); int drop = Array.FindIndex(path, step => step.IsDrop);
        Assert.IsGreaterThan(0, drop); Assert.IsFalse(path[drop - 1].IsAirborne);
        foreach (var step in path) { Assert.AreEqual(at, step.From); Assert.IsTrue(map.CanFollow(step)); at = step.To; }
        Assert.AreEqual(to, at);
        var shallow = new AshaStep(new(0, 0), new(0, 30), 0, true);
        var high = new AshaStep(new(0, 0), new(0, 200), 0, true);
        Assert.IsTrue(high.Impact > shallow.Impact); Assert.IsTrue(high.LandingDuration > shallow.LandingDuration);
        Assert.IsTrue(high.PeekDuration > shallow.PeekDuration);
    }
    [TestMethod]
    public void ExpressionsMoveEarsTailAndGazeWithoutMovingTheFeet()
    {
        var pose = new AshaExpression(1, 1, 1, 1);
        foreach (Point point in new Point[] { new(.28, .05), new(.9, .76), new(.5, .48) })
        { Assert.AreNotEqual(point, AshaSprite.Deform(point, 4, pose)); Assert.AreEqual(point, AshaSprite.Deform(point, 4, default)); }
        Point feet = new(.5, .96); Assert.AreEqual(feet, AshaSprite.Deform(feet, 4, pose));
    }
}
