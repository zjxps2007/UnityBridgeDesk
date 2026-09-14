using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Core.Tests;

[TestClass]
public sealed class ExecutionStateTests
{
    [TestMethod]
    public async Task CancelAcceptedBeforeCommitCannotBecomeSuccess()
    {
        var state = SampleData.Validating();
        Assert.IsTrue(state.TryBeginFinalizing(RunOutcome.Succeeded, SampleData.Passed));
        Assert.IsTrue(await state.RequestStopAsync(StopReason.UserCancellation));
        Assert.IsTrue(state.StopToken.IsCancellationRequested);
        Assert.IsTrue(state.TryComplete(CleanupReport.Complete));
        Assert.AreEqual(RunOutcome.Cancelled, (await state.Completion).Outcome);
        Assert.IsFalse(state.TryComplete(CleanupReport.Complete));
        Assert.IsFalse(await state.RequestStopAsync(StopReason.Timeout));
        Assert.AreEqual(1, state.Events.Count(x => x.Kind == ExecutionEventKind.Completed));
    }

    [TestMethod]
    public async Task ConcurrentCancelAndCompletionCommitExactlyOneOutcome()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var state = SampleData.Validating();
            state.TryBeginFinalizing(RunOutcome.Succeeded, SampleData.Passed);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancel = Task.Run(async () => { await start.Task; return await state.RequestStopAsync(StopReason.UserCancellation); });
            var complete = Task.Run(async () => { await start.Task; return state.TryComplete(CleanupReport.Complete); });
            start.SetResult();
            await Task.WhenAll(cancel, complete).WaitAsync(SampleData.TestTimeout);
            Assert.IsTrue(complete.Result);
            Assert.AreEqual(cancel.Result ? RunOutcome.Cancelled : RunOutcome.Succeeded, state.Snapshot.Outcome);
            Assert.AreEqual(1, state.Events.Count(x => x.Kind == ExecutionEventKind.Completed));
            CollectionAssert.AreEqual(Enumerable.Range(1, state.Events.Length).Select(x => (long)x).ToArray(),
                state.Events.Select(x => x.Sequence).ToArray());
        }
    }

    [TestMethod]
    public async Task ConcurrentFinalizersOnlyCommitOnce()
    {
        var state = SampleData.Validating();
        state.TryBeginFinalizing(RunOutcome.Succeeded, SampleData.Passed);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        { await start.Task; return state.TryComplete(CleanupReport.Complete); })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(completions).WaitAsync(SampleData.TestTimeout);
        Assert.AreEqual(1, results.Count(x => x));
    }

    [TestMethod]
    public async Task TimeoutWinsOverLaterCancellationAndPreservesOriginalFailure()
    {
        var state = new ExecutionState(SampleData.Route());
        await state.RequestStopAsync(StopReason.Timeout);
        Assert.IsFalse(await state.RequestStopAsync(StopReason.UserCancellation));
        Assert.IsFalse(state.TryAdvance(ExecutionPhase.Preparing));
        state.TryBeginFinalizing(RunOutcome.Failed, ValidationResult.Unverified, FailureCode.ExecutionFailed);
        Assert.IsFalse(state.Completion.IsCompleted);
        state.TryComplete(new(CleanupStatus.RecoveryRequired));
        Assert.AreEqual(RunOutcome.TimedOut, state.Snapshot.Outcome);
        Assert.AreEqual(FailureCode.ExecutionFailed, state.Snapshot.Failure);
        Assert.AreEqual(CleanupStatus.RecoveryRequired, state.Snapshot.Cleanup.Status);
    }

    [TestMethod]
    [DataRow(ValidationStatus.Unknown)]
    [DataRow(ValidationStatus.Failed)]
    [DataRow(ValidationStatus.Unsupported)]
    public void MissingOrUnsupportedValidationNeverBecomesSuccess(ValidationStatus validation)
    {
        var state = SampleData.Validating();
        state.TryBeginFinalizing(RunOutcome.Succeeded, new(validation, null));
        state.TryComplete(CleanupReport.Complete);
        Assert.AreEqual(RunOutcome.Failed, state.Snapshot.Outcome);
        Assert.AreEqual(FailureCode.ValidationNotPassed, state.Snapshot.Failure);
    }

    [TestMethod]
    public void UnsupportedProviderFailureIsNotLostDuringCleanup()
    {
        var state = new ExecutionState(SampleData.Route());
        state.TryBeginFinalizing(RunOutcome.Failed, new(ValidationStatus.Unsupported, null), FailureCode.UnsupportedProvider);
        state.TryComplete(new(CleanupStatus.RecoveryRequired));
        Assert.AreEqual(FailureCode.UnsupportedProvider, state.Snapshot.Failure);
        Assert.AreEqual(RunOutcome.Failed, state.Snapshot.Outcome);
    }

    [TestMethod]
    public void UnverifiedCleanupCannotBecomeSuccess()
    {
        var state = SampleData.Validating();
        state.TryBeginFinalizing(RunOutcome.Succeeded, SampleData.Passed);
        state.TryComplete(new(CleanupStatus.Unknown));
        Assert.AreEqual(RunOutcome.Failed, state.Snapshot.Outcome);
        Assert.AreEqual(FailureCode.CleanupIncomplete, state.Snapshot.Failure);
    }

    [TestMethod]
    public void InvalidTransitionsAndUnnumberedRetriesAreRejected()
    {
        var state = new ExecutionState(SampleData.Route());
        Assert.IsFalse(state.TryAdvance(ExecutionPhase.Running));
        Assert.IsFalse(state.TryBeginFinalizing(RunOutcome.Succeeded, SampleData.Passed));
        Assert.IsFalse(state.TryComplete(CleanupReport.Complete));
        var ai = SampleData.Validating(SampleData.Route(SampleData.Draft(ToolKind.AiWork).Freeze()));
        Assert.IsFalse(ai.TryBeginRetry(3));
        Assert.IsTrue(ai.TryBeginRetry(2));
        Assert.AreEqual(2, ai.Snapshot.Attempt);
        Assert.IsFalse(SampleData.Validating().TryBeginRetry(2));
    }

    [TestMethod]
    public async Task CancellationCallbackMayObserveStateWithoutDeadlock()
    {
        var state = SampleData.Validating();
        StopReason seen = StopReason.None;
        using var registration = state.StopToken.Register(() => seen = state.Snapshot.StopReason);
        Assert.IsTrue(await state.RequestStopAsync(StopReason.UserCancellation).AsTask().WaitAsync(SampleData.TestTimeout));
        Assert.AreEqual(StopReason.UserCancellation, seen);
    }

    [TestMethod]
    public async Task StopAfterCommittedSuccessIsRejectedWithoutChangingResult()
    {
        var state = SampleData.Validating();
        state.TryBeginFinalizing(RunOutcome.Succeeded, SampleData.Passed);
        state.TryComplete(CleanupReport.Complete);
        Assert.IsFalse(await state.RequestStopAsync(StopReason.UserCancellation));
        Assert.AreEqual(RunOutcome.Succeeded, state.Snapshot.Outcome);
        Assert.IsFalse(state.StopToken.IsCancellationRequested);
    }

    [TestMethod]
    public async Task FaultingCancellationCallbackCannotUndoAcceptedCancellation()
    {
        var state = SampleData.Validating();
        using var registration = state.StopToken.Register(() => throw new InvalidOperationException("test callback failure"));
        await Assert.ThrowsAsync<AggregateException>(() => state.RequestStopAsync(StopReason.UserCancellation).AsTask());
        state.TryBeginFinalizing(RunOutcome.Succeeded, SampleData.Passed);
        state.TryComplete(CleanupReport.Complete);
        Assert.AreEqual(RunOutcome.Cancelled, state.Snapshot.Outcome);
    }
}
