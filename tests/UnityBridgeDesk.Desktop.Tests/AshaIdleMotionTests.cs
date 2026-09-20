using System.Windows;
using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop.Tests;

[TestClass]
public sealed class AshaIdleMotionTests
{
    [TestMethod]
    public void IdleStartsWithBreathingAndAllowsOnlyOneSmallGestureAtATime()
    {
        var idle = new AshaIdleMotion(17); idle.Reset(0);
        var kinds = new HashSet<string>(); int still = 0;
        for (int i = 0; i < 12000; i++)
        {
            double now = i / 50d; var pose = idle.Sample(now);
            Assert.IsTrue(Math.Abs(pose.Breath) <= 1);
            bool ears = Math.Abs(pose.Ears) > .001, tail = Math.Abs(pose.Tail) > .001, gaze = Math.Abs(pose.GazeX) > .001;
            Assert.IsTrue((ears ? 1 : 0) + (tail ? 1 : 0) + (gaze ? 1 : 0) <= 1, "Do not stack idle reactions.");
            if (now < 2.8) Assert.IsFalse(ears || tail || gaze, "A brief stop should not immediately trigger a gesture.");
            if (ears) kinds.Add("ears"); if (tail) kinds.Add("tail"); if (gaze) kinds.Add("gaze");
            if (!ears && !tail && !gaze) still++;
        }
        Assert.AreEqual(3, kinds.Count); Assert.IsGreaterThan(6000, still, "Most idle time is quiet breathing.");
    }
    [TestMethod]
    public void BreathingMovesTheTorsoWithoutScalingHeadOrMovingFeet()
    {
        var pose = new AshaExpression(0, 0, 0, 0, 1);
        var chest = new Point(.5, .77); Assert.AreNotEqual(chest, AshaSprite.Deform(chest, 4, pose));
        foreach (Point anchor in new Point[] { new(.5, .48), new(.28, .05), new(.45, .96), new(.6, .96) })
            Assert.AreEqual(anchor, AshaSprite.Deform(anchor, 4, pose));
    }
    [TestMethod]
    public void ReenteringIdleDiscardsAnOldGestureWithoutAccumulatingReactions()
    {
        var idle = new AshaIdleMotion(9);
        for (int i = 0; i < 1000; i++) idle.Sample(i / 50d);
        idle.Reset(20);
        for (double now = 20; now < 22.7; now += .05)
        {
            var pose = idle.Sample(now);
            Assert.AreEqual(0d, pose.Ears); Assert.AreEqual(0d, pose.Tail); Assert.AreEqual(0d, pose.GazeX);
        }
    }
}
