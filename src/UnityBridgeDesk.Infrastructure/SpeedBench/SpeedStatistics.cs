using System.Globalization;
using System.Text;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record MeanInterval(int Count, double? Mean, double? Lower, double? Upper)
{
    public string Display => Lower.HasValue ? $"{SpeedReport.Time(Lower)}–{SpeedReport.Time(Upper)}" : $"표본 부족 ({Count}/5)";
}

public sealed record EffectInterval(int Count, string Status, double? Ratio, double? Lower, double? Upper,
    int? SuggestedBlocks = null, bool SequenceWarning = false)
{
    public double? ReductionLower => Upper.HasValue ? 100 * (1 - Upper.Value) : null;
    public double? ReductionUpper => Lower.HasValue ? 100 * (1 - Lower.Value) : null;
    public string Display => Status == "bounded"
        ? $"{ReductionLower!.Value.ToString("F1", CultureInfo.InvariantCulture)} ~ {ReductionUpper!.Value.ToString("F1", CultureInfo.InvariantCulture)}%"
        : Status == "insufficient" ? $"표본 부족 ({Count}/5쌍)" : "구간 추정 불안정";
    public string Interpretation => Status != "bounded" ? "판단 유보" : SequenceWarning ? "시행 순서 영향 · 해석 주의" : Upper < 1 ? "시간 감소 범위" : Lower > 1 ? "시간 증가 범위" : "차이 방향 불확실";
    public string Planning => SuggestedBlocks is { } n
        ? $"현재 호출 수 유지 시 약 {n}블록으로 새 실험 권장" + (n > 100 ? " (앱 상한 100회 초과)" : "")
        : "반복 수 추정 불가 · 예비 측정 보강 필요";
}

public sealed record ReplicationAdvice(string Experiment, string Condition, string Release, int Trials, int CallsPerTrial,
    double? WithinVariance, double? BetweenVariance, double? CallCostMs, double? SetupCostMs,
    int? SuggestedCalls, string SequenceCheck, string Note)
{
    public string CallsDisplay => SuggestedCalls?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string VarianceDisplay => WithinVariance.HasValue
        ? $"호출 내 {SpeedReport.Time(WithinVariance)} / 시행 간 {SpeedReport.Time(BetweenVariance)} ms²" : "분산 분리 불가";
}

/// <summary>
/// Kalibera & Jones (2013), corrected author manuscript, §§6, 9, 10.
/// The top-level observation is a fresh-project trial mean (or F02 total), not a pooled CLI call.
/// Eq. 4 supplies mean intervals; Fieller is adapted to Desk's paired blocks using their covariance.
/// Intervals are asymptotic, conditional on valid trials, and not simultaneous across comparisons.
/// </summary>
public static class SpeedStatistics
{
    public const string MethodologyId = "kj2013-paired-fieller-v1";
    public const string Source = "https://kar.kent.ac.uk/33611/";
    public const int MinimumTrials = 5;
    public const double Confidence = .95, TargetRelativeHalfWidth = .05;

    public static MeanInterval Mean(double[] values)
    {
        if (values.Any(v => !double.IsFinite(v) || v <= 0)) return new(0, null, null, null);
        if (values.Length == 0) return new(0, null, null, null);
        double mean = values.Average();
        if (values.Length < MinimumTrials) return new(values.Length, mean, null, null);
        double half = Student95(values.Length - 1) * Math.Sqrt(Variance(values) / values.Length);
        return double.IsFinite(half) ? new(values.Length, mean, mean - half, mean + half) : new(values.Length, mean, null, null);
    }

