using UnityBridgeDesk.Core.Contracts;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Core.Tests;

[TestClass]
public sealed class WorkerEventGateTests
{
    [TestMethod]
    [DataRow("run")]
    [DataRow("execution")]
    [DataRow("project")]
    [DataRow("trial")]
    [DataRow("mode")]
    [DataRow("tool")]
    public void ForeignEventsNeverAdvanceExpectedStream(string changed)
    {
        var state = new ExecutionState(SampleData.Route());
        var created = state.Events[0];
        var route = created.Snapshot.Route;
        var foreign = changed switch
        {
            "run" => route with { RunId = RunId.New() },
            "execution" => route with { ExecutionId = ExecutionId.New() },
            "project" => route with { ProjectId = ProjectId.New() },
            "trial" => route with { TrialId = TrialId.New() },
            "mode" => route with { Mode = ExecutionMode.AiCreation },
            _ => route with { Tool = ToolKind.AiWork, Mode = ExecutionMode.AiWork, TrialId = null }
        };
        var gate = new WorkerEventGate(route);
        Assert.AreEqual(EventAcceptance.WrongRoute, gate.Accept(created with { Snapshot = created.Snapshot with { Route = foreign } }));
        Assert.AreEqual(EventAcceptance.Accepted, gate.Accept(created));
    }

    [TestMethod]
    public void DuplicateGapAndUnknownVersionAreRejectedWithoutConsumingSequence()
    {
        var state = SampleData.Validating();
        var gate = new WorkerEventGate(state.Snapshot.Route);
        Assert.AreEqual(EventAcceptance.InvalidEnvelope, gate.Accept(state.Events[0] with { SchemaVersion = 99 }));
        Assert.AreEqual(EventAcceptance.Accepted, gate.Accept(state.Events[0]));
        Assert.AreEqual(EventAcceptance.OutOfSequence, gate.Accept(state.Events[0]));
        Assert.AreEqual(EventAcceptance.OutOfSequence, gate.Accept(state.Events[2]));
        Assert.AreEqual(EventAcceptance.Accepted, gate.Accept(state.Events[1]));
        Assert.AreEqual(EventAcceptance.Accepted, gate.Accept(state.Events[2]));
    }

    [TestMethod]
    public async Task ValidOrderedStreamClosesAfterOneFinalResult()
    {
        var state = SampleData.Validating();
        await state.RequestStopAsync(StopReason.UserCancellation);
        state.TryBeginFinalizing(RunOutcome.Cancelled, ValidationResult.Unverified);
        state.TryComplete(CleanupReport.Complete);
        var gate = new WorkerEventGate(state.Snapshot.Route);
        foreach (var item in state.Events) Assert.AreEqual(EventAcceptance.Accepted, gate.Accept(item));
        Assert.AreEqual(EventAcceptance.StreamClosed, gate.Accept(state.Events[^1] with { Sequence = state.Events.Length + 1 }));
    }

    [TestMethod]
    public void ForgedSuccessAndSkippedPhaseAreRejected()
    {
        var state = new ExecutionState(SampleData.Route());
        var created = state.Events[0];
        var gate = new WorkerEventGate(state.Snapshot.Route);
        gate.Accept(created);
        var skipped = created with { Sequence = 2, Kind = ExecutionEventKind.PhaseChanged,
            Snapshot = created.Snapshot with { Phase = ExecutionPhase.Validating } };
        Assert.AreEqual(EventAcceptance.InvalidTransition, gate.Accept(skipped));
        var forged = created with { Sequence = 2, Kind = ExecutionEventKind.Completed,
            Snapshot = created.Snapshot with { Phase = ExecutionPhase.Terminal, Outcome = RunOutcome.Succeeded } };
        Assert.AreEqual(EventAcceptance.InvalidEnvelope, gate.Accept(forged));
        var skippedWork = created with { Sequence = 2, Kind = ExecutionEventKind.PhaseChanged,
            Snapshot = created.Snapshot with { Phase = ExecutionPhase.Finalizing, Validation = SampleData.Passed } };
        Assert.AreEqual(EventAcceptance.InvalidTransition, gate.Accept(skippedWork));
    }

    [TestMethod]
    public void PreviewDoesNotCommitAnEvent()
    {
        var state = new ExecutionState(SampleData.Route());
        var gate = new WorkerEventGate(state.Snapshot.Route);
        Assert.AreEqual(EventAcceptance.Accepted, gate.Check(state.Events[0]));
        Assert.AreEqual(EventAcceptance.Accepted, gate.Accept(state.Events[0]));
    }
}
