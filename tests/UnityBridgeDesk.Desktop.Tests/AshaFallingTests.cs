using System.Windows;
using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop.Tests;

[TestClass]
public sealed class AshaFallingTests
{
    private static AshaMap Map() => new(new(0, 0, 800, 700), new(96, 96),
        [new Rect(100, 303, 450, 12), new Rect(0, 603, 800, 12)],
        [new(100, 550, 300, "upper"), new(0, 800, 600, "lower")]);

    [TestMethod]
    public void ReleasedCharacterAcceleratesAndLandsOnFirstActualLedge()
    {
        var map = Map(); Point p = new(250, 50); double velocity = 0, previousDistance = 0;
        for (int i = 0; i < 200; i++)
        {
            var next = map.Fall(p, velocity, .02);
            Assert.AreEqual(p.X, next.Position.X);
            Assert.IsTrue(map.IsClear(next.Position));
            if (next.Contact == AshaFallContact.Ledge)
            {
                Assert.AreEqual("upper", map.Support(next.Position)?.Id); Assert.AreEqual(0d, next.Speed); return;
            }
            Assert.AreEqual(AshaFallContact.Air, next.Contact);
            Assert.IsGreaterThanOrEqualTo(previousDistance - .00001, next.Position.Y - p.Y);
            previousDistance = next.Position.Y - p.Y; p = next.Position; velocity = next.Speed;
        }
        Assert.Fail("The fall must finish, not hover indefinitely.");
    }

    [TestMethod]
    public void FastFallCannotTunnelAndARemovedPlatformIsNotRemembered()
    {
        var map = Map(); var start = new Point(250, 300 - map.Feet - 9);
        var hit = map.Fall(start, 720, .08);
        Assert.AreEqual(AshaFallContact.Ledge, hit.Contact); Assert.AreEqual(300 - map.Feet, hit.Position.Y, .001);
        var removed = new AshaMap(map.Bounds, map.SpriteSize, [], [new(0, 800, 600, "lower")]);
        var next = removed.Fall(start, 720, .08);
        Assert.AreEqual(AshaFallContact.Air, next.Contact); Assert.IsGreaterThan(hit.Position.Y, next.Position.Y);
        Assert.AreEqual(new Point(250, 600 - map.Feet), removed.LandingBelow(start));
    }

    [TestMethod]
    public void ObstaclesAndViewportEdgesNeverBecomeInvisibleFloors()
    {
        var blocked = new AshaMap(new(0, 0, 800, 700), new(96, 96), [new(200, 250, 300, 25)], [new(0, 800, 600, "floor")]);
        var contact = blocked.Fall(new(230, 150), 720, .08);
        Assert.AreEqual(AshaFallContact.Air, contact.Contact);
        Assert.IsGreaterThan(150d, contact.Position.Y);
        Assert.IsFalse(blocked.IsSafe(contact.Position));
        Assert.AreEqual(new Point(230, 600 - blocked.Feet), blocked.LandingBelow(new(230, 150)), "Unlandable UI must not cancel gravity.");
        var empty = new AshaMap(blocked.Bounds, blocked.SpriteSize, [], []);
        var edge = empty.Fall(new(230, 600), 720, .08);
        Assert.AreEqual(AshaFallContact.Boundary, edge.Contact); Assert.IsFalse(empty.IsSafe(edge.Position));
    }

    [TestMethod]
    public void GravitySpeedCapIsIndependentOfTickPartition()
    {
        var map = new AshaMap(new(0, 0, 1000, 2000), new(96, 96), [], []);
        var one = map.Fall(new(300, 40), 670, .08);
        var half = map.Fall(new(300, 40), 670, .04); var two = map.Fall(half.Position, half.Speed, .04);
        Assert.AreEqual(one.Position.Y, two.Position.Y, .000001); Assert.AreEqual(720d, one.Speed);
    }

    [TestMethod]
    public void ReleaseOverPaintedUiFallsPastItInsteadOfTeleporting()
    {
        var map = new AshaMap(new(0, 0, 800, 700), new(96, 96),
            [new(200, 180, 300, 45)], [new(200, 500, 177, "overlapped"), new(0, 800, 550, "floor")]);
        Point p = new(230, 170); Assert.IsFalse(map.IsClear(p));
        double speed = 0;
        for (int i = 0; i < 150; i++)
        {
            var next = map.Fall(p, speed, .02);
            Assert.AreEqual(p.X, next.Position.X);
            Assert.IsGreaterThanOrEqualTo(p.Y, next.Position.Y);
            if (next.Contact == AshaFallContact.Ledge)
            { Assert.AreEqual("floor", map.Support(next.Position)?.Id); Assert.IsTrue(map.IsSafe(next.Position)); return; }
            Assert.AreEqual(AshaFallContact.Air, next.Contact);
            p = next.Position; speed = next.Speed;
        }
        Assert.Fail("Overlapping a control on release must not strand a character.");
    }

    [TestMethod]
    public void NarrowLedgeCannotCatchBothFeetAndDoesNotCancelTheFall()
    {
        var map = new AshaMap(new(0, 0, 800, 700), new(96, 96), [new(245, 303, 10, 10)],
            [new(245, 255, 300, "too-small"), new(0, 800, 550, "floor")]);
        Point p = new(210, 80);
        Assert.AreEqual(new Point(210, 550 - map.Feet), map.LandingBelow(p));
    }
}
