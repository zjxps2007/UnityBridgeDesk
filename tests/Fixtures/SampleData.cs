using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Tests;

internal static class SampleData
{
    internal static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    internal static ProjectRef Project() => new(ProjectId.New(), "테스트 프로젝트", Path.GetFullPath("fixture-project"), null);
    internal static BridgeReleaseRef Release() => new(ReleaseId.New(), "0.2.1", null, null, null, null, null, null);
    internal static AiProfileRef Profile() => new(AiProfileId.New(), "unimplemented-test-provider", null, null, CredentialId.New());
    internal static RunDraft Draft(ToolKind tool = ToolKind.Benchmark, BenchmarkModes modes = BenchmarkModes.FixedCommands | BenchmarkModes.AiCreation)
    {
        var draft = new RunDraft { Tool = tool, Project = Project(), Modes = tool == ToolKind.Benchmark ? modes : BenchmarkModes.None,
            AiProfile = Profile() };
        draft.Releases.Add(Release());
        return draft;
    }
    internal static ExecutionRoute Route(RunPlan? plan = null, ExecutionMode? mode = null)
    {
        plan ??= Draft().Freeze();
        var actualMode = mode ?? plan.Tool switch
        {
            ToolKind.Installation => ExecutionMode.Installation,
            ToolKind.AiWork => ExecutionMode.AiWork,
            _ => ExecutionMode.FixedCommands
        };
        return new(plan.RunId, ExecutionId.New(), plan.Project.Id, plan.Tool, actualMode,
            plan.Tool == ToolKind.Benchmark ? TrialId.New() : null);
    }
    internal static ExecutionState Validating(ExecutionRoute? route = null)
    {
        var state = new ExecutionState(route ?? Route());
        if (!state.TryAdvance(ExecutionPhase.Preparing) || !state.TryAdvance(ExecutionPhase.Running) || !state.TryAdvance(ExecutionPhase.Validating))
            throw new InvalidOperationException("Fixture did not reach validation.");
        return state;
    }
    internal static ValidationResult Passed => new(ValidationStatus.Passed, new string('a', 64));
    internal static string TestDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Tests must run inside the solution workspace.");
        var path = Path.Combine(directory.FullName, ".cache", "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
