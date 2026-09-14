using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Desktop.Shell;
using UnityBridgeDesk.Desktop.Tools;
using UnityBridgeDesk.Infrastructure.Execution;

namespace UnityBridgeDesk.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<RunId, SidebarRun> sidebarRuns = [];
    private IReadOnlyDictionary<ToolKind, SidebarRecent> sidebarHistory = new Dictionary<ToolKind, SidebarRecent>();
    private readonly DispatcherTimer sidebarTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private SidebarRecent? shownRecent;
    private bool sidebarDisposed, sidebarReading, sidebarHistoryLoaded;
    private string? sidebarHistoryError;
    private int sidebarReadVersion;

    private OperationPanel EnsureOperation(ToolKind tool)
    {
        if (operations.TryGetValue(tool, out var existing)) return existing;
        var panel = new OperationPanel(runtime ?? throw new InvalidOperationException("실행 환경이 연결되지 않았습니다."), catalog, tool, session);
        panel.CatalogRequested += OpenCatalog;
        panel.InputStatusChanged += message => SaveStatusText.Text = message;
        panel.SummaryChanged += UpdateSidebar;
        operations[tool] = panel;
        return panel;
    }
    private void InitializeSidebar()
    {
        sidebarTimer.Tick += (_, _) => { if (Widgets.IsVisible && WindowState != WindowState.Minimized) UpdateSidebarElapsed(); };
    }
    private SidebarSummary CurrentSidebarSummary(ToolKind tool)
    {
        if (operations.TryGetValue(tool, out var panel)) return panel.SidebarSummary();
        var project = catalog.Document.Projects.FirstOrDefault(x => x.Project.Id == catalog.Document.SelectedProject)?.Project;
        var release = catalog.Document.Releases.FirstOrDefault(x => x.Id == catalog.Document.SelectedRelease);
        return new(project?.DisplayName ?? "프로젝트 미선택", release?.Label ?? "버전 미선택",
            tool == ToolKind.Installation ? "패키지와 실행 도구 관리" : tool == ToolKind.AiWork ? "자연어 지시로 Unity 작업" : "같은 조건으로 버전 비교",
            tool == ToolKind.AiWork ? "지시를 적고 AI 실행 구성을 열어 주세요." : "구성 화면에서 실행 조건을 확인하세요.");
    }
    private SidebarRun? ActiveSidebarRun() => sidebarRuns.Values.Where(x => x.Running).OrderBy(x => x.Stage == "대기 중").FirstOrDefault();
    private void ShellRuntimeState(ExecutionState state) => Dispatcher.BeginInvoke(() =>
    {
        if (sidebarDisposed) return;
        session.Observe(state); UpdateShell();
    });
    private void ShellRuntimeNotice(RuntimeNotice notice) => Dispatcher.BeginInvoke(() =>
    {
        if (sidebarDisposed) return;
        if (!sidebarRuns.TryGetValue(notice.RunId, out var run))
        {
            run = new SidebarRun(notice, CurrentSidebarSummary(notice.Tool));
            sidebarRuns[notice.RunId] = run;
            // Retain a bounded set of completed IDs so duplicate terminal notices do not reload history.
            if (sidebarRuns.Count > 128)
                foreach (var old in sidebarRuns.Where(x => !x.Value.Running).Take(sidebarRuns.Count - 128).Select(x => x.Key).ToArray()) sidebarRuns.Remove(old);
        }
        bool finished = run.Apply(notice);
        if (finished) _ = RefreshSidebarHistoryAsync();
        if (finished || notice.Message is "대기 중" or "준비 중" or "실행 중") UpdateShell();
    });
    private void ShellRuntimeProgress(RuntimeProgress progress) => Dispatcher.BeginInvoke(() =>
    {
        if (sidebarDisposed || !sidebarRuns.TryGetValue(progress.RunId, out var run)) return;
        run.Apply(progress); UpdateSidebar();
    });
    private void UpdateSidebar()
    {
        if (!ready || sidebarDisposed) return;
        var active = ActiveSidebarRun();
        var summary = active?.Summary ?? CurrentSidebarSummary(session.ActiveTool);
        SidebarHeading.Text = active is null ? ToolFrame.NameFor(session.ActiveTool) + " · 작업 요약" : ToolFrame.NameFor(active.Tool) + " · 진행 중";
        SidebarProject.Text = summary.Project; SidebarReleases.Text = summary.Releases;
        SidebarCount.Text = active is null ? summary.Trials is { } total && session.ActiveTool == ToolKind.Benchmark ? $"{total:N0}개 시행 예정" : "현재 구성" : active.Stage;
        SidebarCount.TextTrimming = TextTrimming.CharacterEllipsis;
        SidebarCount.ToolTip = SidebarCount.Text;
        SidebarDetail.Text = active is null ? summary.Detail : active.Context.Length > 0 ? active.Context : summary.Detail;
        SidebarHint.Text = active is null ? summary.Hint : "완료 수 기준 · 다른 도구에서도 확인 가능";
        SidebarProgress.Visibility = active?.Total is > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (active?.Total is { } maximum) { SidebarProgress.Maximum = Math.Max(1, maximum); SidebarProgress.Value = active.Completed; }
        SidebarElapsed.Visibility = SidebarProgressButton.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        if (active is null) sidebarTimer.Stop(); else sidebarTimer.Start();
        UpdateSidebarElapsed();
        UpdateSidebarRecent();
        UpdateCompanion();
    }
    private void UpdateSidebarElapsed()
    {
        if (ActiveSidebarRun() is not { } run) return;
        SidebarElapsed.Text = (run.Total is { } total ? $"완료 {run.Completed} / {total}\n" : "") + $"전체 경과 {run.Elapsed:hh\\:mm\\:ss}";
    }
    private void UpdateSidebarRecent()
    {
        SidebarRecentHeading.Text = session.ActiveTool == ToolKind.Benchmark ? "최근 벤치 결과" : "최근 작업 기록";
        SidebarRefresh.IsEnabled = !sidebarReading && ActiveSidebarRun() is null;
        SidebarRefresh.ToolTip = ActiveSidebarRun() is null ? "최근 기록 새로고침" : "실행을 마치면 자동으로 갱신해요.";
        shownRecent = sidebarHistory.GetValueOrDefault(session.ActiveTool);
        SidebarResultButton.IsEnabled = shownRecent is not null && runtime is not null;
        if (shownRecent is { } recent)
        {
            SidebarRecentTitle.Text = recent.Status;
            SidebarRecentDate.Text = $"{recent.UpdatedAt.LocalDateTime:MM.dd HH:mm} · {recent.Project}";
            SidebarRecentCounts.Text = sidebarHistoryError is null ? recent.Counts : "새로고침 실패 · 마지막 확인 기록";
        }
        else
        {
            SidebarRecentTitle.Text = sidebarHistoryError is not null ? "기록을 읽지 못했어요" : !sidebarHistoryLoaded ? "기록을 불러오는 중…" : "아직 기록이 없어요";
            SidebarRecentDate.Text = "";
            SidebarRecentCounts.Text = sidebarHistoryError is not null ? "새로고침으로 다시 확인해 주세요." : "완료한 작업을 여기서 바로 열 수 있어요.";
        }
    }
    private async Task RefreshSidebarHistoryAsync()
    {
        if (sidebarDisposed) return;
        if (runtime is null) { sidebarHistoryLoaded = true; UpdateSidebar(); return; }
        int revision = ++sidebarReadVersion;
        sidebarReading = true; UpdateSidebarRecent();
        try
        {
            var history = await Task.Run(() => SidebarRecent.ReadLatestAsync(runtime.DataRoot));
            if (sidebarDisposed || revision != sidebarReadVersion) return;
            sidebarHistory = history; sidebarHistoryError = null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        { if (!sidebarDisposed && revision == sidebarReadVersion) sidebarHistoryError = error.Message; }
        finally
        {
            if (!sidebarDisposed && revision == sidebarReadVersion) { sidebarReading = false; sidebarHistoryLoaded = true; UpdateSidebar(); }
        }
    }
    private async void SidebarRefreshClick(object sender, RoutedEventArgs e) => await RefreshSidebarHistoryAsync();
    private async void SidebarResultClick(object sender, RoutedEventArgs e)
    {
        if (shownRecent is not { } recent || runtime is null) return;
        OpenTool(recent.Tool);
        var panel = EnsureOperation(recent.Tool); frames[recent.Tool].ShowExecution(panel);
        await panel.ShowHistoryAsync(recent.RunId);
    }
    private void SidebarProgressClick(object sender, RoutedEventArgs e)
    {
        if (ActiveSidebarRun() is not { } run || runtime is null) return;
        OpenTool(run.Tool);
        var panel = EnsureOperation(run.Tool); frames[run.Tool].ShowExecution(panel); panel.ShowProgress();
    }
    private void UpdateCompanion()
    {
        bool working = ActiveSidebarRun() is not null;
        string status = shownRecent?.Status ?? "";
        bool done = status == "완료", attention = !done && status.Length > 0;
        CompanionCaption.Text = working ? "차근차근, 기록하고 있어요." : attention ? "기록을 함께 확인해 봐요." : done ? "결과가 도착했어요." : "같은 시작점, 나란한 결과.";
        string eyes = Companion.IsMouseOver ? "M48,51 Q53,44 58,51 M84,51 Q89,44 94,51" : working ? "M49,50 L57,50 M85,50 L93,50" : attention ? "M53,48 L53,52 M89,46 L89,52" : done ? "M48,51 Q53,45 58,51 M84,51 Q89,45 94,51" : "M53,48 L53,51 M89,48 L89,51";
        CompanionEyes.Data = Geometry.Parse(eyes);
        CompanionMouth.Data = Geometry.Parse(attention && !working ? "M67,61 Q71,57 75,61" : "M63,59 Q67,65 71,59 Q75,65 79,59");
    }
    private void CompanionEntered(object sender, MouseEventArgs e) => ReactCompanion(true);
    private void CompanionLeft(object sender, MouseEventArgs e) => ReactCompanion(false);
    private void ReactCompanion(bool over)
    {
        UpdateCompanion();
        void Animate(Animatable target, DependencyProperty property, double value)
        {
            if (!SystemParameters.ClientAreaAnimation) { target.BeginAnimation(property, null); target.SetValue(property, value); return; }
            target.BeginAnimation(property, new DoubleAnimation(value, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        Animate(CompanionScale, ScaleTransform.ScaleXProperty, over ? 1.04 : 1);
        Animate(CompanionScale, ScaleTransform.ScaleYProperty, over ? 1.04 : 1);
        Animate(CompanionTilt, RotateTransform.AngleProperty, over ? -4 : 0);
        Animate(CompanionLift, TranslateTransform.YProperty, over ? -3 : 0);
    }
    private void DisposeSidebar()
    {
        sidebarDisposed = true; sidebarReadVersion++; sidebarTimer.Stop();
        if (runtime is not null) { runtime.StateChanged -= ShellRuntimeState; runtime.Changed -= ShellRuntimeNotice; runtime.ProgressChanged -= ShellRuntimeProgress; }
        foreach (var panel in operations.Values) panel.SummaryChanged -= UpdateSidebar;
    }
}
