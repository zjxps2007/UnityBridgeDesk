using System.Collections.Immutable;
using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Core.Execution;

public enum ResourceOwnership { Unknown, JobOwned, External }
public enum PathPurpose { Unknown, Temporary, Output, Artifact }
public sealed record ProcessRegistration(ExecutionRoute Route, int ProcessId, DateTimeOffset StartedAtUtc, ResourceOwnership Ownership);
public sealed record PathRegistration(ExecutionRoute Route, string AbsolutePath, PathPurpose Purpose, ResourceOwnership Ownership);
public sealed record CleanupCandidates(ImmutableArray<ProcessRegistration> Processes, ImmutableArray<PathRegistration> TemporaryPaths);

// An ownership ledger is evidence, not permission to kill/delete. Adapters must recheck OS identity and reparse points.
public sealed class ExecutionResources
{
    private readonly object sync = new();
    private readonly ExecutionRoute route;
    private readonly string managedRoot;
    private readonly Dictionary<int, ProcessRegistration> processes = [];
    private readonly Dictionary<string, PathRegistration> paths = new(StringComparer.OrdinalIgnoreCase);

    public ExecutionResources(ExecutionRoute route, string managedRoot)
    {
        route.Validate();
        this.route = route;
        this.managedRoot = Normalize(managedRoot);
    }

    public void RegisterProcess(ProcessRegistration process)
    {
        RequireRoute(process.Route);
        ContractGuard.Known(process.Ownership);
        if (process.ProcessId <= 0 || process.StartedAtUtc == default) throw new ArgumentException("Incomplete process identity.");
        lock (sync)
        {
            if (!processes.TryAdd(process.ProcessId, process)) throw new InvalidOperationException("Process already registered.");
        }
    }

    public void RegisterPath(PathRegistration path)
    {
        RequireRoute(path.Route);
        ContractGuard.Known(path.Ownership); ContractGuard.Known(path.Purpose);
        var normalized = Normalize(path.AbsolutePath);
        if (path.Ownership == ResourceOwnership.JobOwned && !normalized.StartsWith(
            managedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Owned paths must be children of this execution's managed root.");
        lock (sync)
        {
            if (!paths.TryAdd(normalized, path with { AbsolutePath = normalized }))
                throw new InvalidOperationException("Path already registered.");
        }
    }

    public CleanupCandidates GetCleanupCandidates()
    {
        lock (sync) return new(
            processes.Values.Where(x => x.Ownership == ResourceOwnership.JobOwned).ToImmutableArray(),
            paths.Values.Where(x => x.Ownership == ResourceOwnership.JobOwned && x.Purpose == PathPurpose.Temporary).ToImmutableArray());
    }

    private void RequireRoute(ExecutionRoute value)
    {
        if (value != route) throw new ArgumentException("Resource belongs to another execution.");
    }
    private static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Resource path must be absolute.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
