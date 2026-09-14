using System.Collections.Immutable;
using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Core.Execution;

public enum WaitReason { None, SameProjectBusy, BenchmarkExclusive, AnotherRunActive }
public sealed record QueuedRun(RunId RunId, ProjectId ProjectId, ToolKind Tool, WaitReason Reason);
public sealed record CoordinatorSnapshot(RunId? ActiveRun, ImmutableArray<QueuedRun> Waiting);

public interface IExecutionCoordinator
{
    ValueTask<RunLease> AcquireAsync(RunPlan plan, CancellationToken cancellationToken = default);
    CoordinatorSnapshot Snapshot { get; }
}

// Initial P08 policy: one real run globally. A benchmark owns its lease for the entire plan.
public sealed class ExecutionCoordinator : IExecutionCoordinator
{
    private readonly object sync = new();
    private readonly SemaphoreSlim slot = new(1, 1);
    private readonly Dictionary<RunId, RunPlan> waiting = [];
    private RunPlan? active;

    public CoordinatorSnapshot Snapshot
    {
        get
        {
            lock (sync) return new(active?.RunId, waiting.Values.Select(plan => new QueuedRun(
                plan.RunId, plan.Project.Id, plan.Tool, Reason(plan))).ToImmutableArray());
        }
    }

    public async ValueTask<RunLease> AcquireAsync(RunPlan plan, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (active?.RunId == plan.RunId || !waiting.TryAdd(plan.RunId, plan))
                throw new InvalidOperationException("The run is already scheduled.");
        }
        bool acquired = false;
        try
        {
            await slot.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            lock (sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                waiting.Remove(plan.RunId);
                active = plan;
                return new RunLease(plan.RunId, () => Release(plan.RunId));
            }
        }
        catch
        {
            lock (sync) waiting.Remove(plan.RunId);
            if (acquired) slot.Release();
            throw;
        }
    }

    private WaitReason Reason(RunPlan plan)
    {
        if (active is null) return WaitReason.None;
        if (active.Tool == ToolKind.Benchmark || plan.Tool == ToolKind.Benchmark) return WaitReason.BenchmarkExclusive;
        if (active.Project.Id == plan.Project.Id || string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(active.Project.RootPath)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.Project.RootPath)), StringComparison.OrdinalIgnoreCase))
            return WaitReason.SameProjectBusy;
        return WaitReason.AnotherRunActive;
    }

    private void Release(RunId runId)
    {
        lock (sync)
        {
            if (active?.RunId != runId) throw new InvalidOperationException("Execution lease owner mismatch.");
            active = null;
            slot.Release();
        }
    }
}

public sealed class RunLease : IDisposable
{
    private Action? release;
    internal RunLease(RunId runId, Action release) { RunId = runId; this.release = release; }
    public RunId RunId { get; }
    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}
