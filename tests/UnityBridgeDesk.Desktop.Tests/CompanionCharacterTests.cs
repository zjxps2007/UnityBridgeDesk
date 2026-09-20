using System.Text.Json;
using System.Windows;
using UnityBridgeDesk.Desktop.Controls;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop.Tests;

[TestClass]
public sealed class CompanionCharacterTests
{
    [TestMethod]
    public void OldSettingsKeepTheCatAndDoNotSilentlySummonAnotherCharacter()
    {
        var old = JsonSerializer.Deserialize<LocalSpeedSettings>("{\"ShowCompanion\":false,\"CompanionActivity\":2}")!;
        Assert.IsFalse(old.ShowCompanion); Assert.IsFalse(old.ShowFoxCompanion);
        Assert.AreEqual(2, old.CompanionActivity); Assert.IsTrue(old.FoxRoaming);
        var both = old with { ShowCompanion = true, ShowFoxCompanion = true, FoxRoaming = false, FoxActivity = 0 };
        Assert.AreEqual(both, JsonSerializer.Deserialize<LocalSpeedSettings>(JsonSerializer.Serialize(both)));
    }

    [TestMethod]
    public void CatClimbsWhileFoxChoosesLowerPatrolWithSameMapAndSeed()
    {
        var map = new AshaMap(new(0, 0, 700, 600), new(40, 40), [],
            [new(0, 680, 160, "high"), new(0, 680, 310, "start"), new(0, 680, 460, "low")]);
        var start = map.Nearest(new(300, 274.4))!.Value;
        double catHeight = 0, foxHeight = 0;
        for (int seed = 0; seed < 12; seed++)
        {
            var cat = new AshaExplorer(seed, CompanionKind.Cat).Choose(map, start);
            var fox = new AshaExplorer(seed, CompanionKind.Fox).Choose(map, start);
            Assert.IsTrue(cat.All(map.CanFollow)); Assert.IsTrue(fox.All(map.CanFollow));
            catHeight += cat[^1].To.Y; foxHeight += fox[^1].To.Y;
        }
        Assert.IsGreaterThan(catHeight, foxHeight, "Fox destinations should sit below the cat's with identical available geometry.");
        Assert.IsGreaterThan(CompanionCharacter.Speed(CompanionKind.Cat, 1), CompanionCharacter.Speed(CompanionKind.Fox, 1));
    }

    [TestMethod]
    public void AReservedCompanionPositionIsNotChosenAsDestination()
    {
        var map = new AshaMap(new(0, 0, 700, 400), new(40, 40), [], [new(0, 680, 250, "floor")]);
        Point start = map.Positions[0]; var explorer = new AshaExplorer(14, CompanionKind.Fox);
        var route = explorer.Choose(map, start, p => p.X < 220);
        Assert.IsNotEmpty(route); Assert.IsLessThan(220d, route[^1].To.X);
        Assert.IsEmpty(explorer.Choose(map, start, _ => false));
    }
}
