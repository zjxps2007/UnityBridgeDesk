using System.Windows;
using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop.Tests;

[TestClass]
public sealed class AshaBehaviorTests
{
    [TestMethod]
    public void FoxStrideUsesEightPosesAtTheSameRelativeTravelForEachSize()
    {
        foreach (double size in new[] { 96d, 112, 144 })
        {
            double phase = 0; var frames = new HashSet<int>();
            for (int i = 0; i < 32; i++)
            {
                phase = FoxGait.Advance(phase, 6 * size / 96, size); frames.Add(FoxGait.Frame(phase));
                Assert.AreEqual(phase, FoxGait.Advance(phase, 0, size));
            }
            Assert.HasCount(8, frames); Assert.AreEqual(0d, phase, .00001);
        }
    }
    [TestMethod]
    public void FeetFollowDistanceAtEveryActivityAndFrameRate()
    {
        foreach (double speed in new[] { 45d, 65d, 85d })
        foreach (double interval in new[] { .016, .04, .08 })
        {
            var walk = new AshaWalk(195, speed, true, true);
            double at = 0, previous = 0, accumulated = 0;
            var frames = new HashSet<int>();
            while (at < walk.Duration)
            {
                at = Math.Min(walk.Duration, at + interval); double distance = walk.Distance(at);
                Assert.IsTrue(distance >= previous && distance <= walk.Length);
                accumulated += distance - previous; previous = distance; frames.Add(AshaWalk.Frame(accumulated));
            }
            Assert.AreEqual(195d, accumulated, .00001); Assert.AreEqual(0, AshaWalk.Frame(accumulated));
            Assert.HasCount(4, frames);
            Assert.IsLessThan(speed * .02, walk.Distance(.02));
            Assert.IsLessThan(speed * .02, walk.Length - walk.Distance(walk.Duration - .02));
        }
    }
    [TestMethod]
    public void ContinuingWalkHasNoStopAtInternalNodes()
    {
        var first = new AshaWalk(60, 65, true, false); var second = new AshaWalk(80, 65, false, true);
        double before = (first.Length - first.Distance(first.Duration - .001)) / .001;
        double after = second.Distance(.001) / .001;
        Assert.AreEqual(65d, before, .00001); Assert.AreEqual(before, after, .00001);
        var tiny = new AshaWalk(1, 85, true, true);
        Assert.AreEqual(1d, tiny.Distance(tiny.Duration)); Assert.AreEqual(0d, tiny.Distance(-1));
    }
    [TestMethod]
    public void AttentionEscalatesWithoutQueuingAndResetsAfterAQuietGap()
    {
        var input = new AshaInteraction();
        Assert.AreEqual(AshaAction.Glance, input.Tap(0)); Assert.IsNull(input.Tap(.1));
        Assert.AreEqual(AshaAction.Play, input.Tap(.4)); Assert.AreEqual(AshaAction.Dismiss, input.Tap(.8));
        for (int i = 1; i < 15; i++) Assert.IsNull(input.Tap(.8 + i * .1));
        Assert.AreEqual(AshaAction.Glance, input.Tap(7));
    }
    [TestMethod]
    public void PerchMemoryFollowsTheSameStructureAfterLayoutChanges()
    {
        var old = new AshaMap(new(0, 0, 800, 600), new(96, 96), [], [new(100, 400, 200, "heading")]);
        var point = old.Nearest(new(210, 115))!.Value;
        var bookmark = old.Remember(point, true)!.Value;
        var moved = new AshaMap(new(0, 0, 800, 600), new(96, 96), [], [new(350, 700, 350, "heading"), new(100, 400, 200, "different")]);
        var restored = moved.Restore(bookmark)!.Value;
        Assert.IsTrue(moved.IsSafe(restored)); Assert.AreEqual("heading", moved.Support(restored)!.Value.Id);
        Assert.IsGreaterThan(point.X + 150, restored.X); Assert.IsTrue(bookmark.FaceLeft);
        var missing = new AshaMap(new(0, 0, 800, 600), new(96, 96), [], [new(0, 800, 550, "fallback")]);
        Assert.IsTrue(missing.IsSafe(missing.Restore(bookmark)!.Value));
        var blocked = new AshaMap(new(0, 0, 800, 600), new(96, 96), [new(0, 0, 800, 600)], [new(100, 400, 200, "heading")]);
        Assert.IsNull(blocked.Restore(bookmark));
    }
    [TestMethod]
    public void NarrowPerchesSuppressStretchWhileWidePerchesAllowIt()
    {
        var narrow = new AshaIdleMotion(7); var wide = new AshaIdleMotion(7); bool stretched = false;
        for (double now = 0; now < 180; now += .1)
        {
            var a = narrow.Sample(now, true); var b = wide.Sample(now, false);
            Assert.AreEqual(0d, a.Stretch); Assert.AreEqual(1d, a.Paws);
            Assert.AreEqual(0d, b.Paws); stretched |= b.Stretch > .2;
            Assert.AreEqual(.96, AshaSprite.Deform(new(.5, .96), 4, a).Y);
        }
        Assert.IsTrue(stretched);
    }
}
