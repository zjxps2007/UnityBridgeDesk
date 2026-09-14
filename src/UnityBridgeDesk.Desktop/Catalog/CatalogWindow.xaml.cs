using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Text.Json;
using Microsoft.Win32;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Catalog;

namespace UnityBridgeDesk.Desktop.Catalog;

public sealed record CatalogRow(string Title, string Summary, object? Value)
{
    public override string ToString() => Title + " · " + Summary;
}

public partial class CatalogWindow : Window
{
    private readonly CatalogService catalog;
    private bool busy;
    public CatalogWindow(CatalogService catalog)
    {
        this.catalog = catalog; InitializeComponent();
        Width = Math.Min(1000, SystemParameters.WorkArea.Width); Height = Math.Min(760, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(740, SystemParameters.WorkArea.Width); MinHeight = Math.Min(540, SystemParameters.WorkArea.Height);
        RefreshView();
        ProjectForm.IsExpanded = catalog.Document.Projects.IsEmpty;
        ArtifactForm.IsExpanded = catalog.Document.Artifacts.IsEmpty;
        ReleaseForm.IsExpanded = catalog.Document.Releases.IsEmpty;
        Loaded += async (_, _) => await DiscoverAsync();
        Closed += (_, _) => discoveryCancellation.Cancel();
    }
    private void RefreshView()
    {
        var projectId = ProjectItem?.Project.Id ?? catalog.Document.SelectedProject;
        var artifactId = ArtifactItem?.Id; var releaseId = ReleaseItem?.Id ?? catalog.Document.SelectedRelease;
        var cliId = ((ReleaseCli.SelectedItem as CatalogRow)?.Value as CatalogArtifact)?.Id;
        var connectorId = ((ReleaseConnector.SelectedItem as CatalogRow)?.Value as CatalogArtifact)?.Id;
        var doc = catalog.Document;
        Projects.ItemsSource = doc.Projects.Select(x => new CatalogRow((doc.SelectedProject == x.Project.Id ? "● " : "") + x.Project.DisplayName, StatusName(x.Observation.Status), x)).ToArray();
        Artifacts.ItemsSource = doc.Artifacts.Select(x => new CatalogRow(x.Label, $"{(x.Kind == ArtifactKind.CliExecutable ? "CLI" : "Connector")} · {StatusName(x.Current.Status)}", x)).ToArray();
        Releases.ItemsSource = doc.Releases.Select(x => new CatalogRow((doc.SelectedRelease == x.Id ? "● " : "") + x.Label, AxisName(x.Axis), x)).ToArray();
        Projects.SelectedItem = Projects.Items.Cast<CatalogRow>().FirstOrDefault(x => ((CatalogProject)x.Value!).Project.Id == projectId) ?? Projects.Items.Cast<CatalogRow>().LastOrDefault();
        Artifacts.SelectedItem = Artifacts.Items.Cast<CatalogRow>().FirstOrDefault(x => ((CatalogArtifact)x.Value!).Id == artifactId) ?? Artifacts.Items.Cast<CatalogRow>().LastOrDefault();
        Releases.SelectedItem = Releases.Items.Cast<CatalogRow>().FirstOrDefault(x => ((CatalogRelease)x.Value!).Id == releaseId) ?? Releases.Items.Cast<CatalogRow>().LastOrDefault();
        ReleaseCli.ItemsSource = doc.Artifacts.Where(x => x.Kind == ArtifactKind.CliExecutable && x.MatchesRegistration).Select(x => new CatalogRow(x.Label, "", x)).ToArray();
        ReleaseCli.SelectedItem = ReleaseCli.Items.Cast<CatalogRow>().FirstOrDefault(x => ((CatalogArtifact)x.Value!).Id == cliId) ?? ReleaseCli.Items.Cast<CatalogRow>().FirstOrDefault();
        ReleaseConnector.ItemsSource = new[] { new CatalogRow("지정하지 않음", "", null) }.Concat(doc.Artifacts.Where(x => x.Kind == ArtifactKind.ConnectorFolder && x.MatchesRegistration).Select(x => new CatalogRow(x.Label, "", x))).ToArray();
        ReleaseConnector.SelectedItem = ReleaseConnector.Items.Cast<CatalogRow>().FirstOrDefault(x => (x.Value as CatalogArtifact)?.Id == connectorId) ?? ReleaseConnector.Items[0];
        StorageNotice.Text = catalog.Notice;
        ProjectChanged(this, null!); ArtifactChanged(this, null!); ReleaseChanged(this, null!);
    }
    private CatalogProject? ProjectItem => (Projects.SelectedItem as CatalogRow)?.Value as CatalogProject;
    private CatalogArtifact? ArtifactItem => (Artifacts.SelectedItem as CatalogRow)?.Value as CatalogArtifact;
    private CatalogRelease? ReleaseItem => (Releases.SelectedItem as CatalogRow)?.Value as CatalogRelease;
    public static string StatusName(InspectionStatus status) => status switch
    { InspectionStatus.Available => "로컬 확인", InspectionStatus.Missing => "경로 없음", InspectionStatus.Invalid => "형식 확인 필요", InspectionStatus.Unavailable => "읽기 실패", InspectionStatus.Changed => "내용 변경 감지", _ => "미확인" };
    public static string AxisName(ComparisonAxis axis) => axis switch { ComparisonAxis.CliOnly => "CLI 전용 비교", ComparisonAxis.CliAndConnector => "CLI + Connector 조합 비교", _ => "비교 축 미정" };
    public static string PackageName(PackageState state) => state switch
    { PackageState.NotDeclared => "manifest에 선언 없음", PackageState.Declared => "manifest 선언만 확인", PackageState.LockRecorded => "lock 기록 확인 · 실제 로드 미확인", PackageState.Embedded => "내장 package.json 확인 · 실제 로드 미확인", _ => "미확인" };
    private static string Known(string? value) => string.IsNullOrWhiteSpace(value) ? "미확인" : value;
    private static string Time(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
    private void ProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectDetail is null) return;
        var item = ProjectItem; ProjectActions.IsEnabled = item is not null && catalog.CanWrite;
        ProjectDetail.Text = item is null ? "등록한 프로젝트가 없어요. 폴더를 골라 첫 항목을 추가해 보세요." :
            $"{item.Project.DisplayName}\n{item.Project.RootPath}\n\n{item.Observation.Detail}\nEditor 선언: {Known(item.Observation.EditorVersion)}\nConnector: {PackageName(item.Observation.PackageState)}\n선언 출처: {Known(item.Observation.PackageSource)}\n패키지 선언 버전: {Known(item.Observation.PackageVersion)}\nlock 커밋: {Known(item.Observation.LockCommit)}\n확인: {Time(item.Observation.CheckedAtUtc)}\nProject ID: {item.Project.Id.Value}";
    }
    private void ArtifactChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ArtifactDetail is null) return;
        var item = ArtifactItem; ArtifactActions.IsEnabled = item is not null && catalog.CanWrite;
        ArtifactDetail.Text = item is null ? "파일을 등록하면 해시와 확인 정보를 여기에 보여 드려요." :
            $"표시 이름: {item.Label}\n{item.Path}\n\n{item.Current.Detail}\n등록 SHA-256:\n{Known(item.Registered.Sha256)}\n최근 SHA-256:\n{Known(item.Current.Sha256)}\n최근 파일 크기: {item.Current.SizeBytes?.ToString("N0") ?? "미확인"} bytes\n내부 선언 버전: {Known(item.Current.DeclaredVersion)}\n내부 선언 출처: {Known(item.Current.DeclaredSource)}\nGit HEAD 참고: {Known(item.Current.GitHead)}\nHEAD와 작업 파일의 일치 여부는 미확인\n최근 확인: {Time(item.Current.CheckedAtUtc)}\nArtifact ID: {item.Id}";
    }
    private void ReleaseChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReleaseDetail is null) return;
        var item = ReleaseItem; ReleaseActions.IsEnabled = item is not null && catalog.CanWrite;
        if (item is null) { ReleaseDetail.Text = "로컬 파일을 등록한 다음 조합을 만들어 주세요."; return; }
        var cli = catalog.Document.Artifacts.Single(x => x.Id == item.CliId);
        var connector = catalog.Document.Artifacts.SingleOrDefault(x => x.Id == item.ConnectorId);
        ReleaseDetail.Text = $"표시 이름: {item.Label}\n{AxisName(item.Axis)}\nCLI: {cli.Label} · {StatusName(cli.Current.Status)}\nConnector: {(connector is null ? "지정하지 않음" : connector.Label + " · " + StatusName(connector.Current.Status))}\n설치 상태: 이 조합으로부터 추정하지 않음\nRelease ID: {item.Id.Value}";
    }
    private async Task<bool> Perform(Func<Task<CatalogResult>> action)
    {
        if (busy) return false;
        busy = true; Tabs.IsEnabled = false; Done.IsEnabled = false;
        Status.Text = "로컬 정보를 확인하고 저장하고 있어요…";
        try { var result = await action(); RefreshView(); Status.Text = result.Message; return result.Success; }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        {Status.Text="요청을 완료하지 못했습니다. 입력과 경로를 확인한 뒤 다시 시도해 주세요.\n"+error.Message;return false;}
        finally { busy = false; Tabs.IsEnabled = true; Done.IsEnabled = true; }
    }
    private string? PickFolder(string title)
    {
        var picker = new OpenFolderDialog { Title = title, Multiselect = false };
        return picker.ShowDialog(this) == true ? picker.FolderName : null;
    }
    private string? PickArtifact(ArtifactKind kind)
    {
        if (kind == ArtifactKind.ConnectorFolder) return PickFolder("package.json이 있는 Connector 폴더 선택");
        var picker = new OpenFileDialog { Title = "로컬 UnityBridge CLI 선택", Filter = "Windows 실행 파일 (*.exe)|*.exe", CheckFileExists = true, Multiselect = false };
        return picker.ShowDialog(this) == true ? picker.FileName : null;
    }
    private ArtifactKind Kind => ArtifactType.SelectedIndex == 1 ? ArtifactKind.ConnectorFolder : ArtifactKind.CliExecutable;
    private void BrowseProject(object sender, RoutedEventArgs e) { if (PickFolder("Unity 프로젝트 폴더 선택") is { } path) ProjectPath.Text = path; }
    private void BrowseArtifact(object sender, RoutedEventArgs e) { if (PickArtifact(Kind) is { } path) ArtifactPath.Text = path; }
    private async void AddProject(object sender, RoutedEventArgs e) { if (await Perform(() => catalog.RegisterProjectAsync(ProjectPath.Text, ProjectLabel.Text))) ProjectForm.IsExpanded = false; }
    private async void AddArtifact(object sender, RoutedEventArgs e) { if (await Perform(() => catalog.RegisterArtifactAsync(ArtifactPath.Text, Kind, ArtifactLabel.Text))) ArtifactForm.IsExpanded = false; }
    private async void SelectProject(object sender, RoutedEventArgs e) { if (ProjectItem is { } item) await Perform(() => catalog.SelectProjectAsync(item.Project.Id)); }
    private async void RefreshProject(object sender, RoutedEventArgs e) { if (ProjectItem is { } item) await Perform(() => catalog.RefreshProjectAsync(item.Project.Id)); }
    private async void RelocateProject(object sender, RoutedEventArgs e) { if (ProjectItem is { } item && PickFolder("같은 프로젝트의 새 위치 선택") is { } path) await Perform(() => catalog.RelocateProjectAsync(item.Project.Id, path)); }
    private async void RemoveProject(object sender, RoutedEventArgs e) { if (ProjectItem is { } item) await Perform(() => catalog.RemoveProjectAsync(item.Project.Id)); }
    private async void RefreshArtifact(object sender, RoutedEventArgs e) { if (ArtifactItem is { } item) await Perform(() => catalog.RefreshArtifactAsync(item.Id)); }
    private async void RelocateArtifact(object sender, RoutedEventArgs e) { if (ArtifactItem is { } item && PickArtifact(item.Kind) is { } path) await Perform(() => catalog.RelocateArtifactAsync(item.Id, path)); }
    private async void RemoveArtifact(object sender, RoutedEventArgs e) { if (ArtifactItem is { } item) await Perform(() => catalog.RemoveArtifactAsync(item.Id)); }
    private async void AddRelease(object sender, RoutedEventArgs e)
    {
        if ((ReleaseCli.SelectedItem as CatalogRow)?.Value is not CatalogArtifact cli) { Status.Text = "먼저 확인 가능한 CLI 파일을 등록해 주세요."; return; }
        var connector = (ReleaseConnector.SelectedItem as CatalogRow)?.Value as CatalogArtifact;
        if (await Perform(() => catalog.AddReleaseAsync(ReleaseLabel.Text, (ComparisonAxis)Axis.SelectedIndex, cli.Id, connector?.Id))) ReleaseForm.IsExpanded = false;
    }
    private async void SelectRelease(object sender, RoutedEventArgs e) { if (ReleaseItem is { } item) await Perform(() => catalog.SelectReleaseAsync(item.Id)); }
    private async void RemoveRelease(object sender, RoutedEventArgs e) { if (ReleaseItem is { } item) await Perform(() => catalog.RemoveReleaseAsync(item.Id)); }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void CatalogClosing(object? sender, CancelEventArgs e) { if (busy){e.Cancel = true;Status.Text="현재 확인·저장을 마친 뒤 닫을 수 있어요. 잠시 기다려 주세요.";} }
}
