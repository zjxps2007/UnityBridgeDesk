using UnityBridgeDesk.Core.Contracts;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Tests;

[assembly: Parallelize(Workers = 4, Scope = ExecutionScope.MethodLevel)]

namespace UnityBridgeDesk.Core.Tests;

[TestClass]
public sealed class ModelsTests
{
    [TestMethod]
    public void FrozenPlanSurvivesSelectionAndNestedSettingsChanges()
    {
        var draft = SampleData.Draft();
        var plan = draft.Freeze();
        var originalProject = plan.Project;
        var releaseId = plan.Releases[0].Id;
        var originalProfile = plan.AiProfile;
        draft.Project = SampleData.Project();
        draft.Releases[0] = draft.Releases[0] with { Label = "different" };
        draft.Releases.Clear();
        draft.AiProfile = draft.AiProfile! with { Model = "different-model" };
        draft.Budget = new(TimeSpan.FromMinutes(1), 3, 100, 20);
        draft.Modes = BenchmarkModes.AiCreation;
        Assert.AreEqual(originalProject, plan.Project);
        Assert.AreEqual(releaseId, plan.Releases[0].Id);
        Assert.AreEqual("0.2.1", plan.Releases[0].Label);
        Assert.AreEqual(originalProfile, plan.AiProfile);
        Assert.IsNull(plan.Budget.Timeout);
        Assert.AreEqual(BenchmarkModes.FixedCommands | BenchmarkModes.AiCreation, plan.Modes);
    }

    [TestMethod]
    [DataRow(BenchmarkModes.FixedCommands)]
    [DataRow(BenchmarkModes.AiCreation)]
    [DataRow(BenchmarkModes.FixedCommands | BenchmarkModes.AiCreation)]
    public void BothBenchmarkModesAreIndependentlySelectable(BenchmarkModes modes)
    {
        Assert.AreEqual(modes, SampleData.Draft(modes: modes).Freeze().Modes);
    }

    [TestMethod]
    [DataRow(BenchmarkModes.None)]
    [DataRow((BenchmarkModes)8)]
    public void BenchmarkRejectsEmptyOrUnknownModes(BenchmarkModes modes)
    {
        Assert.Throws<ArgumentException>(() => SampleData.Draft(modes: modes).Freeze());
    }

    [TestMethod]
    public void DisabledModeAndForeignProjectCannotCreateExecution()
    {
        var plan = SampleData.Draft(modes: BenchmarkModes.FixedCommands).Freeze();
        var spec = new ExecutionSpec(SampleData.Route(plan, ExecutionMode.AiCreation), plan.Releases[0].Id,
            Path.GetFullPath("work"), Path.GetFullPath("output"));
        Assert.Throws<ArgumentException>(() => spec.ValidateAgainst(plan));
        spec = spec with { Route = SampleData.Route(plan) with { ProjectId = ProjectId.New() } };
        Assert.Throws<ArgumentException>(() => spec.ValidateAgainst(plan));
    }

    [TestMethod]
    public void UnobservedVersionsAndUsageRemainNull()
    {
        var plan = SampleData.Draft().Freeze();
        Assert.IsNull(plan.Releases[0].CliSha256);
        Assert.IsNull(plan.Releases[0].ObservedCliVersion);
        Assert.IsNull(plan.AiProfile!.Model);
        var measurements = new ExecutionMeasurements(null, null, null, null);
        measurements.Validate();
        Assert.IsNull(measurements.ElapsedMilliseconds);
        Assert.IsNull(measurements.InputTokens);
        Assert.Throws<ArgumentException>(() => new ExecutionMeasurements(double.NaN, null, null, null).Validate());
    }
}
