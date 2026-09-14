using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Infrastructure.Catalog;

public sealed partial class CatalogService
{
    public Task<CatalogResult> UseDiscoveredProjectAsync(string path) => ChangeAsync(async doc =>
    {
        path = LocalInspector.NormalizePath(path);
        var observed = await inspector.InspectProjectAsync(path);
        if (observed.Status != InspectionStatus.Available) return Reject(doc, "사용할 수 없는 프로젝트입니다. " + observed.Detail);
        var previous = doc.Projects.FirstOrDefault(x => SamePath(x.Project.RootPath, path));
        var item = previous is null ? new CatalogProject(new(ProjectId.New(), Name(null, path), path, observed.EditorVersion), observed) :
            previous with { Project = previous.Project with { UnityBuild = observed.EditorVersion }, Observation = observed };
        var projects = previous is null ? doc.Projects.Add(item) : doc.Projects.Replace(previous, item);
        return Accept(doc with { Projects = projects, SelectedProject = item.Project.Id }, item.Project.DisplayName + " 선택 완료 · Editor는 작업 준비에서 자동으로 찾습니다.");
    });

    // User chooses a displayed CLI / Connector pair; all inspection and registration commit together.
    public Task<CatalogResult> UseDiscoveredReleaseAsync(string cliPath, string? connectorPath, string label) => ChangeAsync(async doc =>
    {
        cliPath = LocalInspector.NormalizePath(cliPath);
        var cliInfo = await inspector.InspectArtifactAsync(cliPath, ArtifactKind.CliExecutable);
        if (cliInfo.Status != InspectionStatus.Available) return Reject(doc, "CLI를 등록하지 않았습니다. " + cliInfo.Detail);
        ArtifactObservation? connectorInfo = null;
        if (connectorPath is not null)
        {
            connectorPath = LocalInspector.NormalizePath(connectorPath);
            connectorInfo = await inspector.InspectArtifactAsync(connectorPath, ArtifactKind.ConnectorFolder);
            if (connectorInfo.Status != InspectionStatus.Available) return Reject(doc, "Connector를 확인하지 못해 조합을 등록하지 않았습니다. " + connectorInfo.Detail);
        }
        var artifacts = doc.Artifacts;
        CatalogArtifact Register(string path, ArtifactKind kind, ArtifactObservation info)
        {
            var old = artifacts.FirstOrDefault(x => x.Kind == kind && x.Registered.Sha256 == info.Sha256);
            var item = old is null ? new CatalogArtifact(Guid.NewGuid(), Name(null, path), kind, path, info, info) :
                old with { Path = path, Current = info };
            artifacts = old is null ? artifacts.Add(item) : artifacts.Replace(old, item);
            return item;
        }
        var cli = Register(cliPath, ArtifactKind.CliExecutable, cliInfo);
        var connector = connectorInfo is null ? null : Register(connectorPath!, ArtifactKind.ConnectorFolder, connectorInfo);
        var axis = connector is null ? ComparisonAxis.CliOnly : ComparisonAxis.CliAndConnector;
        var release = doc.Releases.FirstOrDefault(x => x.CliId == cli.Id && x.ConnectorId == connector?.Id && x.Axis == axis);
        var all = doc.Releases;
        if (release is null) { release = new(ReleaseId.New(), Name(label, cliPath), axis, cli.Id, connector?.Id); all = all.Add(release); }
        return Accept(doc with { Artifacts = artifacts, Releases = all, SelectedRelease = release.Id },
            release.Label + " 등록·선택 완료 · 프로젝트 적용은 설치 시작 시 수행합니다.");
    });
}
