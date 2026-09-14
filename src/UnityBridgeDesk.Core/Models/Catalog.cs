using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace UnityBridgeDesk.Core.Models;

public enum InspectionStatus { Unknown, Available, Missing, Invalid, Unavailable, Changed }
public enum ArtifactKind { CliExecutable, ConnectorFolder }
public enum ComparisonAxis { Unset, CliOnly, CliAndConnector }
public enum PackageState { Unknown, NotDeclared, Declared, LockRecorded, Embedded }

// These are observations of local files, never proof of a running Editor or installed package.
public sealed record ProjectObservation(InspectionStatus Status, string Detail, DateTimeOffset CheckedAtUtc,
    string? EditorVersion, PackageState PackageState, string? PackageSource,
    string? PackageVersion, string? LockCommit);

public sealed record CatalogProject(ProjectRef Project, ProjectObservation Observation);

public sealed record ArtifactObservation(InspectionStatus Status, string Detail, DateTimeOffset CheckedAtUtc,
    string? Sha256, long? SizeBytes, string? DeclaredVersion, string? DeclaredSource, string? GitHead);

// Registered is immutable evidence. Refresh replaces Current, not Registered or the identity.
public sealed record CatalogArtifact(Guid Id, string Label, ArtifactKind Kind, string Path,
    ArtifactObservation Registered, ArtifactObservation Current)
{
    [JsonIgnore]
    public bool MatchesRegistration => Current.Status == InspectionStatus.Available &&
        Current.Sha256 is not null && Current.Sha256 == Registered.Sha256;
}

public sealed record CatalogRelease(ReleaseId Id, string Label, ComparisonAxis Axis, Guid CliId, Guid? ConnectorId);

public sealed record CatalogDocument(ImmutableArray<CatalogProject> Projects,
    ImmutableArray<CatalogArtifact> Artifacts, ImmutableArray<CatalogRelease> Releases,
    ProjectId? SelectedProject, ReleaseId? SelectedRelease)
{
    public static CatalogDocument Empty => new([], [], [], null, null);

    public void Validate()
    {
        if (Projects.IsDefault || Artifacts.IsDefault || Releases.IsDefault ||
            Projects.Length > 500 || Artifacts.Length > 1000 || Releases.Length > 1000)
            throw new ArgumentException("Invalid catalog collections.");
        Unique(Projects.Select(x => x.Project.Id.Value));
        Unique(Artifacts.Select(x => x.Id)); Unique(Releases.Select(x => x.Id.Value));
        foreach (var item in Projects)
        {
            item.Project.Validate();
            var o = item.Observation;
            CheckStatus(o.Status, o.Detail, o.CheckedAtUtc);
            if (!Enum.IsDefined(o.PackageState)) throw new ArgumentException("Invalid package state.");
            if (item.Project.UnityBuild != o.EditorVersion) throw new ArgumentException("Editor observation mismatch.");
        }
        foreach (var item in Artifacts)
        {
            ContractGuard.Text(item.Label);
            if (!Enum.IsDefined(item.Kind) || !Path.IsPathFullyQualified(item.Path)) throw new ArgumentException("Invalid artifact.");
            foreach (var o in new[] { item.Registered, item.Current })
            {
                CheckStatus(o.Status, o.Detail, o.CheckedAtUtc);
                BridgeReleaseRef.ValidateHash(o.Sha256);
                if (o.SizeBytes < 0 || o.Status is InspectionStatus.Available or InspectionStatus.Changed && o.Sha256 is null)
                    throw new ArgumentException("Invalid artifact observation.");
            }
        }
        foreach (var item in Releases)
        {
            ContractGuard.Text(item.Label);
            if (!Enum.IsDefined(item.Axis) || !Artifacts.Any(x => x.Id == item.CliId && x.Kind == ArtifactKind.CliExecutable) ||
                item.ConnectorId is { } connector && !Artifacts.Any(x => x.Id == connector && x.Kind == ArtifactKind.ConnectorFolder) ||
                item.Axis == ComparisonAxis.CliAndConnector && item.ConnectorId is null)
                throw new ArgumentException("Invalid release combination.");
        }
        if (SelectedProject is { } project && !Projects.Any(x => x.Project.Id == project) ||
            SelectedRelease is { } release && !Releases.Any(x => x.Id == release))
            throw new ArgumentException("Selection is outside the catalog.");
    }

    // Copies values into the existing shared contract; no tool keeps a live catalog reference in a run.
    public RunDraft CreateDraft(ToolKind tool, BenchmarkModes modes = BenchmarkModes.None)
    {
        var project = Projects.SingleOrDefault(x => x.Project.Id == SelectedProject)
            ?? throw new InvalidOperationException("프로젝트를 선택해 주세요.");
        if (project.Observation.Status != InspectionStatus.Available)
            throw new InvalidOperationException("프로젝트 경로를 다시 확인해 주세요.");
        var release = Releases.SingleOrDefault(x => x.Id == SelectedRelease)
            ?? throw new InvalidOperationException("참고 릴리스를 선택해 주세요.");
        var cli = Artifacts.Single(x => x.Id == release.CliId);
        var connector = Artifacts.SingleOrDefault(x => x.Id == release.ConnectorId);
        if (!cli.MatchesRegistration || connector is not null && !connector.MatchesRegistration)
            throw new InvalidOperationException("릴리스 파일이 등록 때와 다릅니다. 파일을 다시 확인해 주세요.");
        var draft = new RunDraft { Tool = tool, Project = project.Project with { }, Modes = modes };
        draft.Releases.Add(new(release.Id, release.Label, cli.Path, cli.Registered.Sha256,
            null, connector?.Path, connector?.Registered.Sha256, connector?.Registered.DeclaredVersion)
        { ComparisonAxis = release.Axis, ConnectorHashScheme = connector is null ? null : "connector-tree-v1",
            ConnectorDeclaredSource = connector?.Registered.DeclaredSource, ConnectorGitHead = connector?.Registered.GitHead });
        return draft;
    }

    private static void Unique(IEnumerable<Guid> ids)
    {
        var all = ids.ToArray();
        if (all.Any(x => x == Guid.Empty) || all.Distinct().Count() != all.Length) throw new ArgumentException("Invalid catalog identity.");
    }
    private static void CheckStatus(InspectionStatus status, string detail, DateTimeOffset time)
    {
        if (!Enum.IsDefined(status) || time == default) throw new ArgumentException("Invalid observation.");
        ContractGuard.Text(detail);
    }
}
