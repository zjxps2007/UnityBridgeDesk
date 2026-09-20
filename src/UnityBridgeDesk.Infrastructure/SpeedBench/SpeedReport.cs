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
    public string FailureCategory => SpeedStability.Category(this);
    public string Reason => Included ? "" : Result is null ? "아직 수행하지 않음" :
        !string.IsNullOrWhiteSpace(Result.Error) ? SpeedFailure.Describe(Result.Error) : !Result.ResetVerified ? "임시 폴더 정리 미확인" :
        Result.Status != "success" ? "시행 " + Status : "측정 결과·대표시간 검증 미충족";
    public string CallProgress
    {
        get
        {
            var samples = Result?.Guest?.Samples ?? [];
            if (samples.Length == 0) return "측정 명령 기록 없음";
            string planned = Experiment == "F02" && int.TryParse(Trial.Variant, out int count) ? $"계획 {count}개 중 " : "";
            int valid = samples.Count(s => s.Outcome == "success"), failed = samples.Count(s => s.Outcome == "failed");
            int unknown = samples.Length - valid - failed;
            string firstFailure = samples.FirstOrDefault(s => s.Outcome == "failed") is { } first ? $" · 첫 실패 {first.Index + 1}번째" : "";
            return $"{planned}{samples.Length}개 호출 · 응답 검증 통과 {valid}개 · 실패 {failed}개" +
                (unknown > 0 ? $" · 판정 없음 {unknown}개" : "") + firstFailure;
        }
    }
    public string Details => $"#{Order} · {Release} · {Experiment} {Condition}\n" +
        $"상태: {Status} · 정리: {Cleanup} · 속도 통계: {(Included ? "포함" : "제외")}\n" +
        $"대표시간: {SpeedReport.Time(WorkMs)} {SpeedReport.Unit(Experiment)}\n" +
        $"측정 명령: {CallProgress}\n" +
        $"확인된 내용: {(Included ? "응답 검증 통과" : Reason)}\n" +
        $"오류 구분: {FailureCategory} · {SpeedFailure.Label(SpeedStability.Kind(this))} · 단계: {Result?.FailureStage ?? Result?.Guest?.FailureStage ?? "기록 없음"}\n" +
        $"Connector 보고 값: {Result?.Guest?.ReportedConnectorVersion ?? "미확인"}\n시행 ID: {Trial.Id}";
}

public sealed record ReportSummary(string Experiment, string Condition, string Release, int Planned, int Valid, int Excluded,
    double? Mean, double? Median, string Unit, MeanInterval? Evidence = null)
{
    public string ConfidenceRange => Evidence?.Display ?? "미평가";
}
public sealed record ReportBlock(string Experiment, string Condition, int Block, string Baseline, string Candidate,
    Guid BaselineId, Guid CandidateId, double? BaselineMs, double? CandidateMs)
{
    public bool Included => BaselineMs.HasValue && CandidateMs.HasValue;
}
public sealed record ReportComparison(string Experiment, string Condition, string Candidate, int Planned, int Valid,
    double? BaselineMs, double? CandidateMs, double? ReductionPercent, string Unit,
    EffectInterval? Evidence = null, bool CompleteRun = true)
{
    public string Change => ReductionPercent is not { } value ? "비교 불가" :
        Math.Abs(value) < .05 ? "표시 정밀도 내 동일" : $"시간 {Math.Abs(value).ToString("F1", CultureInfo.InvariantCulture)}% {(value > 0 ? "감소" : "증가")}";
    public string Blocks => $"{Valid}/{Planned}";
    public string ConfidenceRange => Evidence?.Display ?? "미평가";
    public string Inference => !CompleteRun ? "미완료 결과 · 판단 유보" : Valid < Planned ? "제외 있음 · 성공쌍에 한정" : Evidence?.Interpretation ?? "판단 유보";
    public bool CanPlan => CompleteRun && Valid == Planned && Evidence is { Count: >= SpeedStatistics.MinimumTrials, SuggestedBlocks: >= 5 and <= 100, SequenceWarning: false };
    public string Planning => !CompleteRun ? "미완료 실행으로 다음 반복 수를 권장하지 않습니다." :
        Valid < Planned ? $"제외 {Planned - Valid}쌍의 원인을 확인한 뒤 예비 측정을 보강하세요." :
        Evidence?.Status == "insufficient" ? $"유효 {Valid}/5쌍 · 예비 측정을 보강해야 반복 수를 추정할 수 있습니다." :
        Evidence?.SequenceWarning == true ? "시행 순서에 따른 추세를 확인하고 환경·실험 계획을 재검토하세요." :
        CanPlan || Evidence is { SuggestedBlocks: > 100 } ? Evidence!.Planning : "변동 추정이 불안정합니다. 예비 측정과 순서를 점검하세요.";
}

