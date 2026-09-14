using System.Collections.Immutable;
using UnityBridgeDesk.Infrastructure.Storage;

namespace UnityBridgeDesk.Infrastructure.Catalog;

public sealed record LocalSetup(ImmutableArray<string> Folders, ImmutableDictionary<string, string> Paths)
{
    public static LocalSetup Empty => new([], ImmutableDictionary<string, string>.Empty);
    public void Validate()
    {
        if (Folders.IsDefault || Folders.Length > 20 || Paths is null || Paths.Count > 100 ||
            Folders.Any(x => !Path.IsPathFullyQualified(x)) || Paths.Any(x => !Path.IsPathFullyQualified(x.Value)))
            throw new ArgumentException("Invalid local setup.");
    }
}

// Stores locations only. Models, permissions, experiments and credentials remain outside this file.
public sealed class LocalSetupStore(string dataRoot)
{
    private readonly AtomicJsonStore<LocalSetup> store = new(Path.Combine(dataRoot, "settings", "local-setup.json"), x => x.Validate());
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public async Task<LocalSetup> LoadAsync() => (await store.LoadAsync()).Value ?? LocalSetup.Empty;
    public async Task<bool> RememberAsync(string? folder = null, string? key = null, string? path = null)
    {
        await Gate.WaitAsync();
        try
        {
            var read = await store.LoadAsync();
            if (read.Status is not (ReadStatus.Missing or ReadStatus.Current or ReadStatus.RecoveredBackup)) return false;
            var value = read.Value ?? LocalSetup.Empty;
            if (folder is not null)
            {
                folder = LocalInspector.NormalizePath(folder);
                value = value with { Folders = [folder, ..value.Folders.Where(x => !string.Equals(x, folder, StringComparison.OrdinalIgnoreCase)).Take(19)] };
            }
            if (key is not null && path is not null)
                value = value with { Paths = value.Paths.SetItem(key, LocalInspector.NormalizePath(path)) };
            return (await store.SaveAsync(value)).Status == SaveStatus.Saved;
        }
        finally { Gate.Release(); }
    }
}
