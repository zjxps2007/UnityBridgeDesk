using System.Globalization;
using System.Text.Json;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record ResearchCompletion(string Release, int Planned, int Recorded, int Valid, int Failed, int Cancelled, int NotRun)
{
    public string Display => $"{Release} · 유효 {Valid}/{Planned} · 실패·제외 {Failed} · 중단 {Cancelled} · 미수행 {NotRun}";
}
public static class ResearchOutcomes
{
    public static ResearchCompletion[] Completion(SpeedReport report, string? experiment = null, string? condition = null) => report.Run.Releases.Select(release =>
    {
        var trials = report.Trials.Where(t => t.Release == release.Tag && (experiment is null || t.Experiment == experiment) && (condition is null || t.Condition == condition)).ToArray();
        return new ResearchCompletion(release.Tag, trials.Length, trials.Count(t => t.Result is not null), trials.Count(t => t.Included),
            trials.Count(t => t.Result is not null && !t.Included && t.StatusCode != "cancelled"), trials.Count(t => t.StatusCode == "cancelled"), trials.Count(t => t.Result is null));
    }).ToArray();
    public static string CompletionText(SpeedReport report, string? experiment = null, string? condition = null) =>
        string.Join("\n", Completion(report, experiment, condition).Select(c => c.Display)) +
        "\n분모: 계획한 새 프로젝트 시행 전체. 준비 오류·정리 실패도 숨기지 않는다. 시간 평균은 유효 시행에 한정한다.";
    public static string SessionSummary(SpeedRun run)
    {
        string prefix = $"세션 {run.Id.ToString("N")[..8]} · {run.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · 연구 묶음 {run.Options.Research?.StudyGroup ?? "미지정"}";
        if (run.ResearchPlan is not { } plan) return prefix + "\n과거 기록: 실행 순서·전체 실행기 명세 없음";
        try
        {
            using var json = JsonDocument.Parse(plan.Payload);
            bool manifest = json.RootElement.TryGetProperty("runtimeManifest", out var value) && value.ValueKind == JsonValueKind.Object;
            string count = manifest ? value.GetProperty("Files").GetArrayLength().ToString(CultureInfo.InvariantCulture) : "없음";
            return prefix + $"\n실행기 파일 해시 {count} · 외부 사전등록·다른 세션 재현은 별도 확인";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return prefix + "\n실행 명세 확인 필요"; }
    }
    public static string Sensitivity(SpeedReport report, ReportComparison comparison)
    {
        if (SpeedResearch.PlanStatus(report.Run) != "사전 고정 계획 일치") return "지연 감지 판정 유보 · 사전 계획 확인 필요";
        try { SpeedResearch.ValidateTargets(report.Run.Options, report.Run.Releases); }
        catch (ArgumentException) { return "지연 감지 판정 유보 · 동일 대상 확인 필요"; }
        var blocks = report.Blocks.Where(b => b.Experiment == comparison.Experiment && b.Condition == comparison.Condition && b.Candidate == comparison.Candidate && b.Included).ToArray();
        var outer = new List<double>(); var inner = new List<double>();
        foreach (var block in blocks)
        {
            var a = report.Run.Results.Single(r => r.Trial.Id == block.BaselineId).Guest!.Samples;
            var b = report.Run.Results.Single(r => r.Trial.Id == block.CandidateId).Guest!.Samples;
            if (a.Any(s => s.InnerDelayMs is not >= 0 || !double.IsFinite(s.InnerDelayMs.Value)) || b.Any(s => s.InnerDelayMs is not >= 0 || !double.IsFinite(s.InnerDelayMs.Value))) continue;
            outer.Add(block.CandidateMs!.Value - block.BaselineMs!.Value);
            inner.Add(b.Average(s => s.InnerDelayMs!.Value) - a.Average(s => s.InnerDelayMs!.Value));
        }
        if (outer.Count < 5 || !comparison.CompleteRun || outer.Count != comparison.Planned) return "지연 감지 자료 부족 · 전체 유효 쌍과 Unity 내부 지연 기록 필요";
        double residual = outer.Zip(inner, (a,b) => a-b).Average();
        double half = SpeedStatistics.Student95(outer.Count-1) * Math.Sqrt(SpeedStatistics.Variance(outer.ToArray())/outer.Count);
        return $"외부 증가 {SpeedReport.Time(outer.Average())}ms (95% 구간 {SpeedReport.Time(outer.Average()-half)}–{SpeedReport.Time(outer.Average()+half)}) · 실제 내부 증가 {SpeedReport.Time(inner.Average())}ms · 잔차 {SpeedReport.Time(residual)}ms. 진단 관찰값이며 고정 보정값·정확성 인증이 아닙니다.";
    }
}
