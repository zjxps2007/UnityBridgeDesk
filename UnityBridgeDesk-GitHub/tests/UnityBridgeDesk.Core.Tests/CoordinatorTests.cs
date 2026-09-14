using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Core.Tests;

[TestClass]
public sealed class CoordinatorTests
{
    [TestMethod]
    public async Task BenchmarkKeepsItsLeaseBetweenTrialsAndWaitingJobCanCancel()
    {
        var coordinator = new ExecutionCoordinator();
        var benchmark = SampleData.Draft().Freeze();
        using var lease = await coordinator.AcquireAsync(benchmark);
        using var cancellation = new CancellationTokenSource();
        var pending = coordinator.AcquireAsync(SampleData.Draft(ToolKind.Installation).Freeze(), cancellation.Token).AsTask();
        // A trial's lifecycle has no authority to release its parent benchmark lease.
        var trial = SampleData.Validating(SampleData.Route(benchmark));
        trial.TryBeginFinalizing(RunOutcome.Succeeded, SampleData.Passed);
        trial.TryComplete(CleanupReport.Complete);
        Assert.IsFalse(pending.IsCompleted);
        Assert.AreEqual(benchmark.RunId, coordinator.Snapshot.ActiveRun);
        Assert.AreEqual(WaitReason.BenchmarkExclusive, coordinator.Snapshot.Waiting.Single().Reason);
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending.WaitAsync(SampleData.TestTimeout));
        Assert.IsEmpty(coordinator.Snapshot.Waiting);
        Assert.AreEqual(benchmark.RunId, coordinator.Snapshot.ActiveRun);
    }

    [TestMethod]
    public async Task ReleasingLeaseAllowsNextRunAndDoubleDisposeIsHarmless()
    {
        var coordinator = new ExecutionCoordinator();
        var first = SampleData.Draft(ToolKind.AiWork).Freeze();
        var second = SampleData.Draft(ToolKind.Installation).Freeze();
        var firstLease = await coordinator.AcquireAsync(first);
        var pending = coordinator.AcquireAsync(second).AsTask();
        Assert.IsFalse(pending.IsCompleted);
        firstLease.Dispose();
        firstLease.Dispose();
        using var secondLease = await pending.WaitAsync(SampleData.TestTimeout);
        Assert.AreEqual(second.RunId, coordinator.Snapshot.ActiveRun);
    }

    [TestMethod]
    public async Task DuplicateRunAndSameProjectAliasAreDetected()
    {
        var coordinator = new ExecutionCoordinator();
        var first = SampleData.Draft(ToolKind.AiWork).Freeze();
        using var lease = await coordinator.AcquireAsync(first);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await coordinator.AcquireAsync(first));
        var draft = SampleData.Draft(ToolKind.Installation);
        draft.Project = first.Project with { Id = ProjectId.New(), RootPath = first.Project.RootPath.ToUpperInvariant() + Path.DirectorySeparatorChar };
        using var cancellation = new CancellationTokenSource();
        var pending = coordinator.AcquireAsync(draft.Freeze(), cancellation.Token).AsTask();
        Assert.AreEqual(WaitReason.SameProjectBusy, coordinator.Snapshot.Waiting.Single().Reason);
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending.WaitAsync(SampleData.TestTimeout));
    }

    [TestMethod]
    public async Task PreCancelledRequestDoesNotConsumeExecutionSlot()
    {
        var coordinator = new ExecutionCoordinator();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await coordinator.AcquireAsync(SampleData.Draft().Freeze(), cancellation.Token));
        using var lease = await coordinator.AcquireAsync(SampleData.Draft().Freeze()).AsTask().WaitAsync(SampleData.TestTimeout);
        Assert.IsEmpty(coordinator.Snapshot.Waiting);
    }
}
