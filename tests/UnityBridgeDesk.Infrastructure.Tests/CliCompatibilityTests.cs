using System.Text.Json;
using System.Xml.Linq;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Infrastructure.SpeedBench;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class CliCompatibilityTests
{
    // Usage excerpt verified against v0.1.3 official Windows x64 executable,
    // SHA256 83e80103c6dd5947831fed0913e942283f8c7eb10383ea55bdebe0b8165934b8.
    private const string LegacyHelp = """
        usage: unity-bridge [-h] [--project PROJECT] [--port PORT]
                            [--timeout-ms TIMEOUT_MS] [--instances-dir INSTANCES_DIR]
                            [--json]
                            {instances,status,tools,refresh,console,test,editor,menu,reserialize,profiler,screenshot,exec,call,wait-ready,update}
        """;
    [TestMethod]
    public void OldCliOmitsUnsupportedFlagButKeepsIdenticalTargetAndTimeout()
    {
        var old = CliCapabilities.Parse(LegacyHelp, "--code-file CODE_FILE, --file CODE_FILE");
        var modern = CliCapabilities.Parse(LegacyHelp + " [--no-update-check]", "--file CODE_FILE");
        Assert.IsFalse(old.NoUpdateCheck); Assert.IsTrue(modern.NoUpdateCheck);
        Assert.AreEqual("--file", old.ExecFileOption);
        string[] args = old.GlobalArguments("C:/project with spaces", 8090, "C:/instances", 180);
        Assert.DoesNotContain("--no-update-check", args);
        CollectionAssert.AreEqual(args, modern.GlobalArguments("C:/project with spaces", 8090, "C:/instances", 180).Where(a => a != "--no-update-check").ToArray());
        Assert.AreEqual("180000", args[^1]);
    }
    [TestMethod]
    public void CapabilityTokensAreExactAndMissingRequiredFeaturesFailBeforeMeasurement()
    {
        Assert.IsFalse(CliCapabilities.Parse(LegacyHelp + " --no-update-check-extra").NoUpdateCheck);
        Assert.AreEqual("--code-file", CliCapabilities.Parse(LegacyHelp, "--code-file PATH").ExecFileOption);
        Assert.AreEqual("bench-incompatible", Assert.ThrowsExactly<SpeedMeasurementException>(() =>
            CliCapabilities.Parse(LegacyHelp.Replace("--project", "--project-id"))).Kind);
        Assert.ThrowsExactly<SpeedMeasurementException>(() => CliCapabilities.Parse(LegacyHelp, "--code CODE"));
    }
    [TestMethod]
    public void OldArgumentFailureIsExplainedWithoutRewritingEvidenceOrCountingCommandFailure()
    {
        const string error = "CLI 종료 코드 2 / usage: unity-bridge\nunity-bridge: error: unrecognized arguments: --no-update-check";
        var source = SpeedReportSample.Create();
        var results = source.Results.Select(r => r.Trial.Tag == "vB" ? r with { Status = "failed", Error = error, FailureKind = "cli-error" } : r).ToArray();
        var run = source with { Results = results }; string before = JsonSerializer.Serialize(run, SpeedProtocol.Json);
        var report = new SpeedReport(run); var row = report.Stability.Single(s => s.Release == "vB");
        Assert.AreEqual(2, row.CompatibilityFailures); Assert.AreEqual(0, row.Evaluated); Assert.IsNull(row.SuccessPercent);
        Assert.AreEqual(0, row.OtherFailures); Assert.AreEqual(2, row.Finished);
        Assert.IsTrue(report.Trials.Where(t => t.Release == "vB" && t.Result is not null).All(t => t.FailureCategory == "벤치 호환성 오류"));
        Assert.Contains("성능 실패가 아닙니다", report.Issues());
        Assert.AreEqual(before, JsonSerializer.Serialize(run, SpeedProtocol.Json));
        var reply = new ProcessResult(ProcessOutcome.Exited, 2, "", error, 10, 123, DateTimeOffset.UtcNow);
        Assert.AreEqual("bench-incompatible", Assert.ThrowsExactly<SpeedMeasurementException>(() => SpeedFailure.ReadData(reply)).Kind);
    }
    [TestMethod]
    public void PreparationTimeoutAndCleanupFailureStayDistinct()
    {
        var run = SpeedReportSample.Create();
        var results = run.Results.Select(r => r.Status == "failed" ? r with { FailureKind = "timeout", FailureStage = "preparation" } : r).ToArray();
        var report = new SpeedReport(run with { Results = results });
        var a = report.Stability.Single(s => s.Release == "vA"); var b = report.Stability.Single(s => s.Release == "vB");
        Assert.AreEqual(1, b.EnvironmentFailures); Assert.AreEqual(0, b.Timeouts); Assert.AreEqual(100d, b.SuccessPercent);
        Assert.AreEqual(1, a.CleanupFailures); Assert.AreEqual(3, a.Evaluated);
        Assert.IsTrue(report.Trials.Single(t => t.Result?.FailureStage == "preparation").FailureCategory == "환경 준비 실패");
    }
    [TestMethod]
    public void SelectedSvgKeepsConditionUnitCountsAndVerticalGeometry()
    {
        var report = new SpeedReport(SpeedReportSample.Create()); var chart = SpeedCharts.Build(report).Single();
        string path = Path.Combine(SampleData.TestDirectory(), "selected.svg");
        SpeedCharts.WriteSvg(path, report, [chart], false);
        XNamespace ns = "http://www.w3.org/2000/svg"; var svg = XDocument.Load(path);
        string text = string.Join(" ", svg.Descendants(ns + "text").Select(e => e.Value));
        Assert.Contains("명령 1회당 평균 시간", text); Assert.Contains("사전 실행 1회 제외", text); Assert.Contains("유효 2/3회", text);
        var bars = svg.Descendants(ns + "rect").Where(e => SpeedCharts.Colors.Contains((string?)e.Attribute("fill"))).ToArray();
        Assert.AreEqual(2, bars.Length);
        Assert.AreEqual((double)bars[0].Attribute("width")!, (double)bars[1].Attribute("width")!);
        Assert.IsTrue((double)bars[0].Attribute("x")! < (double)bars[1].Attribute("x")!);
        Assert.IsTrue((double)bars[0].Attribute("height")! > (double)bars[1].Attribute("height")!);
        Assert.AreEqual(SpeedReport.Condition("F01", "prepared"), chart.Condition);
        Assert.Contains("사전 실행 0회 → 명령 1회", report.ProtocolDescription("F01", SpeedReport.Condition("F01", "first")));
        Assert.Contains("사전 실행 0회 제외", new SpeedReport(report.Run with { Options = report.Run.Options with { Warmups = 0 } }).ProtocolDescription(chart.Experiment, chart.Condition));
    }
}
