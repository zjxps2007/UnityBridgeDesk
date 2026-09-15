using System.Globalization;
using System.Text;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record ReportTrial(SpeedTrial Trial, SpeedTrialResult? Result)
{
    public int Order => Trial.Order;
    public int Block => Trial.Block;
    public string Experiment => Trial.Experiment;
    public string Condition => SpeedReport.Condition(Trial.Experiment, Trial.Variant);
    public string Release => Trial.Tag;
    public string StatusCode => Result?.Status ?? "not-run";
    public string Status => SpeedReport.StatusLabel(StatusCode);
    public bool Included => Result is not null && SpeedAnalysis.IsValid(Result);
    public double? WorkMs => Included ? Result!.Guest!.WorkMs : null;
    public string Cleanup => Result is null ? "미수행" : Result.ResetVerified ? "확인됨" : "미확인";
    public string Reason => Included ? "" : Result is null ? "아직 수행하지 않음" :
        !string.IsNullOrWhiteSpace(Result.Error) ? Result.Error : !Result.ResetVerified ? "임시 폴더 정리 미확인" :
        Result.Status != "success" ? "시행 " + Status : "측정 결과·대표시간 검증 미충족";
    public string Details => $"#{Order} · {Release} · {Experiment} {Condition}\n" +
        $"상태: {Status} · 정리: {Cleanup} · 속도 통계: {(Included ? "포함" : "제외")}\n" +
        $"대표시간: {SpeedReport.Time(WorkMs)} {SpeedReport.Unit(Experiment)}\n" +
        $"확인된 내용: {(Included ? "응답 검증 통과" : Reason)}\n" +
        $"Connector 보고 값: {Result?.Guest?.ReportedConnectorVersion ?? "미확인"}\n시행 ID: {Trial.Id}";
}

public sealed record ReportSummary(string Experiment, string Condition, string Release, int Planned, int Valid, int Excluded,
    double? Mean, double? Median, string Unit);
public sealed record ReportBlock(string Experiment, string Condition, int Block, string Baseline, string Candidate,
    Guid BaselineId, Guid CandidateId, double? BaselineMs, double? CandidateMs)
{
    public bool Included => BaselineMs.HasValue && CandidateMs.HasValue;
}
public sealed record ReportComparison(string Experiment, string Condition, string Candidate, int Planned, int Valid,
    double? BaselineMs, double? CandidateMs, double? ReductionPercent, string Unit)
{
    public string Change => ReductionPercent is not { } value ? "비교 불가" :
        Math.Abs(value) < .05 ? "표시 정밀도 내 동일" : $"시간 {Math.Abs(value).ToString("F1", CultureInfo.InvariantCulture)}% {(value > 0 ? "감소" : "증가")}";
    public string Blocks => $"{Valid}/{Planned}";
}

/// <summary>One projection supplies the UI, text report and workbook. Raw evidence is never rewritten.</summary>
public sealed class SpeedReport
{
    public SpeedRun Run { get; }
    public ReportTrial[] Trials { get; }
    public ReportSummary[] Summaries { get; }
    public ReportBlock[] Blocks { get; }
    public ReportComparison[] Comparisons { get; }
    public string Baseline => Run.Releases.FirstOrDefault()?.Tag ?? "없음";
    public int Success => Trials.Count(t => t.StatusCode == "success");
    public int Failed => Trials.Count(t => t.StatusCode == "failed");
    public int Cancelled => Trials.Count(t => t.StatusCode == "cancelled");
    public int CleanupFailed => Trials.Count(t => t.StatusCode == "cleanup-failed");
    public int NotRun => Trials.Count(t => t.Result is null);
    public int Valid => Trials.Count(t => t.Included);
    public string Counts => $"계획 {Trials.Length} · 기록 {Run.Results.Length} · 성공 {Success} · 실패 {Failed} · 중단 {Cancelled} · 정리 실패 {CleanupFailed} · 미수행 {NotRun}";
    public string State => Run.Status switch
    {
        "completed" when Failed > 0 => $"완료 — 실패 {Failed}건 포함",
        "completed" when Valid < Trials.Length => "완료 — 제외된 시행 포함",
        "completed" => "완료",
        "cancelled" => "중단 — 부분 결과",
        "cleanup-failed" => "정리 실패 — 다음 시행 중지",
        "running" => "미완료 기록 — 실행 상태 확인 필요",
        _ => "상태 확인 필요: " + Run.Status
    };
    public string Context => $"기준 {Baseline} · Unity {Run.Local?.EditorVersion ?? Run.Machine?.Settings.GetValueOrDefault("unity") ?? "기록 없음"}";
    public string Overview => Counts + "\n" + $"정리 확인 {Trials.Count(t => t.Result?.ResetVerified == true)}/{Run.Results.Length} · 통계 포함 {Valid}/{Trials.Length}\n" +
        Context + (Trials.Any(t => t.Result is not null && !t.Included) ?
            "\n확인할 시행: " + string.Join(" / ", Trials.Where(t => t.Result is not null && !t.Included).Take(3).Select(t => $"#{t.Order} {t.Release} {t.Experiment} {t.Condition}")) : "");

