using UnityBridgeDesk.Infrastructure.Execution;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record OfficialUnitySelection(string? CliVersion = null, string? PipelineVersion = null)
{
    public void Validate() => OfficialUnityRepository.ValidateVersions(CliVersion, PipelineVersion);
}

// Both desktop buttons and the headless entry point use this preparation/execution boundary.
public interface ISpeedBenchWorkflow
{
    Task<SpeedRelease[]> Prepare(string editor, ReleaseChoice[] choices, OfficialUnitySelection? official, string? baseline,
        IProgress<string>? progress, CancellationToken ct, GoUnitySelection? go = null);
    Task<SpeedRun> Run(string editor, SpeedRelease[] releases, SpeedOptions options, IProgress<string>? progress,
        IProgress<SpeedLiveProgress>? live, CancellationToken ct);
    Task Cleanup();
}

public sealed class SpeedBenchWorkflow(string dataRoot, string workerDirectory) : ISpeedBenchWorkflow
{
    public const string OfficialLabel = "공식 Unity CLI + Pipeline";
    public const string GoLabel = "Go unity-cli (비공식)";
    private OfficialToolWorkspace? officialWorkspace;
    private GoToolWorkspace? goWorkspace;
    private bool officialUsed;
    public static void ValidateSelection(int bridgeCount, bool official, bool go = false)
    {
        if (go)
        {
            if (bridgeCount < 0 || bridgeCount + (official ? 1 : 0) + 1 is < 2 or > 8)
                throw new ArgumentException("UnityBridge·공식 Unity·Go CLI에서 비교 대상 2~8개를 선택하세요.");
            return;
        }
        if (bridgeCount < (official ? 1 : 2) || bridgeCount > (official ? 7 : 8))
            throw new ArgumentException(official ? "공식 Unity와 비교할 UnityBridge 버전을 1~7개 선택하세요." : "비교할 UnityBridge 버전을 2~8개 선택하세요.");
    }
    public async Task<SpeedRelease[]> Prepare(string editor, ReleaseChoice[] choices, OfficialUnitySelection? official, string? baseline,
        IProgress<string>? progress, CancellationToken ct, GoUnitySelection? go = null)
    {
        try
        {
        if (officialUsed) await Cleanup();
        ValidateSelection(choices.Length, official is not null, go is not null); official?.Validate(); go?.Validate();
        var environment = await LocalSpeedCoordinator.InspectEditor(editor, ct);
        if ((official is not null || go is not null) && !environment.EditorVersion.StartsWith("6000.", StringComparison.Ordinal))
            throw new ArgumentException("공식 Unity·Go CLI 비교에는 설치·활성화된 Unity 6가 필요합니다.");
        var releases = new List<SpeedRelease>(); var repository = new ReleaseRepository(dataRoot);
        foreach (var choice in choices) releases.Add(await repository.Prepare(choice, progress, ct));
        if (official is not null)
        {
            officialWorkspace ??= await OfficialToolWorkspace.Create(dataRoot, progress, ct);
            releases.Add(await officialWorkspace.Prepare(official, editor, progress, ct));
        }
        else if (officialWorkspace is not null) { await officialWorkspace.DisposeAsync(); officialWorkspace = null; }
        if (go is not null)
        {
            goWorkspace ??= await GoToolWorkspace.Create(dataRoot, progress, ct);
            releases.Add(await goWorkspace.Prepare(go, progress, ct, editor: editor));
        }
        else if (goWorkspace is not null) { await goWorkspace.DisposeAsync(); goWorkspace = null; }
        return releases.OrderBy(r => r.Tag == baseline || r.OfficialUnity is not null && baseline == OfficialLabel || r.GoUnity is not null && baseline == GoLabel ? 0 : 1).ToArray();
        }
        catch { await Cleanup(); throw; }
    }
    public async Task<SpeedRun> Run(string editor, SpeedRelease[] releases, SpeedOptions options, IProgress<string>? progress,
        IProgress<SpeedLiveProgress>? live, CancellationToken ct)
    {
        SpeedRun? run = null; string? root = officialWorkspace?.Root, goRoot = goWorkspace?.Root;
        officialUsed = root is not null || goRoot is not null;
        try
        {
            if (releases.Any(r => r.OfficialUnity is not null && officialWorkspace?.Contains(r) != true))
                throw new IOException("공식 도구를 이번 벤치의 임시 폴더에 다시 준비해 주세요.");
            if (releases.Any(r => r.GoUnity is not null && goWorkspace?.Contains(r) != true))
                throw new IOException("Go 도구를 이번 벤치의 임시 폴더에 다시 준비해 주세요.");
            run = await new LocalSpeedCoordinator(dataRoot, new WorkerRunner(Path.Combine(workerDirectory, "UnityBridgeDesk.Worker.exe")))
                .Run(editor, releases, options, workerDirectory, progress, ct, live,
                    root is null ? null : new("pending", root), goRoot is null ? null : new("pending", goRoot));
        }
        finally
        {
            string? error = null;
            try
            {
                if (root is not null) progress?.Report("공식 CLI·Pipeline 다운로드 원본과 임시 보관함 삭제 중…");
                if (goRoot is not null) progress?.Report("Go CLI·Connector 다운로드 원본과 임시 보관함 삭제 중…");
                await Cleanup();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { error = ex.Message; if (run is null) throw; }
            if ((root is not null || goRoot is not null) && run is not null)
            {
                run = run with { OfficialCleanup = root is null ? null : new(!Directory.Exists(root) ? "deleted" : "failed", root, Directory.Exists(root) ? error : null),
                    GoCleanup = goRoot is null ? null : new(!Directory.Exists(goRoot) ? "deleted" : "failed", goRoot, Directory.Exists(goRoot) ? error : null),
                    Status = error is null ? run.Status : "cleanup-failed" };
                await SpeedFiles.Write(Path.Combine(Path.GetFullPath(dataRoot), "speed", "local-runs", run.Id.ToString("N"), "run.json"), run);
                progress?.Report(error is null ? "비교 도구 임시 파일 삭제 확인 · 결과는 보관했습니다." : "비교 도구 정리 실패: " + error);
            }
        }
        return run!;
    }
    public async Task Cleanup()
    {
        Exception? failure = null;
        try { if (officialWorkspace is not null) { await officialWorkspace.DisposeAsync(); officialWorkspace = null; } }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { failure = e; }
        try { if (goWorkspace is not null) { await goWorkspace.DisposeAsync(); goWorkspace = null; } }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { failure = failure is null ? e : new IOException(failure.Message + " / " + e.Message); }
        if (failure is not null) throw failure;
        officialUsed = false;
    }
}
