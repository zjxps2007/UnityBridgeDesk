using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record ResearchSettings(string Stage = "exploratory", string Question = "", double TolerancePercent = 1,
    bool QuietProgress = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? StudyGroup = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SessionNote = null)
{
    public void Validate(SpeedOptions options)
    {
        if (Stage is not ("exploratory" or "pilot" or "confirmatory" or "aa" or "sensitivity") || Question is null || Question.Length > 2000 ||
            !double.IsFinite(TolerancePercent) || TolerancePercent is <= 0 or > 20)
            throw new ArgumentException("연구 목적과 허용 차이(0% 초과~20%)를 확인하세요.");
        if (StudyGroup?.Length > 120 || SessionNote?.Length > 300) throw new ArgumentException("연구 묶음은 120자, 세션 메모는 300자 이내로 입력하세요.");
        if (Stage == "sensitivity" && (options.Selected.Length != 1 || options.Selected[0] != "F01"))
            throw new ArgumentException("지연 감지 검증은 F01만 선택하세요. B 대상의 동일 요청에 100ms 대기를 추가합니다.");
        if (Stage is "confirmatory" or "aa" or "sensitivity" && (options.Repeats < 5 || string.IsNullOrWhiteSpace(Question)))
            throw new ArgumentException("본시험·측정기 검증은 연구 질문·판단 기준과 새 프로젝트 5회 이상이 필요합니다. 5회가 충분성을 보장하지는 않습니다.");
    }
}
public sealed record ResearchPlan(string Payload, string Sha256);

