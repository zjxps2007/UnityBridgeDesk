using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Desktop.Catalog;

// One presentation contract for all three tools. A selection never reports an installation.
public sealed record TargetSummary(ProjectId? ProjectId, ReleaseId? ReleaseId,
    string Project, string ProjectPath, string Release)
{
    public static TargetSummary From(CatalogDocument catalog)
    {
        var project = catalog.Projects.SingleOrDefault(x => x.Project.Id == catalog.SelectedProject);
        var release = catalog.Releases.SingleOrDefault(x => x.Id == catalog.SelectedRelease);
        return new(project?.Project.Id, release?.Id,
            project is null ? "아직 선택하지 않았어요" : project.Project.DisplayName + " · " + CatalogWindow.StatusName(project.Observation.Status),
            project?.Project.RootPath ?? "프로젝트를 등록하거나 선택하세요.",
            release is null ? "아직 선택하지 않았어요" : release.Label + " · " + CatalogWindow.AxisName(release.Axis));
    }
}
