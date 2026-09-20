using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop.Tests;

[TestClass]
public sealed class CharacterMotionTests
{
    [TestMethod]
    public void LandingStartsAtContactAbsorbsImpactThenSettles()
    {
        foreach (double impact in new[] { .1, .5, 1 })
        {
            Assert.AreEqual(0d, CharacterMotion.Landing(0, impact));
            Assert.AreEqual(1d, CharacterMotion.Landing(.065, impact), .000001);
            Assert.AreEqual(0d, CharacterMotion.Landing(1, impact));
            double previous = 0;
            for (double t = 0; t < .065; t += .001)
            {
                double value = CharacterMotion.Landing(t, impact);
                Assert.IsTrue(value >= previous && value <= 1);
                previous = value;
            }
            Assert.IsTrue(CharacterMotion.Landing(.001, impact) < .001);
        }
    }
    [TestMethod]
    public void WalkRampPreservesDistanceAndHasNoVelocityJumpAtEitherEnd()
    {
        foreach (double length in new[] { 1d, 39, 200 })
        foreach (double speed in new[] { 45d, 85, 120 })
        {
            var walk = new AshaWalk(length, speed, true, true);
            Assert.AreEqual(length, walk.Distance(walk.Duration), .000001);
            Assert.IsTrue(walk.Distance(.0001) / .0001 < .1);
            Assert.IsTrue((length - walk.Distance(walk.Duration - .0001)) / .0001 < .1);
            double previous = 0;
            for (double t = 0; t < walk.Duration; t += .005)
            {
                double value = walk.Distance(t);
                Assert.IsTrue(value >= previous && value <= length);
                previous = value;
            }
        }
    }
    [TestMethod]
    public void IdleWeightShiftsAreQuietAndNeverAppliedOnNarrowPerches()
    {
        foreach (var kind in new[] { CompanionKind.Cat, CompanionKind.Fox })
        {
            var wide = new AshaIdleMotion(9, kind);
            var narrow = new AshaIdleMotion(9, kind);
            bool shifted = false, stretched = false;
            for (double t = 0; t < 150; t += .05)
            {
                var a = wide.Sample(t); var b = narrow.Sample(t, true);
                shifted |= Math.Abs(a.Weight) > .2; stretched |= a.Stretch > .2;
                Assert.AreEqual(0d, b.Weight); Assert.AreEqual(0d, b.Stretch);
                Assert.IsTrue(Math.Abs(a.Weight) <= 1 && Math.Abs(a.Breath) <= 1);
            }
            Assert.IsTrue(shifted && stretched);
        }
    }
}