/// <summary>One projection supplies the UI, text report and workbook. Raw evidence is never rewritten.</summary>
public sealed class SpeedReport
{
    public SpeedRun Run { get; }
    public ReportTrial[] Trials { get; }
    public ReportSummary[] Summaries { get; }
    public ReportBlock[] Blocks { get; }
    public ReportComparison[] Comparisons { get; }
    public StabilitySummary[] Stability { get; }
    public ReplicationAdvice[] Replication { get; }
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
        "completed" when Run.OfficialCleanup?.Status is "pending" or "failed" => "측정 완료 — 공식 도구 정리 미확인",
        "completed" when Run.GoCleanup?.Status is "pending" or "failed" => "측정 완료 — Go 도구 정리 미확인",
        "completed" when Failed > 0 => $"완료 — 실패 {Failed}건 포함",
        "completed" when Valid < Trials.Length => "완료 — 제외된 시행 포함",
        "completed" => "완료",
        "cancelled" => "중단 — 부분 결과",
        "cleanup-failed" => "정리 실패 — 다음 시행 중지",
        "running" => "미완료 기록 — 실행 상태 확인 필요",
        _ => "상태 확인 필요: " + Run.Status
    };
    public string Context => $"기준 {Baseline} · Unity {Run.Local?.EditorVersion ?? Run.Machine?.Settings.GetValueOrDefault("unity") ?? "기록 없음"}";
    public string OfficialCleanupNote => Run.OfficialCleanup is not { } cleanup ? "" : cleanup.Status == "deleted"
        ? "공식 CLI·Pipeline 임시 보관함 삭제 확인"
        : "공식 CLI·Pipeline 임시 보관함 정리 미확인: " + (cleanup.Error ?? "삭제 완료 기록 없음") + " · " + cleanup.Path;
    public string GoCleanupNote => Run.GoCleanup is not { } cleanup ? "" : cleanup.Status == "deleted"
        ? "Go CLI·Connector 임시 보관함 삭제 확인"
        : "Go CLI·Connector 임시 보관함 정리 미확인: " + (cleanup.Error ?? "삭제 완료 기록 없음") + " · " + cleanup.Path;
    public string ToolCleanupNote => string.Join("\n", new[] { OfficialCleanupNote, GoCleanupNote }.Where(s => s.Length > 0));
    public string GoMeasurementNote => Run.Releases.Any(r => r.GoUnity is not null)
        ? "Go unity-cli는 비공식 도구이며 자체 Connector를 사용한다. 명령별 작업·검증 기준은 공유한다. Go exec는 동일 C#을 표준입력으로, Bridge·Pipeline은 파일로 전달하므로 입력·출력·컴파일러를 포함한 도구 전체 경로 비교이다. Go CLI는 시험 전용 홈의 대상 정보 사본과 매 측정 묶음 직전 준비한 업데이트 확인 캐시를 사용하며, 실제 연결·버전 확인과 재시도는 측정에 포함한다. 캐시는 원본 CLI의 1시간 정책을 따르므로 1시간을 넘는 묶음은 업데이트 조회가 다시 발생할 수 있다." : "";
    public string Overview => Counts + "\n" + $"정리 확인 {Trials.Count(t => t.Result?.ResetVerified == true)}/{Run.Results.Length} · 통계 포함 {Valid}/{Trials.Length}\n" +
        (ToolCleanupNote.Length == 0 ? "" : ToolCleanupNote + "\n") +
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
            s.Release, s.Planned, s.Success, s.Planned - s.Success, s.Mean, s.Median, s.Unit,
            SpeedStatistics.Mean(Trials.Where(t => t.Experiment == s.Experiment && t.Trial.Variant == s.Condition && t.Release == s.Release && t.Included)
                .Select(t => t.WorkMs!.Value).ToArray()))).ToArray();
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
                a, b, a.HasValue ? 100 * (1 - b / a) : null, Unit(g.Key.Experiment), SpeedStatistics.Compare(g), run.Status == "completed");
        }).ToArray();
        Stability = SpeedStability.Summaries(this);
        Replication = SpeedStatistics.Replications(run);
    }

    public static string Condition(string experiment, string variant) => (experiment, variant) switch
    {
        ("F01", "first") => "첫 명령 완료 시간", ("F01", "prepared") => "반복 명령 평균 시간",
        ("F04", "first") => "exec 첫 명령 완료 시간", ("F04", "prepared") => "exec 반복 명령 평균 시간",
        ("S01", _) => "명령 부하 · " + variant,
        ("F02", _) => variant + "개 명령", ("F03", "1024") => "본문 1 KiB",
        ("F03", "65536") => "본문 64 KiB", ("F03", "1048576") => "본문 1 MiB", _ => variant
    };
    public static string Unit(string experiment) => experiment == "F02" ? "ms/전체 작업" : "ms/호출";
    public static string UnitLabel(string experiment) => experiment == "F02" ? "전체 작업 완료 시간 (ms)" : "명령 1회당 평균 시간 (ms)";
    public string ProtocolDescription(string experiment, string condition)
    {
        string common = "Unity 준비 완료 후 측정 · Unity 시작 시간 제외";
        string counts = condition == Condition(experiment, "first") && experiment is "F01" or "F04"
            ? "사전 실행 0회 → 명령 1회 측정"
            : experiment == "S01" ? $"시나리오당 {Run.Options.StressRequests}회 요청 · 최대 {Run.Options.StressConcurrency}개 동시 요청"
            : experiment == "F02" ? $"사전 실행 {Run.Options.Warmups}회 제외 → 1,000개 오브젝트 작업 전체 측정"
            : $"사전 실행 {Run.Options.Warmups}회 제외 → 명령 {Run.Options.Calls}회 측정 후 평균";
        return $"{common}\n조건·버전마다 새 프로젝트 실험 {Run.Options.Repeats}회 · {counts}\n조건마다 별도 Unity 프로세스 · 매 명령은 새 CLI 프로세스";
    }
    public string ConditionMemo(string experiment, string condition)
    {
        var comparisons = Comparisons.Where(c => c.Experiment == experiment && c.Condition == condition).ToArray();
        if (comparisons.All(c => c.Valid == 0)) return "비교할 유효 결과 없음 · 시행 상세에서 제외 이유를 확인하세요.";
        return string.Join("\n", comparisons.Select(c => c.Valid == 0 ? $"{c.Candidate}: 비교할 유효 결과 없음" :
            $"{c.Candidate} · 기준 {Baseline}보다 평균 완료 시간 {c.Change.Replace("시간 ", "")} · 비교 {c.Valid}쌍 / 제외 {c.Planned - c.Valid}쌍\n감소율 95% 구간 {c.ConfidenceRange} · {c.Inference}")) +
            "\n두 버전이 함께 유효한 시행으로 계산합니다. 구간이 0%를 포함하면 차이 방향이 불확실합니다.";
    }
    public static string Time(double? value) => value is { } n && double.IsFinite(n) ? n.ToString("N2", CultureInfo.InvariantCulture) : "—";
    public static string StatusLabel(string status) => status switch
    {
        "success" => "성공", "failed" => "실패", "cancelled" => "중단", "cleanup-failed" => "정리 실패",
        "not-run" => "미수행", _ => "미확정: " + status
    };

    public string ComparisonMemo(ReportComparison comparison)
    {
        string label = $"{comparison.Experiment} {comparison.Condition}";
        string scope = comparison.Experiment == "F02" ? "전체 작업당" : "호출당";
        string pairs = $"공동 유효 {comparison.Blocks}블록";
        if (comparison.Valid == 0 || comparison.BaselineMs is not { } a || comparison.CandidateMs is not { } b ||
            !double.IsFinite(a) || !double.IsFinite(b) || a <= 0 || b <= 0)
            return $"{label}: {Baseline} 버전과 {comparison.Candidate} 버전의 유효한 평균 시간을 확보하지 못해 비교할 수 없다({pairs}).";
        string result;
        if (a == b)
            result = $"{label}: {Baseline} 버전과 {comparison.Candidate} 버전의 {scope} 평균 완료 시간이 {MemoTime(a)} ms로 같았다({pairs}).";
        else if (Math.Round(a, 2) == Math.Round(b, 2) && Math.Abs(a - b) / Math.Max(a, b) * 100 < .05)
            result = $"{label}: {Baseline} 버전과 {comparison.Candidate} 버전의 {scope} 평균은 표시상 모두 {Time(a)} ms이며, 차이는 0.01 ms 미만이었다({pairs}). 표시 정밀도 내에서 빠른 버전을 구분하지 않는다.";
        else
        {
            bool candidateFaster = b < a;
            string faster = candidateFaster ? comparison.Candidate : Baseline;
            string slower = candidateFaster ? Baseline : comparison.Candidate;
            double fast = Math.Min(a, b), slow = Math.Max(a, b);
            double percent = 100 * (1 - fast / slow);
            string reduction = percent < .05 ? "0.1% 미만" : percent.ToString("F1", CultureInfo.InvariantCulture) + "%";
            result = $"{label}: {faster} 버전이 {slower} 버전보다 {scope} 평균 {MemoTime(slow - fast)} ms 빨랐다. " +
                $"평균은 {faster} {MemoTime(fast)} ms, {slower} {MemoTime(slow)} ms이며, {slower} 대비 완료 시간이 {reduction} 짧았다({pairs}).";
        }
        if (comparison.Valid == 1) result += " 한 블록의 비교이므로 반복 간 변동은 확인하지 못했다.";
        if (comparison.Valid < comparison.Planned) result += $" 한쪽 이상이 유효하지 않은 {comparison.Planned - comparison.Valid}블록은 비교에서 제외했다.";
        return result + $" 기준 {Baseline} 대비 {comparison.Candidate}의 시간 감소율 95% 구간은 {comparison.ConfidenceRange}이다. {comparison.Inference}.";
    }
    private static string MemoTime(double value) => value > 0 && value < .01
        ? value.ToString("G3", CultureInfo.InvariantCulture) : Time(value);

    public string Memo()
    {
        var text = new StringBuilder().AppendLine("상태: " + State).AppendLine(Context)
            .AppendLine().AppendLine("이번 측정의 평균 시간 비교").AppendLine("-------------------------");
        if (Trials.Length == 0) text.AppendLine("실행 계획이 없다.");
        else if (Comparisons.Length == 0) text.AppendLine("비교할 두 버전의 조건이 없어 평균 시간 차이를 요약할 수 없다.");
        foreach (var comparison in Comparisons) text.AppendLine(ComparisonMemo(comparison)).AppendLine();
        text.AppendLine("수행 현황").AppendLine("---------").AppendLine(Counts)
            .AppendLine($"정리 확인 {Trials.Count(t => t.Result?.ResetVerified == true)}/{Run.Results.Length} · 통계 포함 {Valid}/{Trials.Length}")
            .AppendLine("평균 차이는 같은 조건·블록에서 두 버전 모두 유효한 시행으로 계산했다. 실패·중단·미수행은 0ms로 넣지 않았다.");
        if (ToolCleanupNote.Length > 0) text.AppendLine(ToolCleanupNote);
        if (GoMeasurementNote.Length > 0) text.AppendLine(GoMeasurementNote);
        foreach (var issue in Trials.Where(t => t.Result is not null && !t.Included).Take(2))
            text.AppendLine($"확인: #{issue.Order} {issue.Release} · {issue.Experiment} {issue.Condition} — {OneLine(issue.Reason, 120)}");
        text.Append("서로 다른 작업과 단위의 시간을 합쳐 전체 순위를 정하지 않는다. 이번 측정의 관찰값이며 통계적 성능 우열을 확정하지 않는다.");
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
            text.AppendLine(ProtocolDescription(group.Key.Experiment, group.Key.Condition));
            foreach (var s in group) text.AppendLine($"  {s.Release}: 평균 {Time(s.Mean)} / 중앙값 {Time(s.Median)} {s.Unit} · 유효 {s.Valid}/{s.Planned}회 · 평균 95% 구간 {s.ConfidenceRange}");
            foreach (var c in Comparisons.Where(c => c.Experiment == group.Key.Experiment && c.Condition == group.Key.Condition))
                text.AppendLine($"  기준 대비 {c.Candidate}: {c.Change} · 공동 유효 {c.Blocks}블록 · 감소율 95% 구간 {c.ConfidenceRange} · {c.Inference}");
            text.AppendLine();
        }
        text.AppendLine("안정성").AppendLine("------");
        foreach (var s in Stability)
            text.AppendLine($"{s.Experiment} {s.Condition} · {s.Release}: 유효 완료 {s.Completion}, 중단 {s.Cancelled}, 미수행 {s.NotRun}. {s.Failures}. " +
                $"시행 범위 {s.Range} ms, 표준편차 {Time(s.StandardDeviationMs)} ms. 성공 호출 P95 {Time(s.CallP95Ms)} ms · 호출 판정 {s.CallCompletion}." +
                (s.RequestsPerSecond is { } rate ? $" 부하 처리량 {rate:N2} 요청/초." : ""));
        if (Run.Options.Selected.Contains("S01"))
        {
            text.AppendLine($"부하 설정: 명령당 {Run.Options.StressRequests}개 요청 · 최대 {Run.Options.StressConcurrency}개 동시 요청.");
            foreach (var c in Run.Options.StressCommands ?? SpeedStress.DefaultCommands)
                text.AppendLine($"  {c.Id} → {c.Command}: {c.Description ?? c.Id} · " +
                    (c.ExpectedDataJson is null ? "명령 성공 응답 확인; 업무 결과 기대값 미지정" : "지정한 기대 결과를 검증"));
        }
        return text.AppendLine().AppendLine("읽는 기준").AppendLine("---------")
            .AppendLine("F01·F03·F04·S01은 시행 안의 호출 평균, F02는 동일 작업을 마치는 전체 시간이다.")
            .AppendLine("F04는 같은 C# 소스의 파일 읽기·전송·컴파일·실행을 포함한다. 반복 명령도 매번 컴파일하며 순수 C# 실행 시간만을 뜻하지 않는다.")
            .AppendLine("S01은 여러 CLI 요청을 동시에 보낸다. Unity 내부의 병렬 실행은 보장하지 않는다. 실패한 부하 시행의 부분 호출은 원본과 호출표본에 남는다.")
            .AppendLine("유효 완료율은 유효 완료 / 평가 대상 시행이다. 환경 준비·벤치 호환성 오류, 사용자 중단·미수행은 분모에서 제외한다. 정리 실패는 포함한다. 성공률 100%가 안정성을 보장하지 않는다.")
            .AppendLine("최소–최대와 표준편차는 새 프로젝트 시행 대표시간의 변동이며 신뢰구간이 아니다. 호출 P95는 성공 호출의 관찰 분포다. 이전 기록에 없는 오류·호출 판정은 추정하지 않는다.")
            .AppendLine("부하 처리량은 유효 S01 시행마다 전체 요청 수 / 부하 구간 경과 시간으로 구한 뒤 평균한다. 이 구간에는 호출 준비·응답 검증과 요청 사이 간격도 포함된다.")
            .AppendLine("각 버전 평균은 그 버전의 유효 시행, 버전 간 증감은 공동 유효 블록으로 계산하므로 두 평균은 다를 수 있다.")
            .AppendLine("메모의 감소율은 문장에 적힌 느린 버전의 평균을 분모로 한다. 상세 수치의 기준 대비 변화는 지정된 기준 버전의 평균을 분모로 한다.")
            .AppendLine("실패·중단·미수행은 0ms로 계산하지 않는다. Unity 준비·별도 검증·정리 시간은 명령 완료 시간과 분리한다.")
            .AppendLine("01_결과분석.xlsx에서 시행·호출을 분석하고, 02_실패내역.txt에서 제외 이유를 확인한다.")
            .AppendLine("03_결과그래프.svg에서 조건별 그래프를, 04_측정근거.txt에서 신뢰구간의 전제·수식·다음 실험 반복 설계를 확인한다. 원본 JSON·CSV와 로그는 앱의 ‘자료 폴더 → 원본·로그 폴더’에서 확인한다.").ToString();
    }
    public string Issues()
    {
        var text = new StringBuilder("실패·제외 내역\n==============\n\n").AppendLine(State).AppendLine(Counts).AppendLine();
        if (ToolCleanupNote.Length > 0) text.AppendLine(ToolCleanupNote).AppendLine();
        var issues = Trials.Where(t => !t.Included && t.Result is not null).ToArray();
        if (issues.Length == 0) text.AppendLine("기록된 시행에 실패·제외 내역이 없습니다.");
        foreach (var issue in issues) text.AppendLine(issue.Details).AppendLine("추가 확인: 해당 시행의 editor.log, worker.json, result.json(존재하는 경우). 오류 문구만으로 직접 원인을 단정하지 않습니다.").AppendLine();
        if (NotRun > 0) text.AppendLine($"미수행 {NotRun}회: Excel의 시행기록에서 확인하세요. 실패 횟수와 구분합니다.");
        return text.ToString();
    }
    private static string OneLine(string text, int max) { text = text.Replace('\r', ' ').Replace('\n', ' '); return text.Length > max ? text[..max] + "…" : text; }
}
