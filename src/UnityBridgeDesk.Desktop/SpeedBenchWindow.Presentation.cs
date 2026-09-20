using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop;

public partial class SpeedBenchWindow
{
    private readonly Stopwatch sessionElapsed = new();
    private bool benchmarkActive;
    private SpeedRun? lastFinishedRun;

    private void PreparationChanged(object sender, RoutedEventArgs e) => UpdatePreparation();

    private void UpdatePreparation()
    {
        if (PlanSummary is null || ReleaseList is null || StressScenarios is null || OfficialPipelineVersion is null || GoVersion is null || applyingOfficial || applyingResearch || ResearchTolerance is null) return;
        UpdateResearchFields(); UpdateBaseline(); UpdateNumberInputs(); UpdateTargetRows(); StartButton.IsEnabled = false;
        int releases = SelectedTargetCount;
        var official = ReadOfficialSelection(); var go = ReadGoSelection();
        OfficialSection.Visibility = official is null ? Visibility.Collapsed : Visibility.Visible;
        GoSection.Visibility = go is null ? Visibility.Collapsed : Visibility.Visible;
        ReleaseHint.Text = IsAa ? "UnityBridge 1개만 선택하세요. 같은 바이너리를 두 대상에서 독립 실행합니다." : official is null && go is null ? "UnityBridge 2~8개를 선택하세요. 필요한 파일은 시작할 때 자동으로 준비합니다." :
            "전체 비교 대상 2~8개를 선택하세요. 공식·Go 도구는 임시 준비하고 벤치 종료 후 삭제합니다.";
        OfficialStatus.Text = official is null ? "" : $"Unity CLI {official.CliVersion ?? "자동"} · Pipeline {official.PipelineVersion ?? "자동"} · Unity 6 사용";
        try
        {
            var options = ReadOptions();
            int conditions = SpeedProtocol.Cases(options).Length;
            int trials = releases * conditions * options.Repeats;
            PlanSummary.Text = $"{releases}개 {(IsAa ? "대상 (동일 릴리스 A/B)" : "버전")} · {conditions}개 조건 · {trials}회 시행";
            MeasurementPlan.Text = $"조건·버전마다 새 프로젝트 {options.Repeats}회 실험. " +
                (options.Selected.Any(x => x is "F01" or "F03" or "F04") ? $"반복 평균: 실험마다 사전 실행 {options.Warmups}회 제외, 명령 {options.Calls}회 측정.\n" : "\n") +
                (options.Selected.Any(x => x is "F01" or "F04") ? "첫 명령: 별도 새 프로젝트에서 사전 실행 없이 1회 측정. " : "") +
                "Unity 시작·컴파일 시간은 명령 시간에서 제외합니다." +
                (options.Repeats < SpeedStatistics.MinimumTrials ? "\n5회 미만은 빠른 확인용입니다. 신뢰구간과 반복 수 추정은 제공하지 않습니다." : "");
            SpeedBenchWorkflow.ValidateSelection(Selected().Length, official is not null, go is not null, IsAa);
            if (options.Research?.Stage is "confirmatory" or "aa" or "sensitivity" && options.Repeats % releases != 0) throw new ArgumentException("실험 횟수는 대상 수의 배수로 설정하세요."); official?.Validate(); go?.Validate();
            if (EditorList.SelectedItem is not LocalCandidate editor) PlanHint.Text = "설치·활성화된 Unity를 선택하세요.";
            else if ((official is not null || go is not null) && editor.Version?.StartsWith("6000.", StringComparison.Ordinal) != true)
                PlanHint.Text = "공식 Unity·Go CLI 비교에는 Unity 6가 필요합니다. Unity 환경에서 선택해 주세요.";
            else
            {
                PlanHint.Text = $"실행 준비 완료 · 기준 {BaselineList.SelectedItem} · 파일 준비·정리는 자동입니다.";
                StartButton.IsEnabled = operation is null;
            }
        }
        catch (Exception e) when (e is ArgumentException or System.Text.Json.JsonException)
        { PlanSummary.Text = "실험 조건 확인 필요"; PlanHint.Text = e.Message; MeasurementPlan.Text = "선택한 작업에 필요한 값만 입력하세요."; }
        if (ResolvePreparationButton is not null) ResolvePreparationButton.Visibility = !StartButton.IsEnabled && operation is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateNextStep();
    }

