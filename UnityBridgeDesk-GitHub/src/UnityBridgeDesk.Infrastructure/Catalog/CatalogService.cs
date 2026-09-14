using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Storage;

namespace UnityBridgeDesk.Infrastructure.Catalog;

public sealed record CatalogResult(bool Success, string Message);

public sealed partial class CatalogService : IDisposable
{
    private readonly AtomicJsonStore<CatalogDocument> store;
    private readonly LocalInspector inspector = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string leasePath;
    private FileStream? lease;
    public CatalogDocument Document { get; private set; } = CatalogDocument.Empty;
    public ReadStatus LoadStatus { get; private set; } = ReadStatus.Missing;
    public bool CanWrite { get; private set; }
    public string Notice { get; private set; } = "";
    public event Action? Changed;
    public string DataRoot { get; }

    public CatalogService(string dataRoot)
    {
        if (!Path.IsPathFullyQualified(dataRoot)) throw new ArgumentException("Absolute data root required.");
        DataRoot = dataRoot;
        string folder = Path.Combine(dataRoot, "catalog");
        store = new(Path.Combine(folder, "catalog.json"), x => x.Validate());
        leasePath = Path.Combine(folder, "catalog.session.lock");
    }
    public async Task LoadAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (lease is null)
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!); lease = new(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            var loaded = await store.LoadAsync();
            Document = loaded.Value ?? CatalogDocument.Empty; LoadStatus = loaded.Status;
            CanWrite = lease is not null && loaded.Status is ReadStatus.Current or ReadStatus.Missing or ReadStatus.RecoveredBackup;
            Notice = lease is null ? "카탈로그 읽기 전용 · 같은 저장 위치를 사용하는 다른 Desk 또는 폴더 권한을 확인해 주세요." : loaded.Status switch
            {
                ReadStatus.RecoveredBackup => "이전 정상 카탈로그를 복원했습니다. 목록과 확인 시각을 검토해 주세요.",
                ReadStatus.Corrupt => "카탈로그가 손상되어 읽기 전용으로 열었어요. catalog.json과 .bak 원본을 보존했습니다.",
                ReadStatus.UnsupportedVersion => "더 새로운 카탈로그 형식입니다. 이 버전에서는 변경할 수 없어요.",
                ReadStatus.Unavailable => "카탈로그를 읽지 못했어요. 저장 위치와 권한을 확인해 주세요.",
                _ => "저장된 확인값입니다. 실행 전에는 파일과 연결을 다시 검사해야 합니다."
            };
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }

    public Task<CatalogResult> RegisterProjectAsync(string path, string? label = null) => ChangeAsync(async doc =>
    {
        path = LocalInspector.NormalizePath(path);
        if (doc.Projects.Any(x => SamePath(x.Project.RootPath, path))) return Reject(doc, "이미 등록한 프로젝트 경로입니다.");
        var observation = await inspector.InspectProjectAsync(path);
        var project = new CatalogProject(new(ProjectId.New(), Name(label, path), path, observation.EditorVersion), observation);
        return Accept(doc with { Projects = doc.Projects.Add(project) }, "프로젝트를 등록했어요. " + observation.Detail);
    });

    public Task<CatalogResult> RegisterArtifactAsync(string path, ArtifactKind kind, string? label = null) => ChangeAsync(async doc =>
    {
        if (!Enum.IsDefined(kind)) return Reject(doc, "지원하지 않는 파일 종류입니다.");
        path = LocalInspector.NormalizePath(path);
        var observation = await inspector.InspectArtifactAsync(path, kind);
        if (doc.Artifacts.Any(x => x.Kind == kind &&
            (observation.Sha256 is not null && x.Registered.Sha256 == observation.Sha256 ||
             SamePath(x.Path, path) && x.Registered.Sha256 == observation.Sha256)))
            return Reject(doc, "같은 내용의 파일이 이미 등록되어 있어요. 위치가 바뀌었다면 기존 항목의 경로를 다시 지정해 주세요.");
        var artifact = new CatalogArtifact(Guid.NewGuid(), Name(label, path), kind, path, observation, observation);
        return Accept(doc with { Artifacts = doc.Artifacts.Add(artifact) }, "파일 정보를 등록했어요. " + observation.Detail);
    });

    public Task<CatalogResult> RefreshProjectAsync(ProjectId id) => ChangeAsync(async doc =>
    {
        int index = ProjectIndex(doc, id); var item = doc.Projects[index];
        var observed = await inspector.InspectProjectAsync(item.Project.RootPath);
        return Accept(doc with { Projects = doc.Projects.SetItem(index, item with { Project = item.Project with { UnityBuild = observed.EditorVersion }, Observation = observed }) }, observed.Detail);
    });
    public Task<CatalogResult> RelocateProjectAsync(ProjectId id, string path) => ChangeAsync(async doc =>
    {
        path = LocalInspector.NormalizePath(path);
        if (doc.Projects.Any(x => x.Project.Id != id && SamePath(x.Project.RootPath, path))) return Reject(doc, "다른 등록 프로젝트가 이 경로를 사용하고 있어요.");
        int index = ProjectIndex(doc, id); var item = doc.Projects[index];
        var observed = await inspector.InspectProjectAsync(path);
        if (observed.Status != InspectionStatus.Available) return Reject(doc, "새 경로를 적용하지 않았어요. " + observed.Detail);
        return Accept(doc with { Projects = doc.Projects.SetItem(index, item with { Project = item.Project with { RootPath = path, UnityBuild = observed.EditorVersion }, Observation = observed }) },
            "프로젝트 경로를 변경했어요. 기존 작업에 저장된 경로는 유지됩니다.");
    });
    public Task<CatalogResult> RefreshArtifactAsync(Guid id) => ChangeAsync(async doc =>
    {
        int index = ArtifactIndex(doc, id); var item = doc.Artifacts[index];
        var observed = await inspector.InspectArtifactAsync(item.Path, item.Kind);
        if (observed.Status == InspectionStatus.Available && observed.Sha256 != item.Registered.Sha256)
            observed = observed with { Status = InspectionStatus.Changed, Detail = "등록 당시와 내용이 달라요. 새 버전은 새 항목으로 등록하고 릴리스를 다시 구성해 주세요." };
        return Accept(doc with { Artifacts = doc.Artifacts.SetItem(index, item with { Current = observed }) }, observed.Detail);
    });
    public Task<CatalogResult> RelocateArtifactAsync(Guid id, string path) => ChangeAsync(async doc =>
    {
        path = LocalInspector.NormalizePath(path);
        int index = ArtifactIndex(doc, id); var item = doc.Artifacts[index];
        var observed = await inspector.InspectArtifactAsync(path, item.Kind);
        if (observed.Sha256 is null || observed.Sha256 != item.Registered.Sha256 || observed.Status != InspectionStatus.Available)
            return Reject(doc, "새 경로의 내용이 등록 때와 일치하지 않아 경로를 바꾸지 않았어요. 새 파일은 별도 등록해 주세요.");
        return Accept(doc with { Artifacts = doc.Artifacts.SetItem(index, item with { Path = path, Current = observed }) }, "동일한 해시를 확인하고 경로를 바꿨어요. 릴리스 식별자는 유지됩니다.");
    });
    public Task<CatalogResult> AddReleaseAsync(string label, ComparisonAxis axis, Guid cliId, Guid? connectorId) => ChangeAsync(doc =>
    {
        if (string.IsNullOrWhiteSpace(label)) return Task.FromResult(Reject(doc, "조합의 표시 이름을 입력해 주세요."));
        if (doc.Releases.Any(x => x.Axis == axis && x.CliId == cliId && x.ConnectorId == connectorId))
            return Task.FromResult(Reject(doc, "같은 파일과 비교 축의 조합이 이미 등록되어 있어요."));
        var cli = doc.Artifacts.SingleOrDefault(x => x.Id == cliId && x.Kind == ArtifactKind.CliExecutable);
        var connector = doc.Artifacts.SingleOrDefault(x => x.Id == connectorId && x.Kind == ArtifactKind.ConnectorFolder);
        if (cli?.MatchesRegistration != true || connectorId is not null && connector?.MatchesRegistration != true)
            return Task.FromResult(Reject(doc, "확인 가능한 CLI와 Connector 항목을 선택해 주세요."));
        if (axis == ComparisonAxis.CliAndConnector && connector is null)
            return Task.FromResult(Reject(doc, "CLI + Connector 비교에는 Connector가 필요해요."));
        return Task.FromResult(Accept(doc with { Releases = doc.Releases.Add(new(ReleaseId.New(), label.Trim(), axis, cliId, connectorId)) }, "릴리스 조합을 등록했어요. 프로젝트에 적용된 버전은 아닙니다."));
    });
    public Task<CatalogResult> SelectProjectAsync(ProjectId? id) => ChangeAsync(doc => Task.FromResult(Accept(doc with { SelectedProject = id }, "새 작업의 공통 프로젝트를 선택했어요.")));
    public Task<CatalogResult> SelectReleaseAsync(ReleaseId? id) => ChangeAsync(doc => Task.FromResult(Accept(doc with { SelectedRelease = id }, "새 작업의 참고 릴리스를 선택했어요. 설치된 버전 표시는 별도입니다.")));
    public Task<CatalogResult> RemoveProjectAsync(ProjectId id) => ChangeAsync(doc => Task.FromResult(Accept(doc with
    { Projects = doc.Projects.RemoveAt(ProjectIndex(doc, id)), SelectedProject = doc.SelectedProject == id ? null : doc.SelectedProject }, "목록에서 제거했어요. 프로젝트 폴더는 그대로입니다.")));
    public Task<CatalogResult> RemoveArtifactAsync(Guid id) => ChangeAsync(doc => Task.FromResult(doc.Releases.Any(x => x.CliId == id || x.ConnectorId == id)
        ? Reject(doc, "이 파일을 참조하는 릴리스 조합을 먼저 목록에서 제거해 주세요.")
        : Accept(doc with { Artifacts = doc.Artifacts.RemoveAt(ArtifactIndex(doc, id)) }, "목록에서 제거했어요. 실제 파일은 그대로입니다.")));
    public Task<CatalogResult> RemoveReleaseAsync(ReleaseId id) => ChangeAsync(doc => Task.FromResult(Accept(doc with
    { Releases = doc.Releases.Remove(doc.Releases.Single(x => x.Id == id)), SelectedRelease = doc.SelectedRelease == id ? null : doc.SelectedRelease }, "릴리스 조합을 목록에서 제거했어요. 파일과 과거 작업 기록은 유지됩니다.")));

    private async Task<CatalogResult> ChangeAsync(Func<CatalogDocument, Task<(CatalogDocument Document, CatalogResult Result)>> mutation)
    {
        await gate.WaitAsync();
        try
        {
            if (!CanWrite) return new(false, Notice);
            var next = await mutation(Document);
            if (!next.Result.Success) return next.Result;
            next.Document.Validate();
            var saved = await store.SaveAsync(next.Document);
            if (saved.Status != SaveStatus.Saved) return new(false, "저장하지 못해 변경을 적용하지 않았어요. 저장 위치와 권한을 확인해 주세요.");
            Document = next.Document; Changed?.Invoke(); return next.Result;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        { return new(false, "입력과 선택한 항목을 확인해 주세요. 변경은 저장하지 않았어요."); }
        finally { gate.Release(); }
    }
    private static int ProjectIndex(CatalogDocument doc, ProjectId id) => Enumerable.Range(0, doc.Projects.Length).First(i => doc.Projects[i].Project.Id == id);
    private static int ArtifactIndex(CatalogDocument doc, Guid id) => Enumerable.Range(0, doc.Artifacts.Length).First(i => doc.Artifacts[i].Id == id);
    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Name(string? label, string path) => string.IsNullOrWhiteSpace(label) ? Path.GetFileName(path) is { Length: > 0 } name ? name : path : label.Trim();
    private static (CatalogDocument, CatalogResult) Accept(CatalogDocument document, string message) => (document, new(true, message));
    private static (CatalogDocument, CatalogResult) Reject(CatalogDocument document, string message) => (document, new(false, message));
    public void Dispose() { lease?.Dispose(); lease = null; CanWrite = false; }
}
