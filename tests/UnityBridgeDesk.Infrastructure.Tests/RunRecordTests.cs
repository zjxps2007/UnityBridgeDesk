using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Storage;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class RunRecordTests
{
    private static ExecutionSpec Spec(RunPlan plan, string directory) => new(SampleData.Route(plan), plan.Releases[0].Id,
        Path.Combine(directory, "work"), Path.Combine(directory, "artifacts"));

    [TestMethod]
    public async Task PersistedPlanIsImmutableAndRoundTripsFrozenValues()
    {
        var store = new RunRecordStore(SampleData.TestDirectory());
        var draft = SampleData.Draft();
        var plan = draft.Freeze();
        Assert.AreEqual(SaveStatus.Saved, (await store.CreatePlanAsync(plan)).Status);
        draft.Project = SampleData.Project();
        draft.Releases.Clear();
        Assert.AreEqual(SaveStatus.AlreadyExists, (await store.CreatePlanAsync(plan)).Status);
        var loaded = await store.ReadPlanAsync(plan.RunId);
        Assert.AreEqual(ReadStatus.Current, loaded.Status);
        Assert.AreEqual(plan.Project, loaded.Value!.Project);
        Assert.AreEqual(plan.Releases[0], loaded.Value.Releases[0]);
    }

    [TestMethod]
    public async Task ForeignAndDuplicateEventsCannotCorruptExecutionRecord()
    {
        var directory = SampleData.TestDirectory();
        var store = new RunRecordStore(directory);
        var plan = SampleData.Draft().Freeze();
        await store.CreatePlanAsync(plan);
        var spec = Spec(plan, directory);
        var state = SampleData.Validating(spec.Route);
        await using var writer = await store.CreateExecutionAsync(plan, spec, state.Events[0]);
        var expected = state.Events[1];
        var foreign = expected with { Snapshot = expected.Snapshot with { Route = expected.Snapshot.Route with { ProjectId = ProjectId.New() } } };
        Assert.AreEqual(SaveStatus.InvalidData, (await writer.AppendAsync(foreign)).Status);
        Assert.AreEqual(SaveStatus.Saved, (await writer.AppendAsync(expected)).Status);
        Assert.AreEqual(SaveStatus.InvalidData, (await writer.AppendAsync(expected)).Status);
        var record = await store.ReadExecutionAsync(plan, spec.Route.ExecutionId);
        Assert.AreEqual(2, record.Value!.Events.Length);
        Assert.AreEqual(plan.Project.Id, record.Value.Events[^1].Snapshot.Route.ProjectId);
    }

    [TestMethod]
    public async Task FailedAppendCanRetrySameSequenceWithoutLosingEvent()
    {
        var files = new FaultingFiles();
        var directory = SampleData.TestDirectory();
        var store = new RunRecordStore(directory, files);
        var plan = SampleData.Draft().Freeze();
        await store.CreatePlanAsync(plan);
        var spec = Spec(plan, directory);
        var state = SampleData.Validating(spec.Route);
        await using var writer = await store.CreateExecutionAsync(plan, spec, state.Events[0]);
        files.Fault = InjectedFault.BeforeCommit;
        Assert.AreEqual(SaveStatus.IoFailure, (await writer.AppendAsync(state.Events[1])).Status);
        Assert.AreEqual(1, (await store.ReadExecutionAsync(plan, spec.Route.ExecutionId)).Value!.Events.Length);
        files.Fault = InjectedFault.None;
        Assert.AreEqual(SaveStatus.Saved, (await writer.AppendAsync(state.Events[1])).Status);
        Assert.AreEqual(2, (await store.ReadExecutionAsync(plan, spec.Route.ExecutionId)).Value!.Events.Length);
    }

    [TestMethod]
    public async Task TerminalResultRoundTripsAndRejectsLateEvents()
    {
        var directory = SampleData.TestDirectory();
        var store = new RunRecordStore(directory);
        var plan = SampleData.Draft().Freeze();
        await store.CreatePlanAsync(plan);
        var spec = Spec(plan, directory);
        var state = SampleData.Validating(spec.Route);
        await state.RequestStopAsync(StopReason.UserCancellation);
        state.TryBeginFinalizing(RunOutcome.Cancelled, ValidationResult.Unverified);
        state.TryComplete(CleanupReport.Complete);
        await using var writer = await store.CreateExecutionAsync(plan, spec, state.Events[0]);
        foreach (var message in state.Events.Skip(1)) Assert.AreEqual(SaveStatus.Saved, (await writer.AppendAsync(message)).Status);
        Assert.AreEqual(SaveStatus.InvalidData, (await writer.AppendAsync(state.Events[^1] with { Sequence = state.Events.Length + 1 })).Status);
        var record = (await store.ReadExecutionAsync(plan, spec.Route.ExecutionId)).Value!;
        Assert.AreEqual(RunOutcome.Cancelled, record.Events[^1].Snapshot.Outcome);
        Assert.IsFalse(record.NeedsInterruptionReview);
    }

    [TestMethod]
    public async Task UnfinishedRecordRequiresReviewAndCannotBeSilentlyResumed()
    {
        var directory = SampleData.TestDirectory();
        var store = new RunRecordStore(directory);
        var plan = SampleData.Draft().Freeze();
        await store.CreatePlanAsync(plan);
        var spec = Spec(plan, directory);
        var state = new ExecutionState(spec.Route);
        var writer = await store.CreateExecutionAsync(plan, spec, state.Events[0]);
        await Assert.ThrowsAsync<IOException>(() => store.CreateExecutionAsync(plan, spec, state.Events[0]));
        await writer.DisposeAsync();
        await Assert.ThrowsAsync<IOException>(() => store.CreateExecutionAsync(plan, spec, state.Events[0]));
        var record = (await store.ReadExecutionAsync(plan, spec.Route.ExecutionId)).Value!;
        Assert.IsTrue(record.NeedsInterruptionReview);
        Assert.IsNull(record.Events[^1].Snapshot.Outcome);
    }

    [TestMethod]
    public async Task UnpersistedOrAlteredPlanCannotCreateExecution()
    {
        var directory = SampleData.TestDirectory();
        var store = new RunRecordStore(directory);
        var plan = SampleData.Draft().Freeze();
        var spec = Spec(plan, directory);
        var created = new ExecutionState(spec.Route).Events[0];
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateExecutionAsync(plan, spec, created));
        await store.CreatePlanAsync(plan);
        var changed = new RunPlan(plan.RunId, plan.CreatedAtUtc, plan.Tool, plan.Project with { UnityBuild = "different" },
            plan.Releases, plan.AiProfile, plan.Modes, plan.Budget, plan.InputSha256, plan.ExperimentSpecSha256);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateExecutionAsync(changed, spec, created));
    }

    [TestMethod]
    public async Task CorruptExecutionDoesNotPreventReadingAnotherRun()
    {
        var directory = SampleData.TestDirectory();
        var store = new RunRecordStore(directory);
        var plan = SampleData.Draft().Freeze();
        var other = SampleData.Draft().Freeze();
        await store.CreatePlanAsync(plan);
        await store.CreatePlanAsync(other);
        var spec = Spec(plan, directory);
        await using var writer = await store.CreateExecutionAsync(plan, spec, new ExecutionState(spec.Route).Events[0]);
        await File.WriteAllTextAsync(Path.Combine(directory, plan.RunId.Value.ToString("N"), "executions",
            spec.Route.ExecutionId.Value.ToString("N"), "record.json"), "corrupt");
        Assert.AreEqual(ReadStatus.Corrupt, (await store.ReadExecutionAsync(plan, spec.Route.ExecutionId)).Status);
        Assert.AreEqual(ReadStatus.Current, (await store.ReadPlanAsync(other.RunId)).Status);
    }
}
