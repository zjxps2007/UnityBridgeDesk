using System.Collections.Immutable;
using System.Text.Json;
using UnityBridgeDesk.Core.Contracts;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Infrastructure.Storage;

public sealed record ExecutionRecord(ExecutionSpec Spec, ImmutableArray<ExecutionEvent> Events)
{
    // Observation only. A later supervisor must check process ownership before recording Interrupted.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeedsInterruptionReview => !Events.IsDefaultOrEmpty && Events[^1].Snapshot.Outcome is null;

    public void Validate(RunPlan plan)
    {
        Spec.ValidateAgainst(plan);
        if (Events.IsDefaultOrEmpty) throw new ArgumentException("Execution record must have its creation event.");
        var gate = new WorkerEventGate(Spec.Route);
        foreach (var message in Events)
            if (gate.Accept(message) != EventAcceptance.Accepted) throw new ArgumentException("Invalid execution history.");
    }
}

public sealed class RunRecordStore
{
    private readonly string root;
    private readonly IAtomicFileOperations? files;

    public RunRecordStore(string root, IAtomicFileOperations? files = null)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Run store path must be absolute.");
        this.root = Path.GetFullPath(root);
        this.files = files;
    }

    public Task<SaveResult> CreatePlanAsync(RunPlan plan, CancellationToken cancellationToken = default) =>
        PlanStore(plan.RunId).SaveAsync(plan, createOnly: true, cancellationToken);

    public Task<ReadResult<RunPlan>> ReadPlanAsync(RunId runId, CancellationToken cancellationToken = default) =>
        PlanStore(runId).LoadAsync(cancellationToken);

    public Task<ReadResult<ExecutionRecord>> ReadExecutionAsync(RunPlan plan, ExecutionId executionId,
        CancellationToken cancellationToken = default) => RecordStore(plan, executionId).LoadAsync(cancellationToken);

    public async Task<ExecutionRecordWriter> CreateExecutionAsync(RunPlan plan, ExecutionSpec spec,
        ExecutionEvent created, CancellationToken cancellationToken = default)
    {
        spec.ValidateAgainst(plan);
        var persisted = await ReadPlanAsync(plan.RunId, cancellationToken).ConfigureAwait(false);
        if (persisted.Value is null || JsonSerializer.Serialize(persisted.Value, DeskJson.Options) != JsonSerializer.Serialize(plan, DeskJson.Options))
            throw new InvalidOperationException("The immutable plan must be persisted before creating an execution.");
        var record = new ExecutionRecord(spec, [created]);
        record.Validate(plan);
        var store = RecordStore(plan, spec.Route.ExecutionId);
        Directory.CreateDirectory(Path.GetDirectoryName(store.DocumentPath)!);
        var writerLock = new FileStream(store.DocumentPath + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var result = await store.SaveAsync(record, createOnly: true, cancellationToken).ConfigureAwait(false);
            if (result.Status != SaveStatus.Saved) throw new IOException("Execution record could not be created; recovery may be required.");
            return new(store, record, writerLock);
        }
        catch { writerLock.Dispose(); throw; }
    }

    private AtomicJsonStore<RunPlan> PlanStore(RunId runId)
    {
        ContractGuard.Id(runId.Value);
        return new(Path.Combine(root, runId.Value.ToString("N"), "plan.json"), plan =>
        {
            if (plan.RunId != runId) throw new ArgumentException("Plan identity does not match its directory.");
        }, files);
    }

    private AtomicJsonStore<ExecutionRecord> RecordStore(RunPlan plan, ExecutionId executionId)
    {
        ContractGuard.Id(executionId.Value);
        return new(Path.Combine(root, plan.RunId.Value.ToString("N"), "executions", executionId.Value.ToString("N"), "record.json"), record =>
        {
            record.Validate(plan);
            if (record.Spec.Route.ExecutionId != executionId) throw new ArgumentException("Execution identity does not match its directory.");
        }, files);
    }
}

// Version 1 is a bounded atomic event document, not the high-volume JSONL transport planned for slice 04/11.
public sealed class ExecutionRecordWriter : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AtomicJsonStore<ExecutionRecord> store;
    private readonly WorkerEventGate eventGate;
    private readonly FileStream writerLock;
    private ExecutionRecord record;
    private bool disposed;

    internal ExecutionRecordWriter(AtomicJsonStore<ExecutionRecord> store, ExecutionRecord record, FileStream writerLock)
    {
        this.store = store; this.record = record; this.writerLock = writerLock;
        eventGate = new(record.Spec.Route);
        foreach (var message in record.Events) eventGate.Accept(message);
    }

    public async Task<SaveResult> AppendAsync(ExecutionEvent message, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (eventGate.Check(message) != EventAcceptance.Accepted) return new(SaveStatus.InvalidData);
            var next = record with { Events = record.Events.Add(message) };
            var result = await store.SaveAsync(next, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Status == SaveStatus.Saved)
            {
                eventGate.Accept(message);
                record = next;
            }
            return result;
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await writerLock.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
