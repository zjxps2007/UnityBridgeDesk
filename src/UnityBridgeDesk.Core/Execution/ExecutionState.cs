using System.Collections.Immutable;
using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Core.Execution;

public enum ExecutionPhase { Unknown, Queued, Preparing, Running, Validating, Finalizing, Terminal }
public enum RunOutcome { Unknown, Succeeded, Failed, Cancelled, TimedOut, Interrupted }
public enum StopReason { None, UserCancellation, Timeout }
public enum ValidationStatus { Unknown, Passed, Failed, Unsupported }
public enum CleanupStatus { Unknown, Completed, RecoveryRequired }
public enum FailureCode
{
    None, ExecutionFailed, UnsupportedProvider, ValidationNotPassed,
    CleanupIncomplete, HostInterrupted, ProtocolRejected
}
public enum ExecutionEventKind { Unknown, Created, PhaseChanged, RetryStarted, StopRequested, Completed }

public sealed record ValidationResult(ValidationStatus Status, string? EvidenceSha256)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Status)) throw new ArgumentException("Unknown validation status.");
        BridgeReleaseRef.ValidateHash(EvidenceSha256);
    }
    public static ValidationResult Unverified => new(ValidationStatus.Unknown, null);
}

public sealed record CleanupReport(CleanupStatus Status)
{
    public static CleanupReport Complete => new(CleanupStatus.Completed);
}

public sealed record ExecutionSnapshot(ExecutionRoute Route, ExecutionPhase Phase, int Attempt,
    StopReason StopReason, RunOutcome? Outcome, FailureCode Failure,
    ValidationResult Validation, CleanupReport Cleanup)
{
    public void Validate()
    {
        Route.Validate();
        ContractGuard.Known(Phase);
        Validation.Validate();
        if (Attempt < 1 || !Enum.IsDefined(StopReason) || !Enum.IsDefined(Failure) || !Enum.IsDefined(Cleanup.Status))
            throw new ArgumentException("Invalid execution snapshot.");
        if (Outcome is { } outcome) ContractGuard.Known(outcome);
        if ((Phase == ExecutionPhase.Terminal) != (Outcome is not null))
            throw new ArgumentException("Only a terminal execution has an outcome.");
        if (Outcome == RunOutcome.Succeeded && (Validation.Status != ValidationStatus.Passed ||
            Cleanup.Status != CleanupStatus.Completed || StopReason != StopReason.None || Failure != FailureCode.None))
            throw new ArgumentException("Success requires validation and cleanup with no stop or failure.");
        if (Phase == ExecutionPhase.Terminal &&
            (StopReason == StopReason.UserCancellation && Outcome != RunOutcome.Cancelled ||
             StopReason == StopReason.Timeout && Outcome != RunOutcome.TimedOut))
            throw new ArgumentException("A terminal result must preserve an accepted stop request.");
        if (Outcome == RunOutcome.Cancelled && StopReason != StopReason.UserCancellation ||
            Outcome == RunOutcome.TimedOut && StopReason != StopReason.Timeout)
            throw new ArgumentException("Cancellation and timeout require a stop request.");
    }
}

// Structured events contain no raw command output, prompt, exception or credential text.
public sealed record ExecutionEvent(int SchemaVersion, long Sequence, DateTimeOffset OccurredAtUtc,
    ExecutionEventKind Kind, ExecutionSnapshot Snapshot)
{
    public const int CurrentSchemaVersion = 1;
    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || Sequence < 1 || OccurredAtUtc == default)
            throw new ArgumentException("Unsupported event envelope.");
        ContractGuard.Known(Kind);
        Snapshot.Validate();
        if ((Kind == ExecutionEventKind.Completed) != (Snapshot.Phase == ExecutionPhase.Terminal))
            throw new ArgumentException("Completion event and snapshot disagree.");
    }
}

// Lifetime belongs to the supervisor/worker, never a window or view model.
public sealed class ExecutionState
{
    private readonly object sync = new();
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource stop = new();
    private readonly List<ExecutionEvent> journal = [];
    private readonly TaskCompletionSource<ExecutionSnapshot> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ExecutionSnapshot snapshot;
    private RunOutcome? intendedOutcome;

    public ExecutionState(ExecutionRoute route, TimeProvider? clock = null)
    {
        route.Validate();
        this.clock = clock ?? TimeProvider.System;
        snapshot = new(route, ExecutionPhase.Queued, 1, StopReason.None, null,
            FailureCode.None, ValidationResult.Unverified, new(CleanupStatus.Unknown));
        Append(ExecutionEventKind.Created);
    }

    public CancellationToken StopToken => stop.Token;
    public Task<ExecutionSnapshot> Completion => completion.Task;
    public ExecutionSnapshot Snapshot { get { lock (sync) return snapshot; } }
    public ImmutableArray<ExecutionEvent> Events { get { lock (sync) return [.. journal]; } }