    public static EffectInterval Compare(IEnumerable<ReportBlock> blocks)
    {
        var pairs = blocks.Where(b => b.Included && b.BaselineMs > 0 && b.CandidateMs > 0 &&
            double.IsFinite(b.BaselineMs!.Value) && double.IsFinite(b.CandidateMs!.Value)).OrderBy(b => b.Block).ToArray();
        int n = pairs.Length;
        if (n == 0) return new(0, "insufficient", null, null, null);
        double[] a = pairs.Select(p => p.BaselineMs!.Value).ToArray(), b = pairs.Select(p => p.CandidateMs!.Value).ToArray();
        double ma = a.Average(), mb = b.Average(), ratio = mb / ma;
        if (n < MinimumTrials) return new(n, "insufficient", ratio, null, null);
        // Scaling avoids overflow in the Fieller quadratic without changing its roots.
        double scale = Math.Max(ma, mb);
        a = a.Select(x => x / scale).ToArray(); b = b.Select(x => x / scale).ToArray();
        ma = a.Average(); mb = b.Average();
        double va = Variance(a), vb = Variance(b);
        double covariance = a.Select((x, i) => (x - ma) * (b[i] - mb)).Sum() / (n - 1);
        var interval = Fieller(ma, mb, va, vb, covariance, n);
        int? required = null;
        // A fixed follow-up plan from pilot estimates, never an automatic significance-based stopping rule.
        bool Precise(int k)
        {
            var candidate = Fieller(ma, mb, va, vb, covariance, k);
            return candidate is { } bounds && (bounds.Upper - bounds.Lower) / (2 * ratio) <= TargetRelativeHalfWidth;
        }
        // For fixed pilot moments the inverted acceptance sets shrink as t²/n decreases.
        if (Precise(10000))
        {
            int low = MinimumTrials, high = 10000;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (Precise(mid)) high = mid; else low = mid + 1;
            }
            required = low;
        }
        bool sequenceWarning = HasSequenceSignal(a) || HasSequenceSignal(b);
        return interval is { } ci ? new(n, "bounded", ratio, ci.Lower, ci.Upper, required, sequenceWarning)
            : new(n, "unbounded", ratio, null, null, required, sequenceWarning);
    }

    private static (double Lower, double Upper)? Fieller(double a, double b, double va, double vb, double covariance, int n)
    {
        double t = Student95(n - 1), q = t * t / n;
        double aa = a * a - q * va, bb = a * b - q * covariance, cc = b * b - q * vb;
        // Inverting the paired t interval for B-r*A gives this quadratic. Do not fake finite bounds
        // when the denominator is indistinguishable from zero, or clip negative roots into certainty.
        if (!(aa > 0)) return null;
        double discriminant = bb * bb - aa * cc;
        double tolerance = 1e-13 * Math.Max(1, Math.Abs(bb * bb) + Math.Abs(aa * cc));
        if (discriminant < -tolerance || !double.IsFinite(discriminant)) return null;
        double root = Math.Sqrt(Math.Max(0, discriminant));
        double lower = (bb - root) / aa, upper = (bb + root) / aa;
        return double.IsFinite(lower) && double.IsFinite(upper) ? (lower, upper) : null;
    }

    public static ReplicationAdvice[] Replications(SpeedRun run) => run.Plan.GroupBy(t => (t.Experiment, t.Variant, t.Tag))
        .Select(group =>
        {
            var ids = group.Select(t => t.Id).ToHashSet();
            var trials = run.Results.Where(r => ids.Contains(r.Trial.Id) && SpeedAnalysis.IsValid(r)).ToArray();
            string condition = SpeedReport.Condition(group.Key.Experiment, group.Key.Variant);
            ReplicationAdvice Missing(string note) => new(group.Key.Experiment, condition, group.Key.Tag,
                trials.Length, 0, null, null, null, null, null, "미평가", note);
            if (!(group.Key.Experiment == "F03" || group.Key.Experiment is "F01" or "F04" && group.Key.Variant == "prepared"))
                return Missing("첫 요청·작업 분할·동시 부하는 호출 수 최적화 대상이 아닙니다. 새 프로젝트 시행 단위로 비교합니다.");
            if (trials.Length < MinimumTrials) return Missing("분산 분리에는 유효한 새 프로젝트 시행이 최소 5회 필요합니다.");
            if (run.Status != "completed" || trials.Length != group.Count())
                return Missing("실패·제외·미수행이 있는 예비 결과로 반복 수를 권장하지 않습니다. 먼저 실패 원인을 확인하세요.");
            var samples = trials.Select(t => (t.Guest!.Samples ?? []).OrderBy(s => s.Index).ToArray()).ToArray();
            int m = samples[0].Length;
            if (m < 2 || samples.Any(s => s.Length != m || s.Any(v => v.Outcome != "success" || !(v.Milliseconds > 0) || !double.IsFinite(v.Milliseconds))))
                return Missing("같은 호출 수와 성공 판정이 있는 원표본이 필요합니다. 과거 기록의 누락 값은 추정하지 않습니다.");
            double within = samples.Average(s => Variance(s.Select(v => v.Milliseconds).ToArray()));
            double between = Variance(trials.Select(t => t.Guest!.WorkMs!.Value).ToArray()) - within / m;
            double c1 = samples.Average(s => s.Average(v => v.Milliseconds));
            double c2 = trials.Average(t => Math.Max(0, t.HostLifecycleMs - t.Guest!.Samples.Sum(s => s.Milliseconds)));
            string check = m < 20 ? "호출 20회 미만 · 순서 영향 미확인" :
                samples.Any(s => HasSequenceSignal(s.Select(v => v.Milliseconds).ToArray()))
                    ? "호출 순서 의존·추세 주의" : "뚜렷한 순서 신호 없음 · 독립성 보증 아님";
            int? optimal = null;
            string note;
            if (m < 20 || check.StartsWith("호출 순서", StringComparison.Ordinal))
                note = "호출 순서 그래프·사전 실행을 확인한 뒤 반복 수를 설계하세요. 호출을 독립 표본으로 합치지 않습니다.";
            else if (!(between > 0) || !(c2 > 0))
                note = "시행 간 분산 또는 준비 비용이 충분히 추정되지 않았습니다. 상위 반복을 없애지 말고 예비 시행을 보강하세요.";
            else
            {
                double optimum = Math.Ceiling(Math.Sqrt(c2 / c1 * within / between));
                if (double.IsFinite(optimum)) optimal = (int)Math.Clamp(optimum, 1, 1000);
                note = "식 (2)·(3)의 두 단계 근사입니다. 비용에는 준비·검증·정리를 포함합니다. 독립성 확인 후 새 예비 실험에만 사용하세요.";
                if (optimum > 1000) note += " 이론 권장값이 호출 상한 1,000회를 초과해 제한했습니다.";
            }
            return new ReplicationAdvice(group.Key.Experiment, condition, group.Key.Tag, trials.Length, m,
                within, between, c1, c2, optimal, check, note);
        }).ToArray();

    public static bool HasSequenceSignal(double[] values)
    {
        if (values.Length < 20) return false;
        double mean = values.Average(), sum = values.Sum(v => (v - mean) * (v - mean));
        if (sum == 0) return false;
        for (int lag = 1; lag <= 4; lag++)
        {
            double acf = Enumerable.Range(lag, values.Length - lag).Sum(i => (values[i] - mean) * (values[i - lag] - mean)) / sum;
            if (Math.Abs(acf) > 1.96 / Math.Sqrt(values.Length)) return true;
        }
        return false;
    }

    public static SpeedOptions? FollowupOptions(SpeedReport report)
    {
        if (report.Comparisons.Length == 0 || report.Comparisons.Any(c => !c.CanPlan)) return null;
        int count = report.Comparisons.Max(c => c.Evidence!.SuggestedBlocks!.Value);
        // Finish complete rotations so each version occupies each within-block position equally.
        int versions = report.Run.Releases.Length;
        if (versions < 2) return null;
        count = (count + versions - 1) / versions * versions;
        return count <= 100 ? report.Run.Options with { Repeats = count } : null;
    }

    public static string SequenceDescription(ReportTrial? trial)
    {
        if (trial?.Result?.Guest is not { } guest) return "시행을 선택하면 호출 순서와 사전 실행 기록을 확인합니다.";
        string warmup = guest.WarmupMilliseconds is null ? "과거 기록: 사전 실행 시간 없음" : $"사전 실행 {guest.WarmupMilliseconds.Length}회(속도 평균에서 제외)";
        string intro = $"{warmup} · 측정 {guest.Samples.Length}회. 회색=사전 실행, 보라=측정, 붉은색=실패. 가로축은 호출 순서입니다.\n";
        if (trial.Experiment is "F02" or "S01") return intro + "작업 분할·동시 부하는 순차 반복 평균과 달라 독립성 진단을 적용하지 않습니다.";
        if (trial.Trial.Variant == "first") return intro + "사전 실행 없는 첫 요청 조건입니다. 반복 명령의 안정 구간과 구분합니다.";
        var samples = guest.Samples.OrderBy(s => s.Index).ToArray();
        if (!trial.Included || samples.Any(s => s.Outcome != "success" || !(s.Milliseconds > 0) || !double.IsFinite(s.Milliseconds)))
            return intro + "실패·제외 또는 호출 판정 누락이 있어 순서 진단을 유보합니다. 원표본은 그대로 표시합니다.";
        if (samples.Length < 20) return intro + "측정 호출이 20회 미만입니다. 충분한 순서 자료를 확보해 추세를 확인하세요.";
        bool signal = HasSequenceSignal(samples.Select(s => s.Milliseconds).ToArray());
        return intro + (signal ? "lag 1~4 자기상관에서 순서 영향 신호가 있습니다. 추세·사전 실행 횟수를 점검하세요."
            : "lag 1~4에서 뚜렷한 순서 신호가 없습니다. 독립성이나 충분한 사전 실행을 보장하지 않습니다.") +
            " 구간과 반복 수는 예비 진단 후 별도 실험으로 확인하세요.";
    }

    public static double Variance(double[] values)
    {
        if (values.Length < 2) return 0;
        double mean = values.Average();
        return values.Sum(x => (x - mean) * (x - mean)) / (values.Length - 1);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, double> CriticalValues = new();
    public static double Student95(int degreesOfFreedom)
    {
        if (degreesOfFreedom < 1) throw new ArgumentOutOfRangeException(nameof(degreesOfFreedom));
        return CriticalValues.GetOrAdd(degreesOfFreedom, df =>
        {
            double low = 0, high = 16;
            // P(|T| > t) = I_{df/(df+t²)}(df/2, 1/2).
            for (int i = 0; i < 64; i++)
            {
                double mid = (low + high) / 2;
                if (BetaRegularized(df / (df + mid * mid), df / 2d, .5) > .05) low = mid;
                else high = mid;
            }
            return (low + high) / 2;
        });
    }

    private static double BetaRegularized(double x, double a, double b)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double factor = Math.Exp(LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1 - x));
        return x < (a + 1) / (a + b + 2) ? factor * BetaFraction(a, b, x) / a : 1 - factor * BetaFraction(b, a, 1 - x) / b;
    }

    private static double BetaFraction(double a, double b, double x)
    {
        const double tiny = 1e-300;
        double c = 1, d = 1 - (a + b) * x / (a + 1);
        if (Math.Abs(d) < tiny) d = tiny;
        d = 1 / d; double result = d;
        for (int m = 1; m <= 300; m++)
        {
            double aa = m * (b - m) * x / ((a + 2 * m - 1) * (a + 2 * m));
            d = 1 + aa * d; if (Math.Abs(d) < tiny) d = tiny;
            c = 1 + aa / c; if (Math.Abs(c) < tiny) c = tiny;
            d = 1 / d; result *= d * c;
            aa = -(a + m) * (a + b + m) * x / ((a + 2 * m) * (a + 2 * m + 1));
            d = 1 + aa * d; if (Math.Abs(d) < tiny) d = tiny;
            c = 1 + aa / c; if (Math.Abs(c) < tiny) c = tiny;
            d = 1 / d; double delta = d * c; result *= delta;
            if (Math.Abs(delta - 1) < 1e-13) break;
        }
        return result;
    }

    private static double LogGamma(double z)
    {
        double[] coefficients = [676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7];
        if (z < .5) return Math.Log(Math.PI) - Math.Log(Math.Sin(Math.PI * z)) - LogGamma(1 - z);
        z--; double x = .99999999999980993;
        for (int i = 0; i < coefficients.Length; i++) x += coefficients[i] / (z + i + 1);
        double t = z + 7.5;
        return .9189385332046727 + (z + .5) * Math.Log(t) - t + Math.Log(x);
    }

    public static string Explanation(SpeedReport report)
    {
        var text = new StringBuilder("측정·분석 근거\n===============\n")
            .AppendLine($"실행 ID: {report.Run.Id} · {report.Run.StartedAt:O} · {report.State}")
            .AppendLine(report.Context).AppendLine(report.Counts)
            .AppendLine("분석 규칙: " + MethodologyId).AppendLine("논문: Kalibera & Jones (2013), Rigorous Benchmarking in Reasonable Time")
            .AppendLine(Source).AppendLine("DOI: 10.1145/2464157.2464160 · 저장소의 정정된 저자 원고 기준")
            .AppendLine().AppendLine("각 새 프로젝트 시행의 평균을 동일 가중치로 비교한다. F02는 전체 작업 완료 시간이다.")
            .AppendLine("평균의 95% 구간 = 시행 평균 ± t(0.975, n−1) × 시행 표준편차 / √n. n은 호출 수가 아니다.")
            .AppendLine("시간비는 후보 평균 / 기준 평균이다. 같은 블록의 공분산을 포함한 Fieller 구간을 사용한다.")
            .AppendLine("논문 식 (5)의 독립 집단 구간을 Desk의 쌍 설계로 확장했다. q=t²/n, u=A평균²−q·sA², v=A평균·B평균−q·sAB, w=B평균²−q·sB².")
            .AppendLine("시간비 구간 = [(v−√(v²−u·w))/u, (v+√(v²−u·w))/u]. sAB는 같은 블록의 표본공분산이다. u≤0이면 유한 구간을 표시하지 않는다.")
            .AppendLine("시간 감소율 구간 = [100×(1−시간비 상한), 100×(1−시간비 하한)]. 0%를 포함하면 방향이 불확실하다.")
            .AppendLine("최소 5개 유효 시행/쌍이 없거나 유한 구간을 얻지 못하면 판단을 유보한다. 5회는 충분한 정밀도를 보장하지 않는다.")
            .AppendLine("각 비교의 95% 구간이며 여러 조건 전체의 동시 신뢰도는 아니다. 실행 블록 간 독립성과 평균의 근사 분포를 전제로 한다.")
            .AppendLine("실패·중단·미수행·정리 실패는 속도에서 제외하되 기록한다. 제외가 있으면 구간도 성공쌍에만 해당한다.")
            .AppendLine("호출 순서의 lag 1~4 상관은 진단 신호다. 신호가 없다고 독립성·사전 준비 완료를 증명하지 않는다.")
            .AppendLine("20개 이상인 호출·시행 순서에서 |ACF|>1.96/√N을 탐색 신호로 사용한다. 이 문턱은 Desk의 진단 선택이며 검정의 합격 기준이 아니다.")
            .AppendLine("균형 반복의 분산 성분: T1²=시행 내 표본분산 평균, T2²=시행 평균의 표본분산−T1²/m. 호출 수 제안=ceil(√((c2/c1)·(T1²/T2²))).")
            .AppendLine("c1은 CLI 호출 평균 비용, c2는 전체 시행 수명에서 측정 CLI 시간 합계를 뺀 비용이다. 독립성·고정 비용 근사가 필요하며 T2²≤0은 제안을 유보한다.")
            .AppendLine("반복 권장은 예비 데이터의 변동이 유지된다는 가정하에 다음 새 실험을 설계하는 추정이다. 현재 실행을 자동 연장하거나 유리한 시점에 중단하지 않는다.")
            .AppendLine("목표 정밀도는 시간비 구간의 상대 반폭 5%다. 5% 성능 개선 목표나 5% 유의수준의 승자 판정과 다르다.")
            .AppendLine("다음 실험 준비는 모든 비교의 권장값 중 최대를 버전 수 배수로 올린다. 호출·사전 실행 수를 유지하고 실패·미완료·시행 순서 신호·100회 초과에서는 자동 적용하지 않는다.")
            .AppendLine("공개 배포 바이너리를 고정하므로 재빌드 계층은 추정하지 않는다. OS 캐시·사용자 환경·PC 부하 공유도 남는다.")
            .AppendLine().AppendLine("조건별 효과와 다음 실험");
        foreach (var c in report.Comparisons)
            text.AppendLine($"{c.Experiment} {c.Condition} · {c.Candidate}: 감소율 95% 구간 {c.ConfidenceRange} · {c.Inference}. {c.Planning}");
        text.AppendLine().AppendLine("호출·시행 변동과 비용 (호출 수 제안은 독립성 확인 후 사용)");
        foreach (var r in report.Replication)
            text.AppendLine($"{r.Experiment} {r.Condition} · {r.Release}: 유효 시행 {r.Trials}, 시행당 호출 {r.CallsPerTrial}. {r.VarianceDisplay}. " +
                $"호출 비용 {SpeedReport.Time(r.CallCostMs)} ms / 시행 준비·종료 비용 {SpeedReport.Time(r.SetupCostMs)} ms. 권장 호출 {r.CallsDisplay}. {r.SequenceCheck}. {r.Note}");
        return text.ToString();
    }
}
