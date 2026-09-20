namespace UnityBridgeDesk.Infrastructure.SpeedBench;

// Downloaded originals are never executed. Trial copies use LocalWorkspace and their own process tree.
public sealed class OfficialToolWorkspace : IAsyncDisposable
{
    private readonly string dataRoot;
    private readonly Guid id;
    private readonly string token;
    public string Root { get; }
    private OfficialToolWorkspace(string dataRoot, Guid id, string token, string root)
    { this.dataRoot = dataRoot; this.id = id; this.token = token; Root = root; }

    public static async Task<OfficialToolWorkspace> Create(string dataRoot, IProgress<string>? progress, CancellationToken ct)
    {
        await LocalWorkspace.Recover(dataRoot, progress, ct, "official-work");
        Guid id = Guid.NewGuid(); string token = Guid.NewGuid().ToString("N");
        string root = await LocalWorkspace.Create(dataRoot, id, token, ct, "official-work");
        return new(dataRoot, id, token, root);
    }
    public async Task<SpeedRelease> Prepare(OfficialUnitySelection selection, string? editor, IProgress<string>? progress,
        CancellationToken ct, HttpClient? http = null)
    {
        await LocalWorkspace.Check(Root, id, token, "official-work");
        try
        {
            return await new OfficialUnityRepository(Path.Combine(Root, "artifacts"), http)
                .Prepare(selection.CliVersion, selection.PipelineVersion, progress, ct, editor);
        }
        catch { await DisposeAsync(); throw; }
    }
    public bool Contains(SpeedRelease release) => release.OfficialUnity is not null &&
        new[] { release.CliPath, release.ConnectorPath }.Concat(release.OfficialUnity.Packages.Select(p => p.Folder))
            .All(p => Path.GetFullPath(p).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    public async ValueTask DisposeAsync() => await LocalWorkspace.Delete(dataRoot, Root, id, token, "official-work");
}