    private void WorkflowChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Tabs)) { UpdateNextStep(); UpdateNavigation(); RevealWorkflow(); }
    }

    private void UpdateNextStep()
    {
        if (NextStep is null || Tabs is null) return;
        NextStep.Text = benchmarkActive ? "다른 탭을 봐도 실험은 계속됩니다." : Tabs.SelectedIndex switch
        {
            1 when lastFinishedRun is not null => "결과 확인에서 비교와 시행 내역을 읽으세요.",
            1 => "준비 탭에서 실험을 시작하세요.",
            2 when shownReport is not null => "조건을 선택해 비교하고, 필요한 자료를 내보내세요.",
            2 => "첫 실험을 마치면 결과가 이곳에 남아요.",
            _ => PlanHint?.Text ?? "Unity와 버전을 선택하세요."
        };
    }

    private void BeginRun(SpeedOptions options)
    {
        ShowWorkspacePage(0); CurrentRunShortcut.IsEnabled = true;
        benchmarkActive = true; lastFinishedRun = null; sessionElapsed.Restart(); UpdateElapsed();
        UpdateMotion();
        ResetLiveIndex();
        ViewResultButton.Visibility = Visibility.Collapsed;
        Journal.Clear(); Activity.Value = 0; Activity.Maximum = SpeedProtocol.Cases(options).Length * SelectedTargetCount * options.Repeats;
        Activity.IsIndeterminate = false; ProgressCount.Text = $"0 / {Activity.Maximum:0}";
        ProgressTitle.Text = "실험 환경을 준비하고 있어요";
        ProgressDetail.Text = "선택한 릴리스 파일과 Unity를 확인합니다. 이후 매 시행을 새 프로젝트에서 시작합니다.";
        SideActivity.Text = "환경 준비"; SideProgress.Text = $"0 / {Activity.Maximum:0}회 종료";
        LatestNotice.Text = "실험을 시작했습니다."; SetStage(SpeedLiveStage.Preparation);
        Tabs.SelectedIndex = 1; UpdateNextStep();
    }

    private void ShowProgress(SpeedLiveProgress progress)
    {
        // Delayed UI messages from a finished run must never revive its running state.
        if (!benchmarkActive) return;
        if (settings.Options?.Research?.QuietProgress == true && progress.Stage is SpeedLiveStage.Unity or SpeedLiveStage.Measurement) return;
        UpdateLiveIndex(progress);
        Activity.Maximum = Math.Max(1, progress.Total); Activity.Value = progress.Completed;
        ProgressCount.Text = $"{progress.Completed} / {progress.Total}";
        SideProgress.Text = $"{progress.Completed} / {progress.Total}회 종료";
        if (progress.Stage == SpeedLiveStage.Finished) return;
        string stage = progress.Stage switch
        {
            SpeedLiveStage.Preparation => "새 실험 공간 준비",
            SpeedLiveStage.Unity => "Unity 기동·컴파일 대기",
            SpeedLiveStage.Measurement => "명령 완료 시간 측정",
            _ => "결과 저장·실험 공간 정리"
        };
        SideActivity.Text = stage; ProgressTitle.Text = stage;
        if (progress.Trial is { } trial)
            ProgressDetail.Text = $"시행 {trial.Order} · {trial.Tag} · {trial.Experiment} · {SpeedReport.Condition(trial.Experiment, trial.Variant)}";
        SetStage(progress.Stage);
    }

    private void SetStage(SpeedLiveStage stage)
    {
        Border[] stages = [StagePrepare, StageUnity, StageMeasure, StageCleanup];
        for (int i = 0; i < stages.Length; i++)
        {
            stages[i].SetResourceReference(Border.BorderBrushProperty, i == (int)stage ? "AccentInk" : "Line");
            if (stages[i].Child is TextBlock label)
            {
                label.SetResourceReference(TextBlock.ForegroundProperty, i == (int)stage ? "AccentInk" : "Muted");
                label.FontWeight = i == (int)stage ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
    }

    private void CompleteRun(SpeedRun run)
    {
        benchmarkActive = false; lastFinishedRun = run; sessionElapsed.Stop(); UpdateElapsed();
        UpdateMotion();
        FinishLiveIndex(run);
        var report = new SpeedReport(run);
        if (run.OfficialCleanup is not null || run.GoCleanup is not null) ReleaseStatus.Text = report.ToolCleanupNote +
            (run.OfficialCleanup?.Status is null or "deleted" && run.GoCleanup?.Status is null or "deleted" ? " · 다음 시작 시 새로 준비합니다." : "");
        ProgressTitle.Text = report.State;
        ProgressDetail.Text = $"{run.Results.Length}/{run.Plan.Length}회 종료 · 유효 {report.Valid} · 실패 {report.Failed} · 정리 실패 {report.CleanupFailed}";
        SideActivity.Text = "현재 실행 중인 벤치 없음"; SideProgress.Text = $"최근 실행: {report.State}";
        LatestNotice.Text = $"{DateTime.Now:HH:mm} · {report.State}\n결과와 정리 상태를 확인할 수 있습니다.";
        Activity.Value = run.Results.Length; ProgressCount.Text = $"{run.Results.Length} / {run.Plan.Length}";
        ViewResultButton.Visibility = Visibility.Visible; SetStage(SpeedLiveStage.Finished); UpdateNextStep();
    }

    private void EndInterruptedRun(string state, string detail)
    {
        LatestNotice.Text = detail;
        if (!benchmarkActive) return;
        benchmarkActive = false; sessionElapsed.Stop(); UpdateElapsed();
        UpdateMotion();
        foreach (var item in liveTrials.Where(t => t.Result is null)) item.End();
        RefreshLiveFilter();
        if (LiveTrialIndex.SelectedItem is LiveTrialItem selected) LiveInspection.Text = selected.Details;
        SideActivity.Text = state; ProgressTitle.Text = state; ProgressDetail.Text = detail;
        SetStage(SpeedLiveStage.Finished); UpdateNextStep();
    }

    private void UpdateElapsed()
    {
        if (Elapsed is null) return;
        var elapsed = sessionElapsed.Elapsed;
        Elapsed.Text = elapsed.TotalHours >= 1 ? $"경과 {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}" :
            $"경과 {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
    }

    private void ViewResultClicked(object sender, RoutedEventArgs e)
    {
        if (lastFinishedRun is not null) ShowRun(lastFinishedRun);
        ShowWorkspacePage(0); Tabs.SelectedIndex = 2;
    }

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (settingsOpen)
        {
            if (e.Key == Key.Escape)
            {
                if (AppearanceActivity.IsDropDownOpen) AppearanceActivity.IsDropDownOpen = false;
                else if (AppearanceFoxActivity.IsDropDownOpen) AppearanceFoxActivity.IsDropDownOpen = false;
                else CloseSettings();
                e.Handled = true;
            }
            // Keep work-page shortcuts from navigating behind the modal.
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.N or Key.H ||
                Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is >= Key.D0 and <= Key.D3) e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.N or Key.H)
        { if (e.Key == Key.N) DraftClicked(sender, e); else RecordsClicked(sender, e); e.Handled = true; return; }
        if (e.Key == Key.Escape && workspacePage != 0) { WorkspaceBackClicked(sender, e); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.D0)
        { SidebarToggleClicked(sender, e); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is >= Key.D1 and <= Key.D3)
        { ShowWorkspacePage(0); Tabs.SelectedIndex = e.SystemKey - Key.D1; e.Handled = true; }
    }
}
