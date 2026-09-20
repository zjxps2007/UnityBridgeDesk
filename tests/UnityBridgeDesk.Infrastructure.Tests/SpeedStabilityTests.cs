using System.Text.Json;
using System.Xml.Linq;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Infrastructure.SpeedBench;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class SpeedStabilityTests
{
    [TestMethod]
    public void ExecAndEachCommandReceiveSeparateBalancedProjectTrials()
    {
        var options = new SpeedOptions(Repeats: 2, Experiments: ["F04", "S01"]);
        var plan = SpeedProtocol.Schedule(options, ["a", "b"]);
        Assert.AreEqual(28, plan.Length);
        Assert.AreEqual(28, plan.Select(t => t.Id).Distinct().Count());
        foreach (var condition in plan.GroupBy(t => (t.Experiment, t.Variant)))
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, condition.GroupBy(t => t.Block).Select(g => g.First().Tag).ToArray());
        Assert.ThrowsExactly<ArgumentException>(() => new SpeedOptions(StressRequests: 2, StressConcurrency: 4).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => SpeedStress.ValidateCommands([null!]));
        Assert.ThrowsExactly<ArgumentException>(() => SpeedStress.ValidateCommands([new("x", "exec", "[]")]));
        Assert.ThrowsExactly<ArgumentException>(() => SpeedStress.ValidateCommands([new("x", "exec", "{}"), new("x", "console", "{}")]));
    }

    [TestMethod]
    public async Task ExecSourceRemainsIdenticalAcrossNoncesAndUsesFileInput()
    {
        string root = SampleData.TestDirectory();
        await SpeedExec.WriteSource(root, CancellationToken.None);
        string path = Path.Combine(root, SpeedExec.SourceName);
        Assert.AreEqual(SpeedExec.Sha256, await SpeedFiles.Hash(path));
        await SpeedExec.WriteNonce(root, "first", CancellationToken.None);
        await SpeedExec.WriteNonce(root, "second", CancellationToken.None);
        Assert.AreEqual(SpeedExec.Sha256, await SpeedFiles.Hash(path));
        CollectionAssert.AreEqual(new[] { "exec", "--file", path }, SpeedExec.Arguments(root));
        Assert.AreEqual("second", await File.ReadAllTextAsync(Path.Combine(root, SpeedExec.NonceName)));
    }

    private static ProcessResult Reply(string output, int exit = 0, ProcessOutcome outcome = ProcessOutcome.Exited) =>
        new(outcome, exit, output, "", 10, 123, DateTimeOffset.UtcNow);

    [TestMethod]
    [DataRow("Compile error: invalid syntax", "compile-error")]
    [DataRow("Runtime error: boom", "runtime-error")]
    [DataRow("Compiler timed out after 30 seconds", "timeout")]
    [DataRow("Unknown command: absent", "unsupported-command")]
    public void StructuredFailuresKeepTheirMeaningEvenWithNonzeroExit(string message, string kind)
    {
        var error = Assert.ThrowsExactly<SpeedMeasurementException>(() => SpeedFailure.ReadData(
            Reply(JsonSerializer.Serialize(new { success = false, message }), 1)));
        Assert.AreEqual(kind, error.Kind);
    }

    [TestMethod]
    [DataRow(true, 1)]
    [DataRow(false, 1)]
    [DataRow(false, 0)]
    public void CliDiscoveryEnvelopeIsNotMistakenForAnEmptyJsonReply(bool stdout, int exit)
    {
        string envelope = JsonSerializer.Serialize(new { ok = false, error = "no active instance on port 8090" });
        var reply = Reply(stdout ? envelope : "", exit) with { Error = stdout ? "" : envelope };
        var error = Assert.ThrowsExactly<SpeedMeasurementException>(() => SpeedFailure.ReadData(reply));
        Assert.AreEqual("instance-unavailable", error.Kind);
        Assert.Contains("8090", error.Message);
        Assert.DoesNotContain("JSON", error.Message);
        Assert.DoesNotContain("tokens", error.Message);
        Assert.AreEqual("Unity 연결 검색 실패", SpeedFailure.Label(error.Kind));
    }

    [TestMethod]
    public void StderrIsDiagnosticOnlyAndOtherExitOutcomesKeepTheirMeaning()
    {
        string envelope = JsonSerializer.Serialize(new { ok = false, error = "connection refused" });
        Assert.AreEqual("connection refused", Assert.ThrowsExactly<SpeedMeasurementException>(() =>
            SpeedFailure.ReadData(Reply("", 1) with { Error = envelope })).Message);
        Assert.AreEqual("timeout", Assert.ThrowsExactly<SpeedMeasurementException>(() =>
            SpeedFailure.ReadData(Reply("", 1, ProcessOutcome.TimedOut) with { Error = envelope })).Kind);
        string success = "{\"success\":true,\"data\":42}";
        Assert.AreEqual("invalid-response", Assert.ThrowsExactly<SpeedMeasurementException>(() =>
            SpeedFailure.ReadData(Reply("") with { Error = success })).Kind);
        Assert.AreEqual(42, SpeedFailure.ReadData(Reply(success) with { Error = "diagnostic warning" }).GetInt32());
        Assert.AreEqual("cli-error", Assert.ThrowsExactly<SpeedMeasurementException>(() =>
            SpeedFailure.ReadData(Reply("", 1) with { Error = "plain stderr error" })).Kind);
        Assert.AreEqual("invalid-response", Assert.ThrowsExactly<SpeedMeasurementException>(() =>
            SpeedFailure.ReadData(Reply("not json"))).Kind);
    }

    [TestMethod]
    public void ResponseValidationRejectsForeignTargetsMalformedRepliesAndConflictingExitCode()
    {
        string project = SampleData.TestDirectory();
        var reply = Reply(JsonSerializer.Serialize(new { success = true, data = new { nonce = "n", pid = 123, projectPath = project, value = 42 } }));
        Assert.AreEqual(42, SpeedFailure.ReadValue(reply, "n", 123, project).GetInt32());
        Assert.AreEqual("response-mismatch", Assert.ThrowsExactly<SpeedMeasurementException>(() => SpeedFailure.ReadValue(reply, "other", 123, project)).Kind);
        Assert.AreEqual("response-mismatch", Assert.ThrowsExactly<SpeedMeasurementException>(() => SpeedFailure.ReadValue(reply, "n", 124, project)).Kind);
        Assert.AreEqual("invalid-response", Assert.ThrowsExactly<SpeedMeasurementException>(() => SpeedFailure.ReadData(Reply("{}"))).Kind);
        Assert.AreEqual("cli-error", Assert.ThrowsExactly<SpeedMeasurementException>(() => SpeedFailure.ReadData(reply with { ExitCode = 1 })).Kind);
        Assert.AreEqual("timeout", Assert.ThrowsExactly<SpeedMeasurementException>(() => SpeedFailure.ReadData(Reply("", outcome: ProcessOutcome.TimedOut))).Kind);
    }

    [TestMethod]
    public void ExpectedObjectsMatchRequiredFieldsButArraysAndValuesMustMatchExactly()
    {
        var actual = JsonSerializer.SerializeToElement(new { nested = new { count = 42, extra = true }, list = new[] { 1, 2 } });
        Assert.IsTrue(SpeedStress.Matches(actual, JsonSerializer.SerializeToElement(new { nested = new { count = 42 } })));
        Assert.IsFalse(SpeedStress.Matches(actual, JsonSerializer.SerializeToElement(new { nested = new { count = 43 } })));
        Assert.IsFalse(SpeedStress.Matches(actual, JsonSerializer.SerializeToElement(new { list = new[] { 1 } })));
    }

    [TestMethod]
    public async Task LoadHonorsConcurrencyAndSubmitsEveryRequestExactlyOnce()
    {
        int active = 0, peak = 0, entered = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = await SpeedStress.Run(12, 3, async (index, offset, ct) =>
        {
            int now = Interlocked.Increment(ref active);
            lock (gate) peak = Math.Max(peak, now);
            if (Interlocked.Increment(ref entered) == 3) gate.SetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await Task.Yield();
            Interlocked.Decrement(ref active);
            return new(index, 10, 1, "success", OffsetMs: offset);
        }, CancellationToken.None);
        Assert.AreEqual(3, peak); Assert.AreEqual(0, active);
        CollectionAssert.AreEqual(Enumerable.Range(0, 12).ToArray(), batch.Samples.Select(s => s.Index).ToArray());
        Assert.IsNull(batch.Error); Assert.IsTrue(batch.ElapsedMs > 0);
    }

    [TestMethod]
    public async Task TimeoutStopsNewRequestsAndPreservesAlreadySubmittedResults()
    {
        int started = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = SpeedStress.Run(100, 2, async (index, offset, ct) =>
        {
            if (Interlocked.Increment(ref started) == 2) both.SetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            return new(index, 100, 0, "failed", "timeout", offset, "timed out");
        }, CancellationToken.None);
        await both.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var batch = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, started); Assert.HasCount(2, batch.Samples);
        Assert.AreEqual("timeout", batch.FailureKind); Assert.AreEqual("failed", batch.Samples[1].Outcome);
    }

    [TestMethod]
    public async Task CancellationKeepsCompletedSamplesAndDoesNotFillMissingWorkWithZeroes()
    {
        using var cancel = new CancellationTokenSource();
        var batch = await SpeedStress.Run(10, 1, (index, offset, ct) =>
        {
            cancel.Cancel();
            return Task.FromResult(new SpeedSample(index, 10, 1, "success", OffsetMs: offset));
        }, cancel.Token);
        Assert.HasCount(1, batch.Samples); Assert.AreEqual(10d, batch.Samples[0].Milliseconds);
    }

    [TestMethod]
    public void StabilityUsesTrialDenominatorsAndKeepsLegacyUnknownSeparate()
    {
        var original = SpeedReportSample.Create();
        var report = new SpeedReport(original);
        var a = report.Stability.Single(s => s.Release == "vA");
        var b = report.Stability.Single(s => s.Release == "vB");
        Assert.AreEqual(3, a.Finished); Assert.AreEqual(2, a.Valid); Assert.AreEqual(1, a.CleanupFailures);
        Assert.AreEqual(200d, (a.MinimumMs + a.MaximumMs) / 2);
        Assert.AreEqual(Math.Sqrt(20000), a.StandardDeviationMs!.Value, .00001);
        Assert.AreEqual(5, a.CallsUnknown); Assert.IsNull(a.CallP95Ms);
        Assert.AreEqual(1, b.UnknownFailures); Assert.AreEqual(1, b.NotRun); Assert.AreEqual(50d, b.SuccessPercent);
        var cancelled = new SpeedReport(original with { Results = [original.Results[0] with { Status = "cancelled" }] });
        var c = cancelled.Stability.Single(s => s.Release == "vA");
        Assert.AreEqual(0, c.Finished); Assert.AreEqual(1, c.Cancelled); Assert.IsNull(c.SuccessPercent);
    }

    [TestMethod]
    public void CallPercentileExcludesFailedCallsButPreservesTheirCount()
    {
        var original = SpeedReportSample.Create(); var result = original.Results[0];
        var samples = Enumerable.Range(1, 20).Select(i => new SpeedSample(i - 1, i, 1, "success")).Append(
            new(20, 9999, 0, "failed", "timeout")).ToArray();
        var report = new SpeedReport(original with { Results = [result with { Status = "failed", FailureKind = "timeout",
            Guest = result.Guest! with { Samples = samples, Status = "failed", WorkMs = null } }] });
        var a = report.Stability.Single(s => s.Release == "vA");
        Assert.AreEqual(19d, a.CallP95Ms); Assert.AreEqual(20, a.CallsVerified); Assert.AreEqual(1, a.CallsFailed);
        Assert.AreEqual(1, a.Timeouts); Assert.IsNull(a.MinimumMs);
    }

    [TestMethod]
    public void SvgKeepsAbsentValuesMissingAndEscapesLabels()
    {
        var run = SpeedReportSample.Create();
        var report = new SpeedReport(run with { Results = [] });
        Assert.IsTrue(SpeedCharts.Build(report).Single().Series.All(s => s.Mean is null && s.SuccessPercent is null));
        string path = Path.Combine(SampleData.TestDirectory(), "chart.svg");
        SpeedCharts.WriteSvg(path, report);
        var doc = XDocument.Load(path);
        Assert.Contains("측정 없음", doc.ToString());
        Assert.AreEqual(1, doc.Descendants(XName.Get("rect", "http://www.w3.org/2000/svg")).Count());
    }

    [TestMethod]
    public void ThroughputAveragesPerTrialRatesRatherThanPoolingCalls()
    {
        var run = SpeedReportSample.Create();
        var plan = run.Plan.Select(t => t with { Experiment = "S01", Variant = "echo" }).ToArray();
        var results = run.Results.Take(3).Select((r, i) => r with { Trial = plan[i], Guest = r.Guest! with
        { Samples = [new(0, 1, 1, "success"), new(1, 1, 1, "success")], MeasurementMs = i == 0 ? 10 : 20 } }).ToArray();
        var report = new SpeedReport(run with { Plan = plan, Results = results });
        Assert.AreEqual(150d, report.Stability.Single(s => s.Release == "vA").RequestsPerSecond);
    }

    [TestMethod]
    public void GuestValidationRequiresCompleteVerifiedLoadAndMatchingExecSource()
    {
        var options = new SpeedOptions(Experiments: ["S01"], StressRequests: 2, StressConcurrency: 2);
        var trial = SpeedProtocol.Schedule(options, ["a", "b"])[0];
        var request = new GuestRequest(Guid.NewGuid(), trial, options, "6000.5.4f1", "", "", "cli", "connector", "0.2.3", "fixture", "nonce");
        var result = new GuestResult(request.RunId, trial.Id, "nonce", LocalWorkspace.Schema, "success", null, 10, 10, 1,
            [new(0, 10, 1, "success", OffsetMs: 0), new(1, 10, 1, "success", OffsetMs: 1)],
            123, "fixture", "6000.5.4f1_revision", "cli", "connector", "fixture", 10000000, [],
            ReportedConnectorVersion: "0.2.3", MeasurementMs: 20);
        SpeedCoordinator.ValidateResult(request, result, LocalWorkspace.Schema);
        SpeedCoordinator.ValidateResult(request, result with { WarmupMilliseconds = [] }, LocalWorkspace.Schema);
        Assert.ThrowsExactly<IOException>(() => SpeedCoordinator.ValidateResult(request, result with { WarmupMilliseconds = [double.NaN] }, LocalWorkspace.Schema));
        Assert.ThrowsExactly<IOException>(() => SpeedCoordinator.ValidateResult(request, result with { WarmupMilliseconds = [1] }, LocalWorkspace.Schema));
        Assert.ThrowsExactly<IOException>(() => SpeedCoordinator.ValidateResult(request, result with { MeasurementMs = null }, LocalWorkspace.Schema));
        Assert.ThrowsExactly<IOException>(() => SpeedCoordinator.ValidateResult(request, result with { Samples = [result.Samples[0]] }, LocalWorkspace.Schema));
        Assert.ThrowsExactly<IOException>(() => SpeedCoordinator.ValidateResult(request, result with { Samples = [result.Samples[0], result.Samples[1] with { Outcome = "failed" }] }, LocalWorkspace.Schema));
        Assert.ThrowsExactly<IOException>(() => SpeedCoordinator.ValidateResult(request, result with { Samples = [result.Samples[0], result.Samples[1] with { OffsetMs = double.NaN }] }, LocalWorkspace.Schema));
        var execRequest = request with { Trial = trial with { Experiment = "F04", Variant = "first" } };
        var execResult = result with { Samples = [result.Samples[0]], ExecSourceSha256 = SpeedExec.Sha256 };
        SpeedCoordinator.ValidateResult(execRequest, execResult, LocalWorkspace.Schema);
        Assert.ThrowsExactly<IOException>(() => SpeedCoordinator.ValidateResult(execRequest, execResult with { ExecSourceSha256 = "wrong" }, LocalWorkspace.Schema));
    }
}
