using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Core.Contracts;

public enum EventAcceptance { Accepted, InvalidEnvelope, WrongRoute, OutOfSequence, InvalidTransition, StreamClosed }

// One gate per execution/pipe connection. Reject gaps; transport must retry or fail explicitly.
public sealed class WorkerEventGate
{
    private readonly object sync = new();
    private readonly ExecutionRoute route;
    private ExecutionEvent? previous;

    public WorkerEventGate(ExecutionRoute route) { route.Validate(); this.route = route; }

    public EventAcceptance Check(ExecutionEvent message)
    {
        lock (sync) return new WorkerEventGate(route) { previous = previous }.Accept(message);
    }

    public EventAcceptance Accept(ExecutionEvent message)
    {
        lock (sync)
        {
            try { message.Validate(); }
            catch (Exception error) when (error is ArgumentException or NullReferenceException)
            { return EventAcceptance.InvalidEnvelope; }
            if (message.Snapshot.Route != route) return EventAcceptance.WrongRoute;
            if (previous?.Kind == ExecutionEventKind.Completed) return EventAcceptance.StreamClosed;
            if (message.Sequence != (previous?.Sequence ?? 0) + 1) return EventAcceptance.OutOfSequence;
            if (!Follows(previous, message)) return EventAcceptance.InvalidTransition;
            previous = message;
            return EventAcceptance.Accepted;
        }
    }

    private static bool Follows(ExecutionEvent? prior, ExecutionEvent next)
    {
        var b = next.Snapshot;
        if (prior is null) return next.Kind == ExecutionEventKind.Created && b.Phase == ExecutionPhase.Queued &&
            b.Attempt == 1 && b.StopReason == StopReason.None && b.Failure == FailureCode.None &&
            b.Validation.Status == ValidationStatus.Unknown && b.Cleanup.Status == CleanupStatus.Unknown;
        var a = prior.Snapshot;
        if (next.Kind == ExecutionEventKind.StopRequested)
            return a.StopReason == StopReason.None && b.StopReason != StopReason.None && b == (a with { StopReason = b.StopReason });
        if (a.StopReason != b.StopReason) return false;
        if (next.Kind == ExecutionEventKind.RetryStarted)
            return a.StopReason == StopReason.None && a.Route.Mode is ExecutionMode.AiWork or ExecutionMode.AiCreation &&
                a.Phase == ExecutionPhase.Validating && b == (a with { Phase = ExecutionPhase.Running, Attempt = a.Attempt + 1 });
        if (b.Attempt != a.Attempt) return false;
        if (next.Kind == ExecutionEventKind.Completed)
            return a.Phase == ExecutionPhase.Finalizing && a.Validation == b.Validation &&
                (a.Failure == b.Failure || a.Failure == FailureCode.None && b.Failure == FailureCode.CleanupIncomplete);
        if (next.Kind != ExecutionEventKind.PhaseChanged) return false;
        if (b.Phase == ExecutionPhase.Finalizing)
            return a.Phase is not (ExecutionPhase.Finalizing or ExecutionPhase.Terminal) && a.Cleanup == b.Cleanup &&
                (b.StopReason != StopReason.None || b.Failure != FailureCode.None ||
                    a.Phase == ExecutionPhase.Validating && b.Validation.Status == ValidationStatus.Passed);
        return a.StopReason == StopReason.None && b == (a with { Phase = b.Phase }) &&
            (a.Phase, b.Phase) is (ExecutionPhase.Queued, ExecutionPhase.Preparing) or
                (ExecutionPhase.Preparing, ExecutionPhase.Running) or (ExecutionPhase.Running, ExecutionPhase.Validating);
    }
}

// Durations are computed inside the measuring process, never by subtracting different process clocks.
public sealed record ExecutionMeasurements(double? ElapsedMilliseconds, long? PeakWorkingSetBytes,
    long? InputTokens, long? OutputTokens)
{
    public void Validate()
    {
        if (ElapsedMilliseconds is { } duration && (!double.IsFinite(duration) || duration < 0) ||
            PeakWorkingSetBytes < 0 || InputTokens < 0 || OutputTokens < 0)
            throw new ArgumentException("Measurements must be nonnegative or unknown (null).");
    }
}
