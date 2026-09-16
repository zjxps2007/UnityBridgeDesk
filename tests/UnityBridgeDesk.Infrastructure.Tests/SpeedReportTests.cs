using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using UnityBridgeDesk.Infrastructure.SpeedBench;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class SpeedReportTests
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [TestMethod]
    public void SeparateTrialsFromCallsAndCompareOnlyCompleteBlocks()
    {
        var run = SpeedReportSample.Create(); var report = new SpeedReport(run);
        Assert.AreEqual(3, report.Success); Assert.AreEqual(1, report.Failed); Assert.AreEqual(1, report.CleanupFailed); Assert.AreEqual(1, report.NotRun);
        Assert.AreEqual(200d, report.Summaries.Single(s => s.Release == "vA").Mean);
        var pair = report.Comparisons.Single();
        Assert.AreEqual(1, pair.Valid); Assert.AreEqual(3, pair.Planned);
        Assert.AreEqual(100d, pair.BaselineMs); Assert.AreEqual(80d, pair.CandidateMs);
        Assert.AreEqual(20d, pair.ReductionPercent!.Value, .000001);
        Assert.Contains("시간 20.0% 감소", pair.Change);
        Assert.IsNull(report.Trials[3].WorkMs); Assert.IsNull(report.Trials[5].WorkMs);
        Assert.Contains("미수행 1", report.Memo()); Assert.Contains("공동 유효 1/3블록", report.Text());
        Assert.Contains("vB 버전이 vA 버전보다 호출당 평균 20.00 ms 빨랐다", report.Memo());
        Assert.Contains("vA 100.00 ms", report.Memo());
        Assert.DoesNotContain("vA 200.00 ms", report.Memo());
        Assert.Contains("2블록은 비교에서 제외", report.Memo());
        Assert.Contains("한 블록의 비교", report.Memo());
        Assert.Contains("cleanup-failed", JsonSerializer.Serialize(run));
    }

    [TestMethod]
    public void EmptyAndUnfinishedRecordsDoNotClaimSuccessOrZeroDuration()
    {
        var run = SpeedReportSample.Create();
        var report = new SpeedReport(run with { Results = [], Status = "running" });
        Assert.Contains("상태 확인 필요", report.State); Assert.AreEqual(6, report.NotRun);
        Assert.IsTrue(report.Summaries.All(s => s.Mean is null && s.Valid == 0));
        Assert.AreEqual("비교 불가", report.Comparisons.Single().Change);
        var empty = new SpeedReport(run with { Plan = [], Results = [], Releases = [] });
        Assert.Contains("실행 계획이 없다", empty.Memo()); Assert.HasCount(0, empty.Comparisons);
        Assert.Contains("비교할 수 없다", report.Memo()); Assert.DoesNotContain("빨랐다", report.Memo());
        var cancelled = new SpeedReport(run with { Results = [run.Results[0] with { Status = "cancelled" }], Status = "cancelled" });
        Assert.AreEqual(1, cancelled.Cancelled); Assert.AreEqual(0, cancelled.Valid); Assert.Contains("부분 결과", cancelled.State);
    }

    [TestMethod]
    [DataRow("F01", 80d, "vB 버전이 vA 버전보다 호출당 평균 20.00 ms 빨랐다", "vA 대비 완료 시간이 20.0% 짧았다")]
    [DataRow("F01", 150d, "vA 버전이 vB 버전보다 호출당 평균 50.00 ms 빨랐다", "vB 대비 완료 시간이 33.3% 짧았다")]
    [DataRow("F02", 80d, "vB 버전이 vA 버전보다 전체 작업당 평균 20.00 ms 빨랐다", "vA 대비 완료 시간이 20.0% 짧았다")]
    [DataRow("F03", 150d, "vA 버전이 vB 버전보다 호출당 평균 50.00 ms 빨랐다", "vB 대비 완료 시간이 33.3% 짧았다")]
    [DataRow("F01", 100d, "평균 완료 시간이 100.00 ms로 같았다", "공동 유효 1/1블록")]
    [DataRow("F01", 100.0001d, "차이는 0.01 ms 미만이었다", "표시 정밀도 내에서 빠른 버전을 구분하지 않는다")]
    public void NarrativeNamesFasterVersionAndUsesSlowerMeanAsPercentageDenominator(string experiment, double candidateMs, string expected, string basis)
    {
        var run = SpeedReportSample.Create();
        string variant = experiment == "F02" ? "10" : experiment == "F03" ? "1024" : "prepared";
        var plan = run.Plan.Take(2).Select(t => t with { Experiment = experiment, Variant = variant }).ToArray();
        var records = run.Results.Take(2).Select((r, i) => r with
        { Trial = plan[i], Guest = r.Guest! with { WorkMs = i == 0 ? 100 : candidateMs } }).ToArray();
        var report = new SpeedReport(run with { Plan = plan, Results = records, Status = "completed" });
        Assert.Contains(expected, report.Memo()); Assert.Contains(basis, report.Memo());
        Assert.Contains(expected, report.Text());
        if (candidateMs >= 100 && candidateMs < 100.005) Assert.DoesNotContain("빨랐다", report.Memo());
    }

    [TestMethod]
    public void DuplicateOrMismatchedRecordsAreRejectedAndNonfiniteTimesExcluded()
    {
        var run = SpeedReportSample.Create();
        Assert.ThrowsExactly<InvalidDataException>(() => new SpeedReport(run with { Results = [run.Results[0], run.Results[0]] }));
        Assert.ThrowsExactly<InvalidDataException>(() => new SpeedReport(run with { Results = [run.Results[0] with { Trial = run.Plan[0] with { Tag = "wrong" } }] }));
        var invalid = run.Results[0] with { Guest = run.Results[0].Guest! with { WorkMs = double.PositiveInfinity } };
        Assert.IsFalse(new SpeedReport(run with { Results = [invalid] }).Trials[0].Included);
    }

    [TestMethod]
    public async Task ExportPreservesEvidenceReusesSnapshotsAndProtectsUserEditedWorkbook()
    {
        string root = SampleData.TestDirectory(); var run = SpeedReportSample.Create();
        string evidence = Path.Combine(root, "speed", "local-runs", run.Id.ToString("N"), "run.json");
        await SpeedFiles.Write(evidence, run); string before = await SpeedFiles.Hash(evidence);
        var report = new SpeedReport(run);
        string first = await SpeedReportFiles.Export(root, report);
        Assert.AreEqual(first, await SpeedReportFiles.Export(root, report));
        CollectionAssert.AreEqual(Encoding.UTF8.GetPreamble(), (await File.ReadAllBytesAsync(Path.Combine(first, SpeedReportFiles.SummaryName)))[..3]);
        Assert.AreEqual(report.Text(), await File.ReadAllTextAsync(Path.Combine(first, SpeedReportFiles.SummaryName)));
        Assert.IsTrue((File.GetAttributes(Path.Combine(first, ".report.json")) & FileAttributes.Hidden) != 0);
        await File.WriteAllTextAsync(Path.Combine(first, SpeedReportFiles.WorkbookName), "user edits");
        string copy = await SpeedReportFiles.Export(root, report);
        Assert.AreNotEqual(first, copy); Assert.AreEqual(copy, await SpeedReportFiles.Export(root, report));
        Assert.AreEqual("user edits", await File.ReadAllTextAsync(Path.Combine(first, SpeedReportFiles.WorkbookName)));
        string metadata = Path.Combine(copy, ".report.json");
        var oldFormat = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(metadata))!;
        oldFormat["formatVersion"] = 1;
        File.SetAttributes(metadata, FileAttributes.Normal);
        await File.WriteAllTextAsync(metadata, oldFormat.ToJsonString());
        File.SetAttributes(metadata, FileAttributes.Hidden);
        string upgraded = await SpeedReportFiles.Export(root, report);
        Assert.AreNotEqual(copy, upgraded);
        Assert.AreEqual(upgraded, await SpeedReportFiles.Export(root, report));
        Assert.Contains("평균 20.00 ms 빨랐다", await File.ReadAllTextAsync(Path.Combine(upgraded, SpeedReportFiles.SummaryName)));
        Assert.IsTrue(Directory.Exists(copy));
        Assert.AreEqual(before, await SpeedFiles.Hash(evidence));
        Assert.HasCount(0, Directory.GetDirectories(Path.Combine(root, "speed", "reports"), ".writing-*"));
    }

    [TestMethod]
    public async Task WorkbookKeepsNumbersBlanksFiltersAndLiteralErrorText()
    {
        string root = SampleData.TestDirectory();
        string folder = await SpeedReportFiles.Export(root, new SpeedReport(SpeedReportSample.Create()));
        using var zip = ZipFile.OpenRead(Path.Combine(folder, SpeedReportFiles.WorkbookName));
        XDocument Read(string name) { using var stream = zip.GetEntry(name)!.Open(); return XDocument.Load(stream); }
        Assert.HasCount(5, Read("xl/workbook.xml").Descendants(S + "sheet").ToArray());
        for (int i = 1; i <= 5; i++)
        {
            var sheet = Read($"xl/worksheets/sheet{i}.xml");
            Assert.IsNotNull(sheet.Root!.Element(S + "autoFilter"));
            Assert.AreEqual("7", sheet.Descendants(S + "pane").Single().Attribute("ySplit")!.Value);
            Assert.IsFalse(sheet.Descendants(S + "c").Any(c => (string?)c.Attribute("t") is "e" or "str"));
        }
        var trials = Read("xl/worksheets/sheet3.xml");
        XElement Cell(string address) => trials.Descendants(S + "c").Single(c => (string?)c.Attribute("r") == address);
        Assert.AreEqual("100", Cell("H8").Element(S + "v")!.Value);
        Assert.IsNull(Cell("H11").Element(S + "v"));
        Assert.IsNull(Cell("K11").Element(S + "f")); Assert.Contains("=HYPERLINK", Cell("K11").Value);
        Assert.AreEqual("inlineStr", (string?)Cell("K11").Attribute("t"));
        Assert.AreEqual(9, Read("xl/worksheets/sheet4.xml").Descendants(S + "row").Count() - 7);
    }

    [TestMethod]
    public async Task CancellationAndBlockedOutputDoNotChangeRunStateOrLeavePartialReports()
    {
        string root = SampleData.TestDirectory(); var report = new SpeedReport(SpeedReportSample.Create());
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => SpeedReportFiles.Export(root, report, stopped.Token));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "speed", "reports")));
        Directory.CreateDirectory(Path.Combine(root, "speed")); await File.WriteAllTextAsync(Path.Combine(root, "speed", "reports"), "block directory");
        await Assert.ThrowsExactlyAsync<IOException>(() => SpeedReportFiles.Export(root, report));
        Assert.AreEqual("cleanup-failed", report.Run.Status);
    }
}