    public SpeedReport(SpeedRun run)
    {
        if (run.Plan is null || run.Results is null || run.Releases is null ||
            run.Plan.Select(t => t.Id).Distinct().Count() != run.Plan.Length ||
            run.Releases.Select(r => r.Tag).Distinct().Count() != run.Releases.Length ||
            run.Results.Select(r => r.Trial.Id).Distinct().Count() != run.Results.Length)
            throw new InvalidDataException("중복되거나 누락된 시행·릴리스 정보입니다.");
        var plan = run.Plan.ToDictionary(t => t.Id);
        if (run.Results.Any(r => !plan.TryGetValue(r.Trial.Id, out var t) || t != r.Trial) ||
            run.Plan.Any(t => !run.Releases.Any(r => r.Tag == t.Tag)) ||
            run.Plan.GroupBy(t => (t.Experiment, t.Variant, t.Block, t.Tag)).Any(g => g.Count() > 1))
            throw new InvalidDataException("실행 계획과 결과의 대상이 일치하지 않습니다.");
        Run = run;
        var records = run.Results.ToDictionary(r => r.Trial.Id);
        Trials = run.Plan.Select(t => new ReportTrial(t, records.GetValueOrDefault(t.Id))).ToArray();
        Summaries = SpeedAnalysis.Summaries(run).Select(s => new ReportSummary(s.Experiment, Condition(s.Experiment, s.Condition),
            s.Release, s.Planned, s.Success, s.Planned - s.Success, s.Mean, s.Median, s.Unit)).ToArray();
        var blocks = new List<ReportBlock>();
        foreach (var group in Trials.GroupBy(t => (t.Experiment, t.Condition, t.Block)))
        foreach (var candidate in run.Releases.Skip(1))
        {
            var a = group.SingleOrDefault(t => t.Release == Baseline);
            var b = group.SingleOrDefault(t => t.Release == candidate.Tag);
            blocks.Add(new(group.Key.Experiment, group.Key.Condition, group.Key.Block, Baseline, candidate.Tag,
                a?.Trial.Id ?? Guid.Empty, b?.Trial.Id ?? Guid.Empty, a?.WorkMs, b?.WorkMs));
        }
        Blocks = blocks.ToArray();
        Comparisons = Blocks.GroupBy(b => (b.Experiment, b.Condition, b.Candidate)).Select(g =>
        {
            var valid = g.Where(b => b.Included).ToArray();
            double? a = valid.Length == 0 ? null : valid.Average(b => b.BaselineMs!.Value);
            double? b = valid.Length == 0 ? null : valid.Average(b => b.CandidateMs!.Value);
            return new ReportComparison(g.Key.Experiment, g.Key.Condition, g.Key.Candidate, g.Count(), valid.Length,
                a, b, a.HasValue ? 100 * (1 - b / a) : null, Unit(g.Key.Experiment));
        }).ToArray();
    }

    public static string Condition(string experiment, string variant) => (experiment, variant) switch
    {
        ("F01", "first") => "첫 호출", ("F01", "prepared") => "준비 후 호출",
        ("F02", _) => variant + "개 명령", ("F03", "1024") => "본문 1 KiB",
        ("F03", "65536") => "본문 64 KiB", ("F03", "1048576") => "본문 1 MiB", _ => variant
    };
    public static string Unit(string experiment) => experiment == "F02" ? "ms/전체 작업" : "ms/호출";
    public static string Time(double? value) => value is { } n && double.IsFinite(n) ? n.ToString("N2", CultureInfo.InvariantCulture) : "—";
    public static string StatusLabel(string status) => status switch
    {
        "success" => "성공", "failed" => "실패", "cancelled" => "중단", "cleanup-failed" => "정리 실패",
        "not-run" => "미수행", _ => "미확정: " + status
    };

