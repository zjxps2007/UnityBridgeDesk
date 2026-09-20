using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop;

public partial class SpeedBenchWindow
{
    private sealed record ResultPosition(string Experiment, string Condition, string? Release, int Status,
        string Query, Guid? Trial, int View);
    private readonly Dictionary<string, ResultPosition> conditionPositions = [];
    private readonly Dictionary<Guid, ResultPosition> recordPositions = [];
    private readonly Dictionary<string, double> resultOffsets = [];
    private string? displayedConditionKey;
    private string displayedExperiment = "", displayedCondition = "";
    private int resultView;
    private bool changingResultView;

    private string ConditionKey(string experiment, string condition) => $"{shownRun?.Id}/{experiment}/{condition}";
    private string OffsetKey(int view) => view == 3 ? $"{shownRun?.Id}/overview" : $"{displayedConditionKey}/{view}";

    private void RememberResultPosition()
    {
        if (shownRun is null || displayedConditionKey is null) return;
        var position = new ResultPosition(displayedExperiment, displayedCondition,
            ReleaseFilter.SelectedItem as string, StatusFilter.SelectedIndex, TrialSearch.Text,
            (Trials.SelectedItem as ReportTrial)?.Trial.Id, resultView);
        conditionPositions[displayedConditionKey] = position;
        recordPositions[shownRun.Id] = position;
        resultOffsets[OffsetKey(resultView)] = ResultDetailsScroll.VerticalOffset;
    }

    private void OverviewClicked(object sender, RoutedEventArgs e) => SelectResultView(3);
    private void ConditionIndexClicked(object sender, MouseButtonEventArgs e)
    {
        if (resultView == 3 && e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(ChartCondition, source) is ListBoxItem) SelectResultView(0);
    }
    private void ConditionIndexKeyDown(object sender, KeyEventArgs e)
    { if (resultView == 3 && e.Key is Key.Enter or Key.Space) { SelectResultView(0); e.Handled = true; } }
    private void OverviewRowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingFilters || OverviewTable.SelectedItem is not ReportComparison row) return;
        OpenOverviewRow(row);
    }
    private void OpenOverviewRow(ReportComparison row)
    {
        RememberResultPosition();
        updatingFilters = true;
        ChartExperiment.SelectedItem = row.Experiment;
        ChartCondition.ItemsSource = reportCharts.Where(c => c.Experiment == row.Experiment).ToArray();
        ChartCondition.SelectedItem = ChartCondition.Items.Cast<ReportChart>().FirstOrDefault(c => c.Condition == row.Condition);
        OverviewTable.SelectedItem = null;
        updatingFilters = false;
        RefreshCondition(); SelectResultView(0);
    }
    private void BackToConditionClicked(object sender, RoutedEventArgs e) => SelectResultView(0);
    private void TrialPreviousClicked(object sender, RoutedEventArgs e) => MoveTrial(-1);
    private void TrialNextClicked(object sender, RoutedEventArgs e) => MoveTrial(1);
    private void MoveTrial(int direction)
    {
        if (TrialIndex.Items.Count == 0) return;
        TrialIndex.SelectedIndex = Math.Clamp(TrialIndex.SelectedIndex + direction, 0, TrialIndex.Items.Count - 1);
        TrialIndex.ScrollIntoView(TrialIndex.SelectedItem);
        ResultDetailsScroll.ScrollToTop();
    }

    private void UpdateOverview(SpeedReport report)
    {
        OverviewTable.ItemsSource = report.Comparisons
            .OrderBy(p => Array.FindIndex(reportCharts, c => c.Experiment == p.Experiment && c.Condition == p.Condition))
            .ThenBy(p => Array.FindIndex(report.Run.Releases, r => r.Tag == p.Candidate)).ToArray();
        OverviewDescription.Text = $"{reportCharts.Length}개 조건 / {report.Run.Releases.Length}개 대상   기준: {report.Baseline}";
        OverviewCounts.Text = $"통계 포함 {report.Valid}/{report.Trials.Length}회   실패 {report.Failed}회   정리 실패 {report.CleanupFailed}회   미수행 {report.NotRun}회";
        OverviewEmpty.Visibility = report.Comparisons.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        OverviewButton.IsEnabled = true;
    }

    private void UpdateResultHeading()
    {
        if (resultView == 3) ResultHeading.Text = "실험 결과 한눈에 보기";
        else if (ChartCondition.SelectedItem is ReportChart chart)
            ResultHeading.Text = ExperimentLabelConverter.Label(chart.Experiment) + " / " + chart.Condition;
    }

    private void UpdateTargetRows()
    {
        if (OfficialRowNote is null || GoRowNote is null) return;
        bool compatible = EditorList.SelectedItem is UnityBridgeDesk.Infrastructure.Catalog.LocalCandidate editor &&
            editor.Version?.StartsWith("6000.", StringComparison.Ordinal) == true;
        string note = compatible ? "Unity 6 선택됨" : "Unity 6 필요";
        OfficialRowNote.Text = (BaselineList.SelectedItem as string == SpeedBenchWorkflow.OfficialLabel ? "비교 기준 · " : "") + note;
        GoRowNote.Text = (BaselineList.SelectedItem as string == SpeedBenchWorkflow.GoLabel ? "비교 기준 · " : "") + note;
        GoVersionCaption.Text = "비공식 도구 / " + (string.IsNullOrWhiteSpace(GoVersion.Text) ? "버전 입력 필요" : "v" + GoVersion.Text.Trim().TrimStart('v'));
        OfficialRowNote.SetResourceReference(TextBlock.ForegroundProperty, IncludeOfficial.IsChecked == true && !compatible ? "ProblemInk" : "Muted");
        GoRowNote.SetResourceReference(TextBlock.ForegroundProperty, IncludeGo.IsChecked == true && !compatible ? "ProblemInk" : "Muted");
    }

    private FrameworkElement ReleaseRow(ReleaseChoice release, bool baseline)
    {
        var grid = new Grid { Tag = baseline };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock { Text = "UnityBridge " + release.Tag, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var detail = new TextBlock { Text = (release.Prerelease ? "사전 릴리스 / " : "정식 릴리스 / ") + CliDistribution.Description(release.CliUrl), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); text.Children.Add(detail); grid.Children.Add(text);
        var status = new TextBlock { Text = release.CliUrl is null ? "CLI 배포 없음" : baseline ? "비교 기준" : "자동 준비", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        status.SetResourceReference(TextBlock.ForegroundProperty, baseline ? "AccentInk" : "Muted");
        Grid.SetColumn(status, 1); grid.Children.Add(status); return grid;
    }
}
