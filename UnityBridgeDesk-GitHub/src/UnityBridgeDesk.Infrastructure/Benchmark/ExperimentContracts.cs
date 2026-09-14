using System.Collections.Immutable;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Ai;

namespace UnityBridgeDesk.Infrastructure.Benchmark;

public sealed record ExperimentCase(string Id, string Name, string Variant, ExecutionMode Mode);
public sealed record BenchOptions(ImmutableArray<string> FixedIds, ImmutableArray<string> AiIds,
    int Repeats = 2, int Warmups = 1, int InnerCalls = 3, int ObjectCount = 1000,
    int TimeoutSeconds = 180, int PrepareTimeoutSeconds = 600, double ObservationSeconds = 3,
    bool BalancedOrder = true, bool KeepFailedClones = true, bool KeepSuccessfulClones = false,
    int RepairAttempts = 0, string Specification = "draft-v1", bool ConfirmedSpecification = false)
{
    public static BenchOptions Default => new(["F01"], ["A01"]);
    public void Validate(BenchmarkModes modes)
    {
        if (modes == BenchmarkModes.None || Repeats is < 1 or > 100 || Warmups is < 0 or > 100 || InnerCalls is < 1 or > 1000 ||
            ObjectCount is < 100 or > 10000 || ObjectCount % 100 != 0 || TimeoutSeconds is < 1 or > 86400 ||
            PrepareTimeoutSeconds is < 1 or > 3600 || ObservationSeconds is < .1 or > 120 || RepairAttempts is < 0 or > 10 ||
            FixedIds.IsDefault || AiIds.IsDefault || FixedIds.Distinct().Count() != FixedIds.Length || AiIds.Distinct().Count() != AiIds.Length ||
            FixedIds.Any(x => !new[] { "F01", "F02", "F03", "F04", "F05" }.Contains(x)) || AiIds.Any(x => !new[] { "A01", "A02", "A03" }.Contains(x)) ||
            modes.HasFlag(BenchmarkModes.FixedCommands) && FixedIds.Length == 0 || modes.HasFlag(BenchmarkModes.AiCreation) && AiIds.Length == 0)
            throw new InvalidOperationException("측정 방식별 실험과 유효한 반복·시간 설정을 선택하세요.");
    }
    public ImmutableArray<ExperimentCase> Cases(BenchmarkModes modes)
    {
        Validate(modes); var cases = new List<ExperimentCase>();
        if (modes.HasFlag(BenchmarkModes.FixedCommands)) foreach (string id in FixedIds)
        {
            var (name, variants) = id switch
            {
                "F01" => ("작은 실제 호출", new[] { "cold", "warm" }),
                "F02" => ("같은 작업의 호출 분할", new[] { "1", "10", "100" }),
                "F03" => ("응답 데이터 크기", new[] { "1024", "65536", "1048576" }),
                "F04" => ("코드 변경 후 복귀", new[] { "ready", "revision" }),
                _ => ("Play / Stop", new[] { "cycle" })
            };
            cases.AddRange(variants.Select(v => new ExperimentCase(id, name, v, ExecutionMode.FixedCommands)));
        }
        if (modes.HasFlag(BenchmarkModes.AiCreation)) foreach (string id in AiIds)
            cases.Add(new(id, id switch { "A01" => "낙하 장면", "A02" => "프리팹과 개별 예외", _ => "물리 실험 도구" }, "task", ExecutionMode.AiCreation));
        return [..cases];
    }
}
public sealed record TrialSchedule(int Order, int Repeat, BridgeReleaseRef Release, ExperimentCase Experiment, TrialId TrialId);
public static class BenchSchedule
{
    public static ImmutableArray<TrialSchedule> Build(RunPlan plan, BenchOptions options)
    {
        if (plan.Releases.Length == 0) throw new InvalidOperationException("비교할 릴리스를 선택하세요.");
        var axes = plan.Releases.Select(x => x.ComparisonAxis).Distinct().ToArray();
        if (axes.Length != 1 || axes[0] == ComparisonAxis.Unset) throw new InvalidOperationException("모든 릴리스의 비교 축을 동일하게 지정하세요.");
        if (axes[0] == ComparisonAxis.CliOnly && plan.Releases.Select(x => x.ConnectorSha256).Distinct().Count() != 1)
            throw new InvalidOperationException("CLI만 비교할 때 Connector 해시는 같아야 합니다.");
        if (plan.Releases.Any(x => x.ConnectorSha256 is null)) throw new InvalidOperationException("복제본에 적용할 Connector를 각 릴리스에 지정하세요.");
        var result = new List<TrialSchedule>();
        foreach (var experiment in options.Cases(plan.Modes))
        for (int repeat = 0; repeat < options.Repeats; repeat++)
        {
            var releases = options.BalancedOrder && repeat % 2 == 1 ? plan.Releases.Reverse() : plan.Releases;
            foreach (var release in releases) result.Add(new(result.Count + 1, repeat + 1, release, experiment, TrialId.New()));
        }
        return [..result];
    }
}
public sealed record RunOptions(string UnityExecutable, BenchOptions Benchmark, AiOptions? Ai = null,
    string Instruction = "", bool InstallConnector = false);
public sealed record TimingSample(string Name, double Milliseconds, int Bytes);
public sealed record TrialResult(ExecutionRoute Route, string Release, string Experiment, string Variant,
    int Repeat, string Specification, string EvidenceKind, string Outcome, string? Failure,
    double? PreparationMs, double? WorkMs, double? ValidationMs, double? RecoveryMs, double? EndToEndMs,
    bool? FirstPass, int Attempts, long? InputTokens, long? OutputTokens, int? ToolCalls,
    ImmutableArray<TimingSample> Samples, string Workspace, bool WorkspaceRetained)
{
    public ReleaseId ReleaseId { get; init; }
}
