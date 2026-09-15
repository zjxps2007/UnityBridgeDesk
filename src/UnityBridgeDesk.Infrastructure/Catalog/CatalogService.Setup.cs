using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Infrastructure.Catalog;

public sealed partial class CatalogService
{
    // Commit the complete verified comparison set in one save, preserving existing identities by content.
    public Task<CatalogResult> UsePreparedReleasesAsync(IReadOnlyList<PreparedBridgeRelease> prepared) => ChangeAsync(async doc =>
    {
        if (prepared.Count == 0) return Reject(doc, "준비한 버전이 없습니다.");
        var artifacts = doc.Artifacts;
        var releases = doc.Releases;
        ReleaseId? selected = null;
        foreach (var item in prepared)
        {
            var cliInfo = await inspector.InspectArtifactAsync(item.CliPath, ArtifactKind.CliExecutable);
            var connectorInfo = await inspector.InspectArtifactAsync(item.ConnectorPath, ArtifactKind.ConnectorFolder);
            if (cliInfo.Status != InspectionStatus.Available || cliInfo.Sha256 != item.Release.CliSha256 ||
                connectorInfo.Status != InspectionStatus.Available || connectorInfo.Sha256 != item.Release.ConnectorSha256 ||
                connectorInfo.DeclaredVersion != item.Release.Version)
                return Reject(doc, "준비한 파일이 변경되어 버전 등록을 완료하지 않았습니다. 다시 준비해 주세요.");
            CatalogArtifact Register(string path, ArtifactKind kind, ArtifactObservation info)
            {
                var old = artifacts.FirstOrDefault(x => x.Kind == kind && x.Registered.Sha256 == info.Sha256);
                var value = old is null ? new CatalogArtifact(Guid.NewGuid(), $"UnityBridge {item.Release.Version} · {kind}", kind, path, info, info) : old with { Path = path, Current = info };
                artifacts = old is null ? artifacts.Add(value) : artifacts.Replace(old, value);
                return value;
            }
            var cli = Register(item.CliPath, ArtifactKind.CliExecutable, cliInfo);
            var connector = Register(item.ConnectorPath, ArtifactKind.ConnectorFolder, connectorInfo);
            var release = releases.FirstOrDefault(x => x.CliId == cli.Id && x.ConnectorId == connector.Id && x.Axis == ComparisonAxis.CliAndConnector);
            if (release is null)
            { release = new(ReleaseId.New(), "UnityBridge " + item.Release.Version, ComparisonAxis.CliAndConnector, cli.Id, connector.Id); releases = releases.Add(release); }
            selected ??= release.Id;
        }
        return Accept(doc with { Artifacts = artifacts, Releases = releases, SelectedRelease = selected }, "CLI·Connector를 버전별로 검증하고 보관함에 등록했습니다.");
    });
}