public static class SpeedResearch
{
    public const string Rules = "desk-research-v2";
    public const string ScheduleId = "randomized-condition-rounds-latin-target-cycles-v2";
    public static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static SpeedRelease[] AaTargets(SpeedRelease release)
    {
        if (release.OfficialUnity is not null || release.GoUnity is not null || release.SourceTag is not null)
            throw new ArgumentException("A/A 검증에는 원본 UnityBridge 릴리스 1개를 선택하세요.");
        return [release with { Tag = release.Tag + " [A]", SourceTag = release.Tag },
            release with { Tag = release.Tag + " [B]", SourceTag = release.Tag }];
    }
    public static void ValidateTargets(SpeedOptions options, SpeedRelease[] releases)
    {
        if (releases.Length < 2) throw new ArgumentException("비교 대상은 2개 이상이어야 합니다.");
        bool aa = options.Research?.Stage is "aa" or "sensitivity";
        if (!aa && releases.Any(r => r.SourceTag is not null)) throw new ArgumentException("A/A 대상은 측정기 검증에서만 사용합니다.");
        if (aa && (releases.Length != 2 || releases[0].SourceTag is null || releases[1].SourceTag != releases[0].SourceTag ||
            releases[0].Tag != releases[0].SourceTag + " [A]" || releases[1].Tag != releases[1].SourceTag + " [B]" ||
            releases.Any(r => r.OfficialUnity is not null || r.GoUnity is not null) ||
            (releases[0] with { Tag = releases[1].Tag }) != releases[1]))
            throw new ArgumentException("A/A 검증은 같은 릴리스 원본과 파일 해시의 독립된 A/B 시행이어야 합니다.");
        if (options.Research?.Stage is "confirmatory" or "aa" or "sensitivity" && options.Repeats % releases.Length != 0)
            throw new ArgumentException("본시험·A/A 검증은 실행 순서를 균등하게 하도록 실험 횟수를 대상 수의 배수로 설정하세요.");
    }
    private static object Identity(SpeedRun run) => new { run.Id, run.StartedAt, run.Schema, run.Evidence, run.Options,
        run.Releases, run.Plan, run.Local, run.HostDescription };
    public static ResearchPlan Freeze(SpeedRun run, string fixtureHash, string workerHash, RuntimeManifest? manifest = null) => FreezeWithEnvironment(run, fixtureHash, workerHash,
        JsonSerializer.Serialize(new { dotnet = Environment.Version.ToString(), os = Environment.OSVersion.ToString(),
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            power = ResearchHost.PowerScheme(), uptimeMs = Environment.TickCount64,
            bootTimeEstimateUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64) }, SpeedProtocol.Json), manifest);
    internal static ResearchPlan FreezeWithEnvironment(SpeedRun run, string fixtureHash, string workerHash, string environment, RuntimeManifest? manifest = null)
    {
        string payload = JsonSerializer.Serialize(new { rules = Rules, frozenAt = DateTimeOffset.UtcNow,
            identity = Identity(run), fixtureSha256 = fixtureHash, workerSha256 = workerHash,
            appVersion = typeof(SpeedResearch).Assembly.GetName().Version?.ToString(), environment,
            session = new { id = run.Id, studyGroup = run.Options.Research?.StudyGroup, note = run.Options.Research?.SessionNote,
                startedAt = run.StartedAt, unit = "one run; separate runs are not proof of independence" },
            schedule = ScheduleId, runtimeManifest = manifest,
            workload = "desk-common-work-v2; F02 host checks observed scene; sensitivity adds B-only actual delay",
            timing = "CLI start to exit + stdout/stderr drain; parsing, hashing, Unity preparation and cleanup excluded",
            exclusion = "success + verified response + verified cleanup; failed/missing kept; no automatic retry or outlier deletion",
            analysis = SpeedStatistics.MethodologyId, confidence = .95, independentUnit = "fresh-project trial",
            multiplicity = "pointwise intervals; no simultaneous/global superiority claim",
            stopping = "fixed planned trials; cancellation and cleanup failure stop; never precision-based optional stopping" }, SpeedProtocol.Json);
        return new(payload, Digest(payload));
    }
    public static string PlanStatus(SpeedRun run)
    {
        if (run.ResearchPlan is not { } plan) return "사전 고정 계획 없음 (과거 기록)";
        if (plan.Sha256 != Digest(plan.Payload)) return "계획 무결성 오류";
        try
        {
            using var json = JsonDocument.Parse(plan.Payload);
            var identity = JsonSerializer.SerializeToElement(Identity(run), SpeedProtocol.Json);
            return json.RootElement.GetProperty("rules").GetString() is Rules or "desk-research-v1" &&
                JsonElement.DeepEquals(json.RootElement.GetProperty("identity"), identity)
                ? "사전 고정 계획 일치" : "계획과 실행 설정 불일치";
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return "계획 형식 오류"; }
    }
    public static string Assessment(SpeedReport report, ReportComparison comparison)
    {
        if (report.Run.Options.Research?.Stage == "sensitivity") return ResearchOutcomes.Sensitivity(report, comparison);
        if (report.Run.Options.Research?.Stage != "aa") return comparison.Inference;
        var settings = report.Run.Options.Research;
        if (PlanStatus(report.Run) != "사전 고정 계획 일치") return "A/A 검증 유보 · 사전 계획 확인 필요";
        try { ValidateTargets(report.Run.Options, report.Run.Releases); }
        catch (ArgumentException) { return "A/A 검증 유보 · 대상 동일성 오류"; }
        if (!comparison.CompleteRun || comparison.Valid != comparison.Planned || comparison.Evidence is not { Status: "bounded" } ci)
            return "A/A 자료 부족 · 실패·미완료·구간을 확인하세요";
        if (ci.SequenceWarning || ci.Count < 20) return "A/A 판정 유보 · 시행 순서 진단과 20쌍 이상의 예비 자료 필요";
        double lo = 1 - settings.TolerancePercent / 100, hi = 1 + settings.TolerancePercent / 100;
        if (ci.Lower >= lo && ci.Upper <= hi) return $"A/A 시간비 구간이 사전 허용 ±{settings.TolerancePercent:G}% 안 · 이 조건에 한정";
        if (ci.Upper < lo || ci.Lower > hi) return "A/A 허용 범위 밖 차이 관찰 · 실행 순서·환경·계측 영향 확인";
        return "A/A 검증 불충분 · 구간이 허용 경계에 걸침 (0% 포함만으로 동등하지 않음)";
    }
    public static string Summary(SpeedReport report) => PlanStatus(report.Run) + "\n" +
        (report.Run.Options.Research is { } r ? $"목적: {r.Stage} · {r.Question}\n진행 갱신 최소화: {r.QuietProgress}" : "탐색 측정") +
        "\n" + ResearchOutcomes.SessionSummary(report.Run) + "\n" + ResearchOutcomes.CompletionText(report) +
        "\n" + string.Join("\n", report.Comparisons.Select(c => $"{c.Experiment} {c.Condition} / {c.Candidate}: {Assessment(report, c)}")) +
        "\n관찰 범위의 성공 조건부 시간이다. 실패율·제외 사유를 함께 확인한다. OS 캐시·온도·클럭·외부 부하와 제삼자 재현은 별도 검증이 필요하다.";
}

internal static class ResearchHost
{
    public static string PowerScheme()
    {
        if (!OperatingSystem.IsWindows()) return "unavailable";
        IntPtr ptr = IntPtr.Zero;
        try { return PowerGetActiveScheme(IntPtr.Zero, out ptr) == 0 ? System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>(ptr).ToString() : "unavailable"; }
        finally { if (ptr != IntPtr.Zero) LocalFree(ptr); }
    }
    [System.Runtime.InteropServices.DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
