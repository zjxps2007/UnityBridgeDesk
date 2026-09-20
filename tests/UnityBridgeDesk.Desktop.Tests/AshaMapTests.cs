using System.Windows;
using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop.Tests;

[TestClass]
public sealed class AshaMapTests
{
    private static readonly Size Sprite = new(40, 40);
    [TestMethod]
    public void EmptySpaceIsNotGroundWithoutAnExplicitLedge()
    {
        var map = new AshaMap(new Rect(0, 0, 800, 600), Sprite, [], []);
        Assert.IsTrue(map.IsClear(new Point(100, 100)));
        Assert.IsFalse(map.IsSupported(new Point(100, 100)));
        Assert.IsNull(map.Nearest(new Point(100, 100)));
    }
    [TestMethod]
    public void FeetMustRestOnTheSpecifiedSurface()
    {
        var map = new AshaMap(new Rect(0, 0, 800, 600), Sprite, [], [new(100, 300, 400)]);
        var grounded = new Point(150, 400 - map.Feet);
        Assert.IsTrue(map.IsSafe(grounded));
        Assert.IsFalse(map.IsSupported(grounded - new Vector(0, 10)));
        Assert.IsFalse(map.IsSupported(new Point(320, grounded.Y)));
    }
    [TestMethod]
    public void HigherLedgeUsesIntermediateFootholdsAndVerifiedArcs()
    {
        var map = new AshaMap(new Rect(0, 0, 800, 600), Sprite, [], [new(0, 240, 500), new(200, 440, 350), new(380, 720, 200)]);
        Point from = map.Nearest(new(80, 465))!.Value, goal = map.Nearest(new(600, 165))!.Value;
        Assert.IsNull(map.Connection(from, goal), "A cat cannot jump directly over an excessive height.");
        var route = map.Route(from, goal);
        Assert.IsGreaterThanOrEqualTo(2, route.Count(x => x.IsJump));
        foreach (var step in route)
        {
            Assert.IsTrue(map.IsSafe(step.From)); Assert.IsTrue(map.IsSafe(step.To)); Assert.IsTrue(map.CanFollow(step));
            for (int i = 0; i <= 100; i++) Assert.IsTrue(map.IsClear(step.At(i / 100d)));
        }
        Assert.AreEqual(goal, route[^1].To);
    }
    [TestMethod]
    public void WalkingCannotCrossAnUnsupportedGap()
    {
        var map = new AshaMap(new Rect(0, 0, 800, 600), Sprite, [], [new(0, 150, 400), new(230, 400, 400)]);
        Point from = new(80, 400 - map.Feet), to = new(260, from.Y);
        Assert.IsFalse(map.CanFollow(new(from, to, 0)));
        Assert.IsTrue(map.Connection(from, to) is { IsJump: true });
    }
    [TestMethod]
    public void ThinObstaclesBlockTheWholeJumpNotJustItsEndpoints()
    {
        var map = new AshaMap(new Rect(0, 0, 800, 600), Sprite, [new Rect(180, 100, 2, 450)], [new(0, 150, 500), new(230, 400, 370)]);
        Point from = new(70, 500 - map.Feet), to = new(260, 370 - map.Feet);
        Assert.IsTrue(map.IsSafe(from)); Assert.IsTrue(map.IsSafe(to));
        Assert.IsNull(map.Connection(from, to)); Assert.IsEmpty(map.Route(from, to));
    }
    [TestMethod]
    public void JumpCannotLeaveTheWindowAboveItsCeiling()
    {
        var map = new AshaMap(new Rect(0, 0, 800, 600), Sprite, [], [new(0, 150, 42), new(230, 400, 42)]);
        Point from = new(80, 42 - map.Feet), to = new(260, from.Y);
        Assert.IsTrue(map.IsSafe(from)); Assert.IsTrue(map.IsSafe(to)); Assert.IsNull(map.Connection(from, to));
    }
    [TestMethod]
    public void DownwardStepAcceleratesAndCanLeaveANearCeilingPerch()
    {
        var map = new AshaMap(new Rect(0, 0, 800, 600), Sprite, [], [new(0, 150, 42), new(230, 400, 200)]);
        Point from = new(80, 42 - map.Feet), to = new(260, 200 - map.Feet);
        var step = map.Connection(from, to);
        Assert.IsNotNull(step); Assert.IsTrue(step.Value.IsDrop); Assert.IsFalse(step.Value.IsJump);
        Assert.AreEqual(from, step.Value.At(0)); Assert.AreEqual(to, step.Value.At(1));
        Assert.IsTrue(step.Value.At(.75).Y - step.Value.At(.5).Y > step.Value.At(.5).Y - step.Value.At(.25).Y);
        Assert.IsTrue(map.CanFollow(step.Value));
        Assert.IsTrue(step.Value.At(.25).Y >= from.Y, "A clear descent does not require an upward hop into the ceiling.");
    }
    [TestMethod]
    public void DropClearsTheTakeOffTextBeforeDescending()
    {
        var map = new AshaMap(new Rect(0, 0, 800, 600), Sprite, [new(100, 150, 140, 24)], [new(100, 240, 149), new(280, 450, 299)]);
        Point from = new(200, 149 - map.Feet), to = new(310, 299 - map.Feet);
        Assert.IsFalse(map.CanFollow(new(from, to, 0, IsDrop: true)), "A direct fall would pass through the title under its feet.");
        var step = map.Connection(from, to);
        Assert.IsTrue(step is { IsDrop: true }); Assert.IsTrue(map.CanFollow(step!.Value));
        for (int i = 0; i <= 100; i++) Assert.IsTrue(map.IsClear(step.Value.At(i / 100d)));
    }
    [TestMethod]
    public void DropRejectsBlockedPathsAndExcessiveHeights()
    {
        var map = new AshaMap(new Rect(0, 0, 800, 600), Sprite, [new(180, 0, 2, 590)], [new(0, 150, 150), new(230, 400, 300), new(0, 150, 500)]);
        Point from = new(80, 150 - map.Feet);
        Assert.IsNull(map.Connection(from, new(260, 300 - map.Feet)));
        Assert.IsNull(map.Connection(from, new(80, 500 - map.Feet)));
        Assert.IsFalse(map.CanFollow(new(from, new(260, 450 - map.Feet), 0, IsDrop: true)), "A drop needs an actual landing surface.");
    }
    [TestMethod]
    public void OccupiedOrTooNarrowScreensHaveNoUnsafeFallback()
    {
        var small = new AshaMap(new Rect(0, 0, 30, 30), Sprite, [], [new(0, 30, 29)]);
        Assert.IsNull(small.Nearest(new()));
        var full = new AshaMap(new Rect(0, 0, 400, 300), Sprite, [new(0, 0, 400, 300)], [new(0, 400, 280)]);
        Assert.IsNull(full.Nearest(new()));
    }
    [TestMethod]
    public void RemovedLedgeCannotRemainAStandingPosition()
    {
        var before = new AshaMap(new Rect(0, 0, 600, 600), Sprite, [], [new(0, 600, 500), new(100, 300, 300)]);
        Point perched = new(150, 300 - before.Feet);
        Assert.IsTrue(before.IsSafe(perched));
        var after = new AshaMap(before.Bounds, Sprite, [], [new(0, 600, 500)]);
        Assert.IsFalse(after.IsSafe(perched)); Assert.IsTrue(after.IsSafe(after.Nearest(perched)!.Value));
    }
}
