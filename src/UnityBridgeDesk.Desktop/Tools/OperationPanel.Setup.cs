using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.Storage;

namespace UnityBridgeDesk.Desktop.Tools;

public sealed partial class OperationPanel
{
    private Border? bridgeSetupCard;
    private readonly Button bridgeSetupStart = new() { Content = "실험 환경 자동 준비", Margin = new(0,0,8,6), MinHeight = 38 };
    private readonly Button bridgeSetupCancel = new() { Content = "준비 취소", Visibility = Visibility.Collapsed, Margin = new(0,0,8,6) };
    private readonly TextBlock bridgeSetupStatus = Text("0.2.0 ↔ 0.2.1 · CLI와 Connector를 함께 준비합니다.", 13);
    private readonly ComboBox bridgeProjectChoice = new() { MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = nameof(LocalCandidate.Label) };
    private readonly StackPanel bridgeSetupOptions = new();
    private Expander? bridgeSetupDetails;
    private CancellationTokenSource? bridgeSetupCancellation;
    private bool bridgeSetupBusy, choosingSetupProject, bridgeEnvironmentReady;

    private void BuildBridgeSetupCard()
    {
        var body = new StackPanel();
        body.Children.Add(Text("경로를 몰라도 시작할 수 있어요.", 16));
        body.Children.Add(bridgeSetupStatus);
        body.Children.Add(Text("찾은 파일은 검증해 복사하고, 없는 파일은 공식 GitHub에서 받습니다. 두 버전은 Desk 전용 폴더에 따로 보관합니다.", 11));
        var actions = new WrapPanel(); actions.Children.Add(bridgeSetupStart); actions.Children.Add(bridgeSetupCancel); body.Children.Add(actions);
        var bar = new ProgressBar { Height = 4, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new(0,2,0,8) };
        bridgeSetupStart.IsEnabledChanged += (_,_) => bar.Visibility = bridgeSetupBusy ? Visibility.Visible : Visibility.Collapsed;
        body.Children.Add(bar);
        bridgeSetupOptions.Children.Add(Text("기준 프로젝트 · 이전 선택을 우선 사용합니다. 여러 개가 있으면 여기서 선택하세요.", 11));
        bridgeSetupOptions.Children.Add(bridgeProjectChoice);
        var browse = new Button { Content = "프로젝트 폴더 선택", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0,6,0,8) };
        browse.Click += async (_,_) => {
            if (bridgeSetupBusy) return;
            var picker = new OpenFolderDialog { Title = "Assets · Packages · ProjectSettings가 있는 Unity 프로젝트" };
            if (picker.ShowDialog(Window.GetWindow(this)) == true) await ChooseSetupProjectAsync(picker.FolderName);
        };
        bridgeSetupOptions.Children.Add(browse);
        var folder = new Button { Content = "기존 CLI·Connector가 있는 폴더 추가", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0,0,0,8) };
        folder.Click += async (_,_) => {
            if (bridgeSetupBusy) return;
            var picker = new OpenFolderDialog { Title = "기존 Bridge 파일을 찾아볼 폴더" };
            if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
            try {
                bool saved = await new LocalSetupStore(runtime.DataRoot).RememberAsync(folder: picker.FolderName);
                if (!disposed) bridgeSetupStatus.Text = saved ? "검색 위치를 추가했어요. ‘실험 환경 자동 준비’를 누르면 이 폴더도 확인합니다." : "검색 위치를 저장하지 못했어요. 폴더 권한을 확인하세요.";
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { if (!disposed) bridgeSetupStatus.Text = e.Message; }
        };
        bridgeSetupOptions.Children.Add(folder);
        bridgeSetupOptions.Children.Add(Text("자동 준비 대상은 0.2.0·0.2.1입니다. 다른 버전이나 직접 수정한 파일은 ‘프로젝트·버전 보관함’에서 등록하세요. Unity는 설치된 Editor를 연결하며, 복제·Connector 적용·기동은 ‘시행 시작’ 후 진행합니다.", 11));
        bridgeSetupDetails = new Expander { Header = "프로젝트 선택 · 다른 위치의 파일", Content = bridgeSetupOptions };
        body.Children.Add(bridgeSetupDetails);
        bridgeSetupCard = Card(body, "Mint"); benchmarkForm.Children.Add(bridgeSetupCard);
        AutomationProperties.SetAutomationId(bridgeSetupStart, "BridgeAutoSetup");
        AutomationProperties.SetAutomationId(bridgeSetupCancel, "BridgeAutoSetupCancel");
        AutomationProperties.SetAutomationId(bridgeSetupStatus, "BridgeAutoSetupStatus");
        AutomationProperties.SetAutomationId(bridgeProjectChoice, "BridgeSetupProject");
        bridgeSetupStart.Click += async (_,_) => await PrepareBridgeEnvironmentAsync();
        bridgeSetupCancel.Click += (_,_) => { bridgeSetupCancellation?.Cancel(); bridgeSetupCancel.IsEnabled = false; bridgeSetupStatus.Text = "준비를 취소하고 임시 파일을 정리하고 있어요…"; };
        bridgeProjectChoice.SelectionChanged += async (_,_) => {
            if (!choosingSetupProject && !bridgeSetupBusy && bridgeProjectChoice.SelectedItem is LocalCandidate candidate)
                await ChooseSetupProjectAsync(candidate.Path);
        };
    }
    private void UpdateBridgeSetupControls()
    {
        bridgeSetupStart.IsEnabled = !loading && !bridgeSetupBusy && !benchmarkBusy && !benchmarkReviewBusy && !benchmarkResetBusy && !resolvingPaths;
        bridgeSetupCancel.Visibility = bridgeSetupBusy ? Visibility.Visible : Visibility.Collapsed;
        bridgeSetupOptions.IsEnabled = !bridgeSetupBusy;
    }
    private async Task ChooseSetupProjectAsync(string path)
    {
        try {
            var result = await catalog.UseDiscoveredProjectAsync(path);
            if (disposed) return;
            bridgeSetupStatus.Text = result.Success ? "프로젝트를 선택했어요. ‘실험 환경 자동 준비’로 버전 파일과 Editor를 연결하세요." : result.Message;
            if (result.Success) { Invalidate(); await ResolveLocalSetupAsync(); }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { if (!disposed) bridgeSetupStatus.Text = e.Message; }
    }
    private async Task PrepareBridgeEnvironmentAsync()
    {
        if (loading || disposed || bridgeSetupBusy || benchmarkBusy || benchmarkReviewBusy || benchmarkResetBusy || resolvingPaths) return;
        if (!catalog.CanWrite) { bridgeSetupStatus.Text = catalog.Notice; return; }
        if (runtime.IsRunning) { bridgeSetupStatus.Text = "진행 중인 작업을 마친 뒤 환경을 준비해 주세요."; return; }
        bridgeSetupBusy = true; bridgeSetupCancel.IsEnabled = true;
        using var cancellation = new CancellationTokenSource(); bridgeSetupCancellation = cancellation;
        Invalidate(); UpdateBenchmarkFooter();
        bridgeSetupStatus.Text = "1/3 · 기본 위치와 이전 선택을 확인하고 있어요…";
        try
        {
            var memory = await new LocalSetupStore(runtime.DataRoot).LoadAsync();
            var found = await localDiscovery.ScanAsync(catalog.Document, memory.Folders, memory.Paths.Values, cancellation.Token, memory.Paths);
            cancellation.Token.ThrowIfCancellationRequested(); if (disposed) return;
            var projects = found.Candidates.Where(x => x.Kind == DiscoveryKind.Project).ToArray();
            var current = catalog.Document.Projects.FirstOrDefault(x => x.Project.Id == catalog.Document.SelectedProject);
            choosingSetupProject = true;
            try {
                bridgeProjectChoice.ItemsSource = projects;
                bridgeProjectChoice.SelectedItem = projects.FirstOrDefault(x => string.Equals(x.Path, current?.Project.RootPath, StringComparison.OrdinalIgnoreCase));
            } finally { choosingSetupProject = false; }
            string? projectProblem = null;
            if (current is not null)
            {
                var result = await catalog.UseDiscoveredProjectAsync(current.Project.RootPath);
                if (!result.Success) { bridgeSetupDetails!.IsExpanded = true; projectProblem = result.Message; }
            }
            else if (projects.Length == 1)
            { var result = await catalog.UseDiscoveredProjectAsync(projects[0].Path); if (!result.Success) projectProblem = result.Message; }
            else bridgeSetupDetails!.IsExpanded = true;
            choosingSetupProject = true;
            try { bridgeProjectChoice.SelectedItem = projects.FirstOrDefault(x => catalog.Document.Projects.Any(p => p.Project.Id == catalog.Document.SelectedProject && string.Equals(x.Path, p.Project.RootPath, StringComparison.OrdinalIgnoreCase))); }
            finally { choosingSetupProject = false; }
            var progress = new Progress<string>(message => { if (!disposed && bridgeSetupCancellation == cancellation && !cancellation.IsCancellationRequested) bridgeSetupStatus.Text = "2/3 · " + message; });
            var prepared = await bridgeSetup.PrepareAsync(found.Candidates, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); if (disposed) return;
            var registered = await catalog.UsePreparedReleasesAsync(prepared);
            if (!registered.Success) throw new IOException(registered.Message);
            cancellation.Token.ThrowIfCancellationRequested(); if (disposed) return;
            var ids = catalog.Document.Releases.Where(release => prepared.Any(item =>
                catalog.Document.Artifacts.Any(x => x.Id == release.CliId && x.Registered.Sha256 == item.Release.CliSha256) &&
                catalog.Document.Artifacts.Any(x => x.Id == release.ConnectorId && x.Registered.Sha256 == item.Release.ConnectorSha256) &&
                release.Axis == ComparisonAxis.CliAndConnector)).Select(x => x.Id).ToArray();
            SetReleaseSelection(ids);
            bridgeSetupStatus.Text = "3/3 · Unity Editor와 실행 조건 확인 중…";
            await ResolveLocalSetupAsync();
            cancellation.Token.ThrowIfCancellationRequested(); if (disposed) return;
            if (catalog.Document.SelectedProject is null || projectProblem is not null)
            {
                bridgeSetupDetails!.IsExpanded = true;
                bridgeSetupStatus.Text = "CLI·Connector 준비 완료 · " + (projectProblem ?? "‘프로젝트 선택’에서 기준 프로젝트를 골라 주세요.");
                UpdateBenchmarkSetup();
            }
            else if (await PrepareAsync())
            {
                bridgeEnvironmentReady = true;
                bridgeSetupStatus.Text = "준비 완료 · 0.2.0 ↔ 0.2.1\n아래 ‘시행 시작’ 버튼을 누르면 복제 환경에서 실험합니다.";
                setupGuide.Text = "버전별 CLI·Connector와 실행 조건을 확인했어요.\n" + setupSummary.Text;
            }
            else
            {
                bridgeSetupStatus.Text = "CLI·Connector 준비 완료 · " + preview.Text.Split('\n')[0];
                if (catalog.Document.SelectedProject is null) bridgeSetupDetails!.IsExpanded = true;
                UpdateBenchmarkSetup();
            }
            await FlushInputAsync();
        }
        catch (OperationCanceledException) { if (!disposed) bridgeSetupStatus.Text = cancellation.IsCancellationRequested ? "준비를 취소했어요. 검증이 끝난 버전 파일은 다음 준비 때 재사용합니다." : "다운로드 시간이 초과됐어요. 인터넷 연결을 확인한 뒤 다시 준비해 주세요."; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or HttpRequestException or System.Text.Json.JsonException)
        { if (!disposed) bridgeSetupStatus.Text = "준비를 완료하지 못했어요. " + (e is HttpRequestException ? "공식 GitHub 다운로드를 확인해 주세요. 인터넷 연결 후 다시 준비하거나 파일이 있는 폴더를 추가하세요." : e.Message); }
        finally
        {
            bridgeSetupBusy = false; bridgeSetupCancellation = null;
            if (!disposed) { UpdateBenchmarkFooter(); InputStatusChanged?.Invoke(bridgeSetupStatus.Text); }
        }
    }
    private void InvalidateBridgeReadiness()
    {
        if (!bridgeEnvironmentReady) return;
        bridgeEnvironmentReady = false;
        if (!bridgeSetupBusy) bridgeSetupStatus.Text = "버전 파일은 보관되어 있어요. 변경한 조건은 아래 ‘구성 확인’으로 다시 확인하세요.";
    }
}
