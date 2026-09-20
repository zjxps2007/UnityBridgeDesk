using System.Globalization;
using System.Text;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record SpeedSummary(string Experiment, string Condition, string Release, int Planned, int Success, int Failed,
    double? Mean, double? Median, double? Minimum, double? Maximum, string Unit);
public sealed record SpeedPair(string Experiment, string Condition, string Baseline, string Candidate, int PlannedBlocks, int CompleteBlocks,
    double? DifferenceMs, double? ReductionPercent);
public static class SpeedAnalysis
{
    public static SpeedSummary[] Summaries(SpeedRun run) => run.Plan.GroupBy(t => (t.Experiment, t.Variant, t.Tag)).Select(g =>
    {
        var trials = run.Results.Where(r => g.Any(t => t.Id == r.Trial.Id)).ToArray();
        var values = trials.Where(IsValid).Select(r => r.Guest!.WorkMs!.Value).Order().ToArray();
        double? median = values.Length == 0 ? null : values.Length % 2 == 1 ? values[values.Length / 2] : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
        return new SpeedSummary(g.Key.Experiment, g.Key.Variant, g.Key.Tag, g.Count(), values.Length, trials.Count(r => !IsValid(r)),
            values.Length == 0 ? null : values.Average(), median, values.Length == 0 ? null : values[0], values.Length == 0 ? null : values[^1],
            g.Key.Experiment == "F02" ? "ms/전체 작업" : "ms/호출");
    }).OrderBy(s => s.Experiment, StringComparer.Ordinal).ThenBy(s => VariantOrder(s.Condition)).ThenBy(s => s.Condition, StringComparer.Ordinal)
        .ThenBy(s => Array.FindIndex(run.Releases, r => r.Tag == s.Release)).ToArray();
    internal static int VariantOrder(string variant) => variant == "first" ? 0 : variant == "prepared" ? 1 : int.TryParse(variant, out int n) ? n : int.MaxValue;
    public static bool IsValid(SpeedTrialResult result) => result.Status == "success" && result.ResetVerified &&
        result.Guest is { Status: "success", WorkMs: > 0 } && double.IsFinite(result.Guest.WorkMs.Value);
    public static SpeedPair[] Pairs(SpeedRun run)
    {
        var result = new List<SpeedPair>(); string baseline = run.Releases[0].Tag;
        foreach (var c in run.Plan.GroupBy(t => (t.Experiment, t.Variant)))
        foreach (string candidate in run.Releases.Skip(1).Select(r => r.Tag))
        {
            var pairs = new List<(double A, double B)>();
            foreach (var block in c.GroupBy(t => t.Block))
            {
                var a = run.Results.SingleOrDefault(r => r.Trial.Block == block.Key && r.Trial.Tag == baseline);
                var b = run.Results.SingleOrDefault(r => r.Trial.Block == block.Key && r.Trial.Tag == candidate);
                if (a is not null && b is not null && IsValid(a) && IsValid(b)) pairs.Add((a.Guest!.WorkMs!.Value, b.Guest!.WorkMs!.Value));
            }
            result.Add(new(c.Key.Experiment, c.Key.Variant, baseline, candidate, c.Select(t => t.Block).Distinct().Count(), pairs.Count,
                pairs.Count == 0 ? null : pairs.Average(p => p.B - p.A), pairs.Count == 0 ? null : 100 * (1 - pairs.Average(p => p.B) / pairs.Average(p => p.A))));
        }
        return result.ToArray();
    }
    public static string Csv(SpeedRun run)
    {
        var csv = new StringBuilder("schema,runId,trialId,block,order,release,experiment,condition,status,resetVerified,workMs,sampleIndex,sampleMs,bytes,failureKind,failureStage,sampleOutcome,sampleFailure,sampleOffsetMs,sampleError,innerDelayMs\n");
        static string Cell(object? value) => "\"" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\"";
        foreach (var trial in run.Plan)
        {
            var result = run.Results.SingleOrDefault(r => r.Trial.Id == trial.Id);
            SpeedSample?[] samples = result?.Guest?.Samples is { Length: > 0 } raw ? raw.Cast<SpeedSample?>().ToArray() : [null];
            foreach (var sample in samples)
                csv.AppendLine(string.Join(',', new object?[] { run.Schema, run.Id, trial.Id, trial.Block, trial.Order, trial.Tag, trial.Experiment, trial.Variant,
                    result?.Status ?? "not-run", result?.ResetVerified, result?.Guest?.WorkMs, sample?.Index, sample?.Milliseconds, sample?.Bytes,
                    result?.FailureKind ?? result?.Guest?.FailureKind, result?.FailureStage ?? result?.Guest?.FailureStage,
                    sample?.Outcome, sample?.FailureKind, sample?.OffsetMs, sample?.Error, sample?.InnerDelayMs }.Select(Cell)));
        }
        return csv.ToString();
    }
}