    public bool TryAdvance(ExecutionPhase next)
    {
        lock (sync)
        {
            if (snapshot.StopReason != StopReason.None) return false;
            bool allowed = (snapshot.Phase, next) is
                (ExecutionPhase.Queued, ExecutionPhase.Preparing) or
                (ExecutionPhase.Preparing, ExecutionPhase.Running) or
                (ExecutionPhase.Running, ExecutionPhase.Validating);
            if (!allowed) return false;
            snapshot = snapshot with { Phase = next };
            Append(ExecutionEventKind.PhaseChanged);
            return true;
        }
    }

    public bool TryBeginRetry(int nextAttempt)
    {
        lock (sync)
        {
            if (snapshot.Phase != ExecutionPhase.Validating || snapshot.StopReason != StopReason.None ||
                snapshot.Route.Mode is not (ExecutionMode.AiWork or ExecutionMode.AiCreation) ||
                nextAttempt != snapshot.Attempt + 1) return false;
            snapshot = snapshot with { Phase = ExecutionPhase.Running, Attempt = nextAttempt };
            Append(ExecutionEventKind.RetryStarted);
            return true;
        }
    }

    // Returns whether this request won. Callback faults propagate to the supervisor; the stop remains accepted.
    // Callbacks must only signal work; cleanup belongs in the runner's finalization path.
    public async ValueTask<bool> RequestStopAsync(StopReason reason)
    {
        ContractGuard.Known(reason);
        lock (sync)
        {
            if (snapshot.Phase == ExecutionPhase.Terminal || snapshot.StopReason != StopReason.None) return false;
            snapshot = snapshot with { StopReason = reason };
            Append(ExecutionEventKind.StopRequested);
        }
        await stop.CancelAsync().ConfigureAwait(false);
        return true;
    }

    public bool TryBeginFinalizing(RunOutcome intended, ValidationResult validation, FailureCode failure = FailureCode.None)
    {
        ContractGuard.Known(intended);
        validation.Validate();
        if (!Enum.IsDefined(failure)) throw new ArgumentException("Unknown failure code.");
        lock (sync)
        {
            if (snapshot.Phase is ExecutionPhase.Finalizing or ExecutionPhase.Terminal) return false;
            if (intended == RunOutcome.Succeeded && snapshot.Phase != ExecutionPhase.Validating) return false;
            if (intended == RunOutcome.Cancelled && snapshot.StopReason != StopReason.UserCancellation ||
                intended == RunOutcome.TimedOut && snapshot.StopReason != StopReason.Timeout) return false;
            if (intended == RunOutcome.Succeeded && (validation.Status != ValidationStatus.Passed || failure != FailureCode.None))
            {
                intended = RunOutcome.Failed;
                if (failure == FailureCode.None) failure = FailureCode.ValidationNotPassed;
            }
            if (intended == RunOutcome.Failed && failure == FailureCode.None) failure = FailureCode.ExecutionFailed;
            if (intended == RunOutcome.Interrupted && failure == FailureCode.None) failure = FailureCode.HostInterrupted;
            intendedOutcome = intended;
            snapshot = snapshot with { Phase = ExecutionPhase.Finalizing, Validation = validation, Failure = failure };
            Append(ExecutionEventKind.PhaseChanged);
            return true;
        }
    }

    // A stop accepted before this lock wins over success, even if work already finished.
    public bool TryComplete(CleanupReport cleanup)
    {
        if (!Enum.IsDefined(cleanup.Status)) throw new ArgumentException("Unknown cleanup status.");
        lock (sync)
        {
            if (snapshot.Phase != ExecutionPhase.Finalizing) return false;
            var outcome = snapshot.StopReason switch
            {
                StopReason.UserCancellation => RunOutcome.Cancelled,
                StopReason.Timeout => RunOutcome.TimedOut,
                _ => intendedOutcome!.Value
            };
            var failure = snapshot.Failure;
            if (outcome == RunOutcome.Succeeded && cleanup.Status != CleanupStatus.Completed)
            {
                outcome = RunOutcome.Failed;
                failure = FailureCode.CleanupIncomplete;
            }
            snapshot = snapshot with { Phase = ExecutionPhase.Terminal, Outcome = outcome, Cleanup = cleanup, Failure = failure };
            snapshot.Validate();
            Append(ExecutionEventKind.Completed);
            completion.TrySetResult(snapshot);
            return true;
        }
    }

    private void Append(ExecutionEventKind kind) => journal.Add(new(ExecutionEvent.CurrentSchemaVersion,
        journal.Count + 1L, clock.GetUtcNow(), kind, snapshot));
}
