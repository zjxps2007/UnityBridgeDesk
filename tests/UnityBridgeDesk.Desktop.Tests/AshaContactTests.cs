using System.Windows;
using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop.Tests;

[TestClass]
public sealed class AshaContactTests
{
    [TestMethod]
    public void SmallClearSlotBetweenObstaclesCannotDisappearBetweenNavigationSamples()
    {
        var map = new AshaMap(new(0, 0, 400, 500), new(40, 40),
            [new(0, 200, 101.1, 90), new(144.2, 200, 255.8, 90)], [new(0, 400, 300, "shelf")]);
        Point wanted = new(102.2, 300 - map.Feet);
        Assert.IsTrue(map.IsSafe(wanted));
        Assert.AreEqual(wanted, map.Nearest(wanted));
        Assert.IsTrue(map.Positions.Any(map.IsSafe), "Every continuous clear interval contributes navigation candidates.");
        Assert.AreEqual(wanted, map.LandingBelow(new(102.2, 0)));
    }
    [TestMethod]
    public void NearestAndSavedPerchesPreserveAnArbitrarySafeHorizontalCoordinate()
    {
        var map = new AshaMap(new(0, 0, 800, 600), new(96, 96), [], [new(0, 800, 400, "shelf")]);
        Point wanted = new(267.12345, 400 - map.Feet);
        Assert.AreEqual(wanted, map.Nearest(wanted));
        Assert.AreEqual(wanted, map.Restore(map.Remember(wanted, false)!.Value));
        var route = map.Route(new(40, wanted.Y), wanted);
        Assert.IsNotEmpty(route); Assert.AreEqual(wanted, route[^1].To);
        // A destination near a graph node must not stop the search at that different node.
        var nearNode = map.Positions[4] + new Vector(.25, 0);
        route = map.Route(map.Positions[0], nearNode);
        Assert.IsNotEmpty(route); Assert.AreEqual(nearNode, route[^1].To);
    }
    [TestMethod]
    public void WalkingChecksEverySupportIntervalEvenWhenAGapIsSmallerThanASamplingStep()
    {
        var map = new AshaMap(new(0, 0, 450, 450), new(2, 2), [], [new(0, 100, 200), new(100.15, 440, 200)]);
        Point from = new(20, 200 - map.Feet), to = new(380, from.Y);
        Assert.IsTrue(map.IsSafe(from) && map.IsSafe(to));
        Assert.IsFalse(map.CanWalk(from, to));
        Assert.IsFalse(map.CanFollow(new(from, to, 0)));
    }
    [TestMethod]
    public void DescendingSweepFindsTheFirstCrossingAtItsActualHorizontalCoordinate()
    {
        var map = new AshaMap(new(0, 0, 600, 600), new(40, 40), [],
            [new(130, 180, 190, "first"), new(200, 500, 300, "second")]);
        Point from = new(100, 100), to = new(300, 350);
        Point contact = map.SweepLanding(from, to)!.Value;
        double expectedX = from.X + (to.X - from.X) * ((190 - map.Feet - from.Y) / (to.Y - from.Y));
        Assert.AreEqual(expectedX, contact.X, .000001);
        Assert.AreEqual("first", map.Support(contact)?.Id);
        Assert.IsNull(map.SweepLanding(to, from), "Rising feet do not land on a one-way surface.");
    }
    [TestMethod]
    public void LandingDoesNotRequireANavigationNodeAtTheContactPoint()
    {
        var map = new AshaMap(new(0, 0, 800, 700), new(96, 96), [], [new(200, 600, 400, "floor")]);
        Point from = new(283.271, 250);
        var hit = map.Fall(from, 720, .08);
        Assert.AreEqual(AshaFallContact.Air, hit.Contact);
        hit = map.Fall(hit.Position, hit.Speed, .08);
        Assert.AreEqual(AshaFallContact.Ledge, hit.Contact);
        Assert.AreEqual(from.X, hit.Position.X);
        Assert.AreEqual(400d, hit.Position.Y + map.Feet, .000001);
    }
    [TestMethod]
    public void GeometryResizeChangesFootprintAndInvalidShapesCannotProduceGround()
    {
        var bounds = new Rect(0, 0, 800, 600);
        AshaLedge[] ledges = [new(200, 230, 400, "thin"), new(double.NaN, 500, 300, "invalid")];
        var small = new AshaMap(bounds, new(96, 96), [], ledges);
        var large = new AshaMap(bounds, new(144, 144), [], ledges);
        Assert.IsNotNull(small.Nearest(new(165, 310)));
        Assert.IsNull(large.Nearest(new(165, 310)));
    }
}
