using System.Text.Json;
using UnityBridgeDesk.Infrastructure.SpeedBench;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class SpeedStatisticsTests
{
    [TestMethod]
    [DataRow(1, 12.7062047364)]
    [DataRow(4, 2.7764451052)]
    [DataRow(9, 2.2621571629)]
    [DataRow(29, 2.0452296421)]
    [DataRow(99, 1.9842169515)]
    public void StudentQuantilesMatchPublishedTables(int degrees, double expected) =>
        Assert.AreEqual(expected, SpeedStatistics.Student95(degrees), 1e-8);

    private static ReportBlock[] Pairs(double[] a, double[] b) => a.Select((x, i) =>
        new ReportBlock("F01", "prepared", i, "a", "b", Guid.NewGuid(), Guid.NewGuid(), x, b[i])).ToArray();

    [TestMethod]
    public void MeanUsesIndependentTrialCountAndStudentInterval()
    {
        var ci = SpeedStatistics.Mean([90, 95, 100, 105, 110]);
        Assert.AreEqual(100, ci.Mean); Assert.AreEqual(5, ci.Count);
        Assert.AreEqual(100 - 2.7764451052 * Math.Sqrt(12.5), ci.Lower!.Value, 1e-8);
        Assert.AreEqual(100 + 2.7764451052 * Math.Sqrt(12.5), ci.Upper!.Value, 1e-8);
        Assert.IsNull(SpeedStatistics.Mean([1, 2, 3, 4]).Lower);
        Assert.IsNull(SpeedStatistics.Mean([double.NaN]).Mean);
        Assert.IsNull(SpeedStatistics.Mean([]).Mean);
        var report = new SpeedReport(SpeedReportSample.Methodology());
        Assert.AreEqual(10, report.Summaries[0].Evidence!.Count);
        Assert.AreEqual(10, report.Comparisons[0].Evidence!.Count); // Not 200 calls per arm.
    }

    [TestMethod]
    public void PairedFiellerInvertsPairedTAndPreservesScaleAndPairing()
    {
        double[] a = [90, 95, 100, 105, 110], b = [70, 85, 80, 75, 90];
        var ci = SpeedStatistics.Compare(Pairs(a, b));
        Assert.AreEqual("bounded", ci.Status);
        Assert.AreEqual(.8, ci.Ratio!.Value, 1e-12);
        // Independently verify the defining equation at both roots, without copying the solver.
        foreach (double ratio in new[] { ci.Lower!.Value, ci.Upper!.Value })
        {
            var difference = b.Select((y, i) => y - ratio * a[i]).ToArray();
            double t = difference.Average() / Math.Sqrt(SpeedStatistics.Variance(difference) / 5);
            Assert.AreEqual(2.7764451052, Math.Abs(t), 1e-8);
        }
        Assert.AreEqual(100 * (1 - ci.Upper), ci.ReductionLower);
        Assert.AreEqual(100 * (1 - ci.Lower), ci.ReductionUpper);
        var scaled = SpeedStatistics.Compare(Pairs(a.Select(x => x * 1e8).ToArray(), b.Select(x => x * 1e8).ToArray()));
        Assert.AreEqual(ci.Lower.Value, scaled.Lower!.Value, 1e-12);
        var independentPairs = SpeedStatistics.Compare(Pairs(a, b.Reverse().ToArray()));
        Assert.IsGreaterThan(ci.Upper.Value - ci.Lower.Value, independentPairs.Upper!.Value - independentPairs.Lower!.Value);
        Assert.IsNotNull(ci.SuggestedBlocks);
    }

    [TestMethod]
    public void InsufficientOrUnboundedAndNoDifferenceCannotClaimWinner()
    {
        Assert.AreEqual("insufficient", SpeedStatistics.Compare(Pairs([1, 2, 3, 4], [1, 2, 3, 4])).Status);
        var unstable = SpeedStatistics.Compare(Pairs([1, 1, 1, 1, 1000], [10, 12, 14, 11, 13]));
        Assert.AreEqual("unbounded", unstable.Status); Assert.IsNull(unstable.Lower); Assert.IsNull(unstable.Upper);
        Assert.AreEqual("판단 유보", unstable.Interpretation);
        var equal = SpeedStatistics.Compare(Pairs([90, 95, 100, 105, 110], [90, 95, 100, 105, 110]));
        Assert.AreEqual("차이 방향 불확실", equal.Interpretation);
        Assert.AreEqual(1, equal.Lower!.Value, 1e-6); Assert.AreEqual(1, equal.Upper!.Value, 1e-6);
        var failed = Pairs([90, 95, 100, 105, 110], [70, 75, 80, 85, 90]);
        failed[0] = failed[0] with { CandidateMs = null };
        Assert.AreEqual(4, SpeedStatistics.Compare(failed).Count);
    }

    [TestMethod]
    public void NewFollowupIsFixedBalancedAndDoesNotRewritePilot()
    {
        var run = SpeedReportSample.Methodology(); string raw = JsonSerializer.Serialize(run);
        var report = new SpeedReport(run); var plan = SpeedStatistics.FollowupOptions(report);
        Assert.IsNotNull(plan); Assert.IsTrue(plan.Repeats is >= 5 and <= 100);
        Assert.AreEqual(0, plan.Repeats % run.Releases.Length);
        Assert.AreEqual(run.Options.Calls, plan.Calls); Assert.AreEqual(run.Options.Warmups, plan.Warmups);
        Assert.AreEqual(raw, JsonSerializer.Serialize(run));
        Assert.IsNull(SpeedStatistics.FollowupOptions(new SpeedReport(run with { Status = "running" })));
        var incomplete = run with { Results = run.Results.Skip(1).ToArray() };
        var partial = new SpeedReport(incomplete);
        Assert.IsNull(SpeedStatistics.FollowupOptions(partial));
        Assert.Contains("성공쌍에 한정", partial.Comparisons[0].Inference);
        Assert.IsNull(SpeedStatistics.FollowupOptions(new SpeedReport(SpeedReportSample.Create())));
        var drift = new SpeedReport(SpeedReportSample.Methodology(20));
        Assert.IsTrue(drift.Comparisons[0].Evidence!.SequenceWarning);
        Assert.Contains("순서", drift.Comparisons[0].Inference);
        Assert.IsNull(SpeedStatistics.FollowupOptions(drift));
    }

    [TestMethod]
    public void SeparatesVarianceAndRefusesInnerAllocationWithDriftOrUnequalCalls()
    {
        var run = SpeedReportSample.Methodology();
        var advice = SpeedStatistics.Replications(run)[0];
        Assert.AreEqual(35, advice.WithinVariance); // sample variance of 0..19
        Assert.AreEqual(SpeedStatistics.Variance(Enumerable.Range(0, 10).Select(i => 100d + i * 2).ToArray()) - 35d / 20,
            advice.BetweenVariance!.Value, 1e-10);
        Assert.AreEqual(60000d, advice.SetupCostMs);
        var drifting = run with { Results = run.Results.Select(r => r with { Guest = r.Guest! with {
            Samples = r.Guest!.Samples.OrderBy(s => s.Milliseconds).Select((s, i) => s with { Index = i }).ToArray() } }).ToArray() };
        Assert.IsTrue(SpeedStatistics.HasSequenceSignal(Enumerable.Range(0, 20).Select(i => (double)i).ToArray()));
        Assert.IsNull(SpeedStatistics.Replications(drifting)[0].SuggestedCalls);
        Assert.Contains("순서", SpeedStatistics.Replications(drifting)[0].SequenceCheck);
        Assert.IsFalse(SpeedStatistics.HasSequenceSignal(Enumerable.Repeat(10d, 20).ToArray()));
        var unequal = run with { Results = [run.Results[0] with { Guest = run.Results[0].Guest! with { Samples = [] } }, ..run.Results.Skip(1)] };
        Assert.IsNull(SpeedStatistics.Replications(unequal)[0].WithinVariance);
    }

    [TestMethod]
    public void LegacyWarmupsRemainUnknownAndAreNeverPooledIntoMeans()
    {
        var run = SpeedReportSample.Methodology();
        var modified = run with { Results = run.Results.Select(r => r with { Guest = r.Guest! with { WarmupMilliseconds = [999999, 999999] } }).ToArray() };
        Assert.AreEqual(new SpeedReport(run).Summaries[0].Mean, new SpeedReport(modified).Summaries[0].Mean);
        Assert.Contains("사전 실행 시간 없음", SpeedStatistics.SequenceDescription(new SpeedReport(SpeedReportSample.Create()).Trials[0]));
        Assert.Contains("속도 평균에서 제외", SpeedStatistics.SequenceDescription(new SpeedReport(run).Trials[0]));
        var legacy = JsonSerializer.Deserialize<SpeedRun>(JsonSerializer.Serialize(SpeedReportSample.Create()))!;
        Assert.IsNull(legacy.Results[0].Guest!.WarmupMilliseconds);
    }

    [TestMethod]
    public void PairedNormalSimulationHasReasonableCoverageWithoutPoolingCalls()
    {
        var random = new Random(93731);
        double Normal() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        int covered = 0;
        for (int repetition = 0; repetition < 1000; repetition++)
        {
            double[] a = new double[10], b = new double[10];
            for (int i = 0; i < a.Length; i++) { double shared = Normal() * 8; a[i] = 100 + shared + Normal() * 3; b[i] = 80 + shared * .8 + Normal() * 3; }
            var interval = SpeedStatistics.Compare(Pairs(a, b));
            if (interval.Lower <= .8 && interval.Upper >= .8) covered++;
        }
        Assert.IsTrue(covered is >= 925 and <= 980, $"Synthetic paired normal coverage: {covered}/1000");
    }
}
