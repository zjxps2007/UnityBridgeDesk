namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record StabilitySummary(string Experiment, string Condition, string Release, int Planned, int Finished,
    int Valid, int Cancelled, int NotRun, int Timeouts, int Mismatches, int CleanupFailures, int OtherFailures,
    int UnknownFailures, int CallsObserved, int CallsVerified, int CallsFailed, int CallsUnknown,
    double? MinimumMs, double? MaximumMs, double? StandardDeviationMs, double? CallP95Ms, double? RequestsPerSecond = null,
    int EnvironmentFailures = 0, int CompatibilityFailures = 0)
{
    public int Evaluated => Finished - EnvironmentFailures - CompatibilityFailures;
    public double? SuccessPercent => Evaluated == 0 ? null : 100d * Valid / Evaluated;
    public string Completion => $"{Valid}/{Evaluated}" + (SuccessPercent is { } n ? $" ({n:F1}%)" : " (평가 없음)");
    public string CallCompletion => $"{CallsVerified}/{CallsObserved}" + (CallsUnknown > 0 ? $" · 판정 없음 {CallsUnknown}" : "");
    public string Range => MinimumMs.HasValue ? $"{SpeedReport.Time(MinimumMs)}–{SpeedReport.Time(MaximumMs)}" : "—";
    public string Failures => $"환경 준비 {EnvironmentFailures} · 벤치 호환성 {CompatibilityFailures} · 시간 초과 {Timeouts} · 응답 불일치 {Mismatches} · 정리 {CleanupFailures} · 기타 {OtherFailures} · 미분류 {UnknownFailures}";
}

public static class SpeedStability
{
    public static StabilitySummary[] Summaries(SpeedReport report) => report.Trials
        .GroupBy(t => (t.Experiment, t.Condition, t.Release)).Select(g =>
        {
            var trials = g.ToArray(); var valid = trials.Where(t => t.Included).Select(t => t.WorkMs!.Value).Order().ToArray();
            var samples = trials.SelectMany(t => t.Result?.Guest?.Samples ?? []).ToArray();
            var calls = samples.Where(s => s.Outcome == "success" && double.IsFinite(s.Milliseconds)).Select(s => s.Milliseconds).Order().ToArray();
            var failed = trials.Where(t => t.Result is not null && !t.Included && t.StatusCode != "cancelled" && Category(t) is not ("환경 준비 실패" or "벤치 호환성 오류")).ToArray();
            var throughput = trials.Where(t => t.Included && t.Experiment == "S01" && t.Result?.Guest?.MeasurementMs is > 0)
                .Select(t => t.Result!.Guest!.Samples.Length * 1000d / t.Result.Guest.MeasurementMs!.Value).Where(double.IsFinite).ToArray();
            int Count(string kind) => failed.Count(t => Kind(t) == kind);
            int other = failed.Count(t => Kind(t) is not ("timeout" or "response-mismatch" or "cleanup-error" or "unknown"));
            double? deviation = valid.Length < 2 ? null : Math.Sqrt(valid.Sum(x => Math.Pow(x - valid.Average(), 2)) / (valid.Length - 1));
            return new StabilitySummary(g.Key.Experiment, g.Key.Condition, g.Key.Release, trials.Length,
                trials.Count(t => t.Result is not null && t.StatusCode != "cancelled"), valid.Length,
                trials.Count(t => t.StatusCode == "cancelled"), trials.Count(t => t.Result is null),
                Count("timeout"), Count("response-mismatch"), Count("cleanup-error"), other, Count("unknown"),
                samples.Length, samples.Count(s => s.Outcome == "success"), samples.Count(s => s.Outcome == "failed"),
                samples.Count(s => s.Outcome is not ("success" or "failed")), valid.Length == 0 ? null : valid[0],
                valid.Length == 0 ? null : valid[^1], deviation, calls.Length == 0 ? null : calls[(int)Math.Ceiling(calls.Length * .95) - 1],
                throughput.Length == 0 ? null : throughput.Average(),
                trials.Count(t => Category(t) == "환경 준비 실패"), trials.Count(t => Category(t) == "벤치 호환성 오류"));
        }).ToArray();
    public static string Category(ReportTrial trial) => trial.Included ? "없음" : trial.Result is null ? "미수행" :
        trial.StatusCode == "cancelled" ? "사용자 중단" : trial.StatusCode == "cleanup-failed" ? "정리 실패" :
        Kind(trial) is "bench-incompatible" or "unsupported-command" ? "벤치 호환성 오류" :
        (trial.Result.FailureStage ?? trial.Result.Guest?.FailureStage) == "preparation" || Kind(trial) == "preparation-error" ? "환경 준비 실패" : "명령 실행 실패";
    public static string Kind(ReportTrial trial) => trial.Included ? "none" :
        trial.StatusCode == "cleanup-failed" ? "cleanup-error" : trial.StatusCode == "cancelled" ? "cancelled" :
        SpeedFailure.ReportKind(trial.Result?.FailureKind ?? trial.Result?.Guest?.FailureKind ?? "unknown",
            trial.Result?.Error ?? trial.Result?.Guest?.Error);
}