    public string Memo()
    {
        var text = new StringBuilder().AppendLine(State).AppendLine(Counts)
            .AppendLine($"정리 확인 {Trials.Count(t => t.Result?.ResetVerified == true)}/{Run.Results.Length} · 통계 포함 {Valid}/{Trials.Length}")
            .AppendLine(Context);
        if (Trials.Length == 0) text.AppendLine("실행 계획이 없습니다.");
        foreach (var issue in Trials.Where(t => t.Result is not null && !t.Included).Take(2))
            text.AppendLine($"확인: #{issue.Order} {issue.Release} · {issue.Experiment} {issue.Condition} — {OneLine(issue.Reason, 120)}");
        text.Append("같은 블록의 유효한 시행끼리 비교합니다. 시간은 낮을수록 짧으며, 표본이 적어 전체 우열은 확정하지 않습니다.");
        return text.ToString();
    }
    public string Text()
    {
        var text = new StringBuilder("UnityBridge 벤치 결과\n====================\n\n").AppendLine(Memo())
            .AppendLine().AppendLine($"시작: {Run.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"비교 버전: {string.Join(", ", Run.Releases.Select(r => r.Tag))}")
            .AppendLine($"실행 ID: {Run.Id}").AppendLine().AppendLine("조건별 시간").AppendLine("-----------");
        foreach (var group in Summaries.GroupBy(s => (s.Experiment, s.Condition)))
        {
            text.AppendLine($"[{group.Key.Experiment} {group.Key.Condition}]");
            foreach (var s in group) text.AppendLine($"  {s.Release}: 평균 {Time(s.Mean)} / 중앙값 {Time(s.Median)} {s.Unit} · 유효 {s.Valid}/{s.Planned}회");
            foreach (var c in Comparisons.Where(c => c.Experiment == group.Key.Experiment && c.Condition == group.Key.Condition))
                text.AppendLine($"  기준 대비 {c.Candidate}: {c.Change} · 공동 유효 {c.Blocks}블록");
            text.AppendLine();
        }
        return text.AppendLine("읽는 기준").AppendLine("---------")
            .AppendLine("F01·F03: 시행 안의 호출 평균. F02: 동일 작업을 마치는 전체 시간.")
            .AppendLine("각 버전 평균은 그 버전의 유효 시행, 버전 간 증감은 공동 유효 블록으로 계산하므로 두 평균은 다를 수 있습니다.")
            .AppendLine("실패·중단·미수행은 0ms로 계산하지 않습니다. Unity 준비·별도 검증·정리 시간은 명령 완료 시간과 분리합니다.")
            .AppendLine("01_결과분석.xlsx에서 시행·호출을 분석하고, 02_실패내역.txt에서 제외 이유를 확인하세요.")
            .AppendLine("원본 JSON·CSV와 로그는 앱의 ‘원본 자료’에서 확인할 수 있습니다.").ToString();
    }
    public string Issues()
    {
        var text = new StringBuilder("실패·제외 내역\n==============\n\n").AppendLine(State).AppendLine(Counts).AppendLine();
        var issues = Trials.Where(t => !t.Included && t.Result is not null).ToArray();
        if (issues.Length == 0) text.AppendLine("기록된 시행에 실패·제외 내역이 없습니다.");
        foreach (var issue in issues) text.AppendLine(issue.Details).AppendLine("추가 확인: 해당 시행의 editor.log, worker.json, result.json(존재하는 경우). 오류 문구만으로 직접 원인을 단정하지 않습니다.").AppendLine();
        if (NotRun > 0) text.AppendLine($"미수행 {NotRun}회: Excel의 시행기록에서 확인하세요. 실패 횟수와 구분합니다.");
        return text.ToString();
    }
    private static string OneLine(string text, int max) { text = text.Replace('\r', ' ').Replace('\n', ' '); return text.Length > max ? text[..max] + "…" : text; }
}
