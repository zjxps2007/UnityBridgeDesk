using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace UnityBridgeDesk.Core.Models;

// Mutable input belongs to the view. Never pass a RunDraft to a running adapter.
public sealed class RunDraft
{
    public required ToolKind Tool { get; set; }
    public required ProjectRef Project { get; set; }
    public List<BridgeReleaseRef> Releases { get; } = [];
    public AiProfileRef? AiProfile { get; set; }
    public BenchmarkModes Modes { get; set; }
    public ExecutionBudget Budget { get; set; } = new(null, null, null, null);
    public string? InputSha256 { get; set; }
    public string? ExperimentSpecSha256 { get; set; }

    public RunPlan Freeze(TimeProvider? clock = null) => new(RunId.New(),
        (clock ?? TimeProvider.System).GetUtcNow(), Tool, Project,
        Releases.ToImmutableArray(), AiProfile, Modes, Budget, InputSha256, ExperimentSpecSha256);
}

public sealed record RunPlan
{
    public RunId RunId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public ToolKind Tool { get; }
    public ProjectRef Project { get; }
    public ImmutableArray<BridgeReleaseRef> Releases { get; }
    public AiProfileRef? AiProfile { get; }
    public BenchmarkModes Modes { get; }
    public ExecutionBudget Budget { get; }
    public string? InputSha256 { get; }
    public string? ExperimentSpecSha256 { get; }

    [JsonConstructor]
    public RunPlan(RunId runId, DateTimeOffset createdAtUtc, ToolKind tool, ProjectRef project,
        ImmutableArray<BridgeReleaseRef> releases, AiProfileRef? aiProfile,
        BenchmarkModes modes, ExecutionBudget budget, string? inputSha256, string? experimentSpecSha256)
    {
        ContractGuard.Id(runId.Value);
        ContractGuard.Known(tool);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(budget);
        project.Validate();
        budget.Validate();
        aiProfile?.Validate();
        if (createdAtUtc == default || releases.IsDefault) throw new ArgumentException("Incomplete run plan.");
        foreach (var release in releases) release.Validate();
        if (releases.Select(x => x.Id).Distinct().Count() != releases.Length)
            throw new ArgumentException("Release identities must be unique.");
        if ((modes & ~(BenchmarkModes.FixedCommands | BenchmarkModes.AiCreation)) != 0 ||
            (tool == ToolKind.Benchmark ? modes == BenchmarkModes.None : modes != BenchmarkModes.None))
            throw new ArgumentException("Benchmark modes do not match the tool.");
        BridgeReleaseRef.ValidateHash(inputSha256);
        BridgeReleaseRef.ValidateHash(experimentSpecSha256);
        RunId = runId; CreatedAtUtc = createdAtUtc; Tool = tool; Project = project;
        Releases = releases; AiProfile = aiProfile; Modes = modes; Budget = budget;
        InputSha256 = inputSha256; ExperimentSpecSha256 = experimentSpecSha256;
    }
}

// A route is the complete identity of one worker stream, including a benchmark's specific mode.
public sealed record ExecutionRoute(RunId RunId, ExecutionId ExecutionId, ProjectId ProjectId,
    ToolKind Tool, ExecutionMode Mode, TrialId? TrialId)
{
    public void Validate()
    {
        ContractGuard.Id(RunId.Value); ContractGuard.Id(ExecutionId.Value); ContractGuard.Id(ProjectId.Value);
        ContractGuard.Known(Tool); ContractGuard.Known(Mode);
        bool valid = Tool switch
        {
            ToolKind.Installation => Mode == ExecutionMode.Installation && TrialId is null,
            ToolKind.AiWork => Mode == ExecutionMode.AiWork && TrialId is null,
            ToolKind.Benchmark => Mode is ExecutionMode.FixedCommands or ExecutionMode.AiCreation && TrialId is not null,
            _ => false
        };
        if (!valid) throw new ArgumentException("Tool, mode and trial do not match.");
        if (TrialId is { } trial) ContractGuard.Id(trial.Value);
    }
}

public sealed record ExecutionSpec(ExecutionRoute Route, ReleaseId? Release,
    string WorkingDirectory, string OutputDirectory)
{
    public void ValidateAgainst(RunPlan plan)
    {
        Route.Validate();
        if (Route.RunId != plan.RunId || Route.ProjectId != plan.Project.Id || Route.Tool != plan.Tool)
            throw new ArgumentException("Execution does not belong to this plan.");
        if (Route.Mode == ExecutionMode.FixedCommands && !plan.Modes.HasFlag(BenchmarkModes.FixedCommands) ||
            Route.Mode == ExecutionMode.AiCreation && !plan.Modes.HasFlag(BenchmarkModes.AiCreation))
            throw new ArgumentException("Execution mode is disabled in the frozen plan.");
        if (Release is { } release && !plan.Releases.Any(x => x.Id == release))
            throw new ArgumentException("Execution release is absent from the frozen plan.");
        if (!Path.IsPathFullyQualified(WorkingDirectory) || !Path.IsPathFullyQualified(OutputDirectory))
            throw new ArgumentException("Execution directories must be absolute.");
    }
}
