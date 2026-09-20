using System.Text.Json.Nodes;
using System.Xml.Linq;
using UnityBridgeDesk.Infrastructure.SpeedBench;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class SpeedChartOrderingTests
{
    private static SpeedRun RotatedRun()
    {
        var original = SpeedReportSample.Create();
        string[] tags = ["v0.2.3", "v0.2.1", "v0.2.2-rc.2"];
        var releases = tags.Select(t => original.Releases[0] with { Tag = t, Version = t }).ToArray();
        var options = new SpeedOptions(Repeats: 3, Seed: 42, Experiments: ["F01", "F02", "F03", "F04", "S01"]);
        var plan = SpeedProtocol.Schedule(options, tags);
        var results = plan.Select(t => original.Results[0] with
        {
            Trial = t,
            Guest = original.Results[0].Guest! with { TrialId = t.Id, WorkMs = 100 + Array.IndexOf(tags, t.Tag) * 20 }
        }).ToArray();
        return original with { Releases = releases, Options = options, Plan = plan, Results = results, Status = "completed" };
    }

    [TestMethod]
    public void EveryConditionUsesSavedReleaseOrderAndColorsDespiteRotatedMeasurements()
    {
        var run = RotatedRun(); var report = new SpeedReport(run);
        Assert.IsTrue(run.Plan.GroupBy(t => (t.Experiment, t.Variant)).Select(g => g.First().Tag).Distinct().Count() > 1,
            "The fixture must reproduce differing first versions across conditions.");
        var charts = SpeedCharts.Build(report);
        foreach (var chart in charts)
        {
            CollectionAssert.AreEqual(run.Releases.Select(r => r.Tag).ToArray(), chart.Series.Select(s => s.Release).ToArray());
            CollectionAssert.AreEqual(SpeedCharts.Colors.Take(3).ToArray(), chart.Series.Select(s => s.Color).ToArray());
            CollectionAssert.AreEqual(new double?[] { 100, 120, 140 }, chart.Series.Select(s => s.Mean).ToArray());
        }
        // Building a view must not reorder the measurement plan or redefine the baseline.
        Assert.AreSame(run, report.Run); Assert.AreEqual("v0.2.3", report.Baseline);
    }

    [TestMethod]
    public void MissingAndFailedResultsKeepTheirVersionSlotAndDoNotShiftColors()
    {
        var run = RotatedRun();
        var plan = run.Plan.Where(t => !(t.Experiment == "F01" && t.Variant == "first" && t.Tag == "v0.2.3")).ToArray();
        var results = run.Results.Where(r => plan.Any(t => t.Id == r.Trial.Id) && r.Trial.Tag != "v0.2.2-rc.2")
            .Select(r => r.Trial.Tag == "v0.2.1" ? r with { Status = "failed" } : r).ToArray();
        var chart = SpeedCharts.Build(new SpeedReport(run with { Plan = plan, Results = results })).First();
        CollectionAssert.AreEqual(run.Releases.Select(r => r.Tag).ToArray(), chart.Series.Select(s => s.Release).ToArray());
        CollectionAssert.AreEqual(SpeedCharts.Colors.Take(3).ToArray(), chart.Series.Select(s => s.Color).ToArray());
        Assert.IsTrue(chart.Series.All(s => s.Mean is null));
        Assert.AreEqual(0, chart.Series[0].Planned); Assert.IsNull(chart.Series[0].SuccessPercent);
        Assert.AreEqual(0d, chart.Series[1].SuccessPercent); Assert.IsNull(chart.Series[2].SuccessPercent);
    }

    [TestMethod]
    public void SvgUsesTheSameVersionAndColorOrderInEveryCondition()
    {
        var report = new SpeedReport(RotatedRun());
        string path = Path.Combine(SampleData.TestDirectory(), "ordered.svg"); SpeedCharts.WriteSvg(path, report);
        XNamespace svg = "http://www.w3.org/2000/svg"; var doc = XDocument.Load(path);
        var charts = SpeedCharts.Build(report);
        var labels = doc.Descendants(svg + "text").Where(t => report.Run.Releases.Any(r => r.Tag == t.Value)).Select(t => t.Value).ToArray();
        CollectionAssert.AreEqual(charts.SelectMany(c => report.Run.Releases.Select(r => r.Tag)).ToArray(), labels);
        var fills = doc.Descendants(svg + "rect").Select(r => (string?)r.Attribute("fill")).Where(c => SpeedCharts.Colors.Contains(c)).ToArray();
        CollectionAssert.AreEqual(charts.SelectMany(c => SpeedCharts.Colors.Take(3)).ToArray(), fills);
    }

    [TestMethod]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(9)]
    public async Task OldGraphReportsAreRegeneratedWithoutChangingOriginalFilesOrMeasurements(int oldFormat)
    {
        string root = SampleData.TestDirectory(); var report = new SpeedReport(RotatedRun());
        string old = await SpeedReportFiles.Export(root, report);
        string metadata = Path.Combine(old, ".report.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(metadata))!;
        manifest["FormatVersion"] = oldFormat; File.SetAttributes(metadata, FileAttributes.Normal);
        await File.WriteAllTextAsync(metadata, manifest.ToJsonString());
        string oldHash = await SpeedFiles.Hash(Path.Combine(old, SpeedReportFiles.ChartName));
        string updated = await SpeedReportFiles.Export(root, report);
        Assert.AreNotEqual(old, updated);
        Assert.AreEqual(oldHash, await SpeedFiles.Hash(Path.Combine(old, SpeedReportFiles.ChartName)));
        Assert.AreEqual(oldFormat, JsonNode.Parse(await File.ReadAllTextAsync(metadata))!["FormatVersion"]!.GetValue<int>());
        Assert.AreEqual(10, JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(updated, ".report.json")))!["FormatVersion"]!.GetValue<int>());
        Assert.AreEqual(updated, await SpeedReportFiles.Export(root, report));
    }
}
