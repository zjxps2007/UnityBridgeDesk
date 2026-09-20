using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop;

public partial class SpeedBenchWindow
{
    private bool compactResults;
    private void SurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ResultToolbar is null) return;
        ApplySidebarLayout(e.NewSize.Width);
        ApplyPreparationLayout(e.NewSize.Width);
        compactResults = e.NewSize.Height < 740; ApplyResultLayout();
        HeaderRow.Height = new(settings.PlainScreen || compactResults ? 76 : 104);
    }
    private void ApplyResultLayout()
    {
        if (OverviewTable is not null && ResultDetailsScroll.ActualHeight > 0)
        {
            double height = Math.Max(140, ResultDetailsScroll.ActualHeight - 185);
            if (Math.Abs(OverviewTable.MaxHeight - height) > .5) OverviewTable.MaxHeight = height;
            double flexible = Math.Max(305, ResultDetailsScroll.ActualWidth - 585);
            SetResultColumnWidth(OverviewTable.Columns[0], Math.Max(185, flexible * .6));
            SetResultColumnWidth(OverviewTable.Columns[1], Math.Max(120, flexible * .4));
        }
        if (ChartCondition.SelectedItem is not null)
        {
            double room = ResultDetailsScroll.ActualHeight - ResultInsight.ActualHeight - 112;
            double height = Math.Clamp(room > 0 ? room : (compactResults ? 180 : 280), SpeedChartView.MinimumPlotHeight, 340);
            if (double.IsNaN(ResultChart.Height) || Math.Abs(ResultChart.Height - height) > .5) ResultChart.Height = height;
        }
    }
    private static void SetResultColumnWidth(DataGridColumn column, double width)
    {
        // DataGrid updates DesiredValue/DisplayValue while arranging. Reassigning an equal
        // pixel Value discards that state and can invalidate the same layout indefinitely.
        if (column.Width.UnitType != DataGridLengthUnitType.Pixel || Math.Abs(column.Width.Value - width) > .5)
            column.Width = new DataGridLength(width);
    }
    private static (int Min, int Max, string Label) NumberRange(string name) => name switch
    {
        "Repeats" => (1, 100, "새 프로젝트 실험 횟수"), "Warmups" => (0, 100, "제외할 사전 실행"),
        "Calls" => (1, 1000, "실험당 측정 명령"), "Timeout" => (1, 3600, "CLI 제한(초)"),
        "PrepareTimeout" => (30, 3600, "Unity 준비(초)"), "StressRequests" => (1, 1000, "명령당 요청 수"),
        "StressConcurrency" => (1, 16, "최대 동시 요청"), _ => (int.MinValue, int.MaxValue, "순서 시드")
    };
    private int ReadNumber(TextBox box)
    {
        var range = NumberRange(box.Name);
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < range.Min || value > range.Max)
            throw new ArgumentException($"{range.Label}: {range.Min}~{range.Max} 범위의 정수를 입력하세요.");
        return value;
    }
    private void UpdateNumberInputs()
    {
        bool repeated = F01.IsChecked == true || F03.IsChecked == true || F04.IsChecked == true;
        foreach (var box in OptionFields.Where(b => b != StressScenarios))
        {
            bool enabled = box == Warmups ? repeated || F02.IsChecked == true : box == Calls ? repeated :
                box == StressRequests || box == StressConcurrency ? S01.IsChecked == true : true;
            ((FrameworkElement)box.Parent).IsEnabled = enabled;
            var range = NumberRange(box.Name);
            box.ToolTip = $"{range.Min}~{range.Max}";
            var error = (TextBlock)FindName(box.Name + "Error");
            try { if (enabled) ReadNumber(box); error.Text = ""; box.SetResourceReference(TextBox.BorderBrushProperty, "Line"); }
            catch (ArgumentException) { error.Text = $"{range.Min}~{range.Max} 정수"; box.BorderBrush = new SolidColorBrush(Color.FromRgb(158, 52, 77)); }
            error.Visibility = error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        if (S01.IsChecked == true && int.TryParse(StressConcurrency.Text, out int concurrency) && int.TryParse(StressRequests.Text, out int requests) && concurrency > requests)
        { StressConcurrencyError.Text = "요청 수 이하여야 합니다."; StressConcurrencyError.Visibility = Visibility.Visible; }
    }
    private void UpdateBaseline()
    {
        if (updatingBaseline || BaselineList is null) return;
        updatingBaseline = true;
        try
        {
            string[] tags = ReleaseList.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => ((ReleaseChoice)b.Tag).Tag).ToArray();
            if (IncludeOfficial.IsChecked == true) tags = [..tags, SpeedBenchWorkflow.OfficialLabel];
            if (IncludeGo.IsChecked == true) tags = [..tags, SpeedBenchWorkflow.GoLabel];
            string? previous = BaselineList.SelectedItem as string ?? settings.BaselineTag;
            if (BaselineList.ItemsSource is not string[] current || !current.SequenceEqual(tags))
            { BaselineList.ItemsSource = tags; BaselineList.SelectedItem = tags.Contains(previous) ? previous : tags.FirstOrDefault(); }
            foreach (var box in ReleaseList.Children.OfType<CheckBox>())
            {
                var choice = (ReleaseChoice)box.Tag; bool baseline = choice.Tag == BaselineList.SelectedItem as string;
                if (box.Content is not FrameworkElement { Tag: bool previousBaseline } || previousBaseline != baseline)
                    box.Content = ReleaseRow(choice, baseline);
            }
        }
        finally { updatingBaseline = false; }
    }
    private void BaselineChanged(object sender, SelectionChangedEventArgs e) { if (!updatingBaseline) UpdatePreparation(); }
    private void HistorySearchChanged(object sender, TextChangedEventArgs e) { if (History is not null) ApplyHistorySearch(); }
    private void SelectShownHistory(SpeedRun run)
    {
        var item = historyItems.FirstOrDefault(i => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(i.Path)) == run.Id.ToString("N"));
        if (item is null) return;
        syncingHistory = true;
        try
        {
            // A completed run must not keep an older run's label or remain hidden by its search filter.
            if (!History.Items.Contains(item)) HistorySearch.Text = "";
            History.SelectedItem = item;
        }
        finally { syncingHistory = false; }
    }
    private void ApplyHistorySearch()
    {
        string query = HistorySearch.Text.Trim();
        string? selected = (History.SelectedItem as HistoryItem)?.Path;
        var items = historyItems.Where(i => i.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        History.ItemsSource = items;
        History.SelectedItem = items.FirstOrDefault(i => i.Path == selected) ?? items.FirstOrDefault(i => shownRun is not null &&
            System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(i.Path)) == shownRun.Id.ToString("N")) ?? items.FirstOrDefault();
        if (HistoryEmpty is not null)
        {
            HistoryEmpty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryEmptyText.Text = query.Length > 0 ? "검색에 맞는 기록이 없습니다. 검색어를 지우면 전체 기록이 보입니다." : "첫 벤치를 마치면 이곳에서 결과를 다시 열 수 있습니다.";
            HistoryCount.Text = $"{items.Length} / {historyItems.Length}개 기록 · 최근 200개까지 표시 · 선택 후 Enter로 열기";
            OpenHistoryButton.IsEnabled = History.SelectedItem is not null;
        }
        if (items.Length == 0) ReportStatus.Text = query.Length > 0 ? "검색에 맞는 기록이 없습니다. 검색어를 지우면 전체 기록을 볼 수 있습니다." : "저장된 실행 기록이 없습니다.";
    }
    private async void ReuseClicked(object sender, RoutedEventArgs e)
    {
        if (shownRun is not { } run || operation is not null) return;
        await ReuseRun(run, run.Options);
    }
    private async Task ReuseRun(SpeedRun run, SpeedOptions options)
    {
        await Work(async ct =>
        {
            var official = run.Releases.FirstOrDefault(r => r.OfficialUnity is not null)?.OfficialUnity;
            var go = run.Releases.FirstOrDefault(r => r.GoUnity is not null)?.GoUnity;
            settings = settings with { Options = options, SelectedTags = run.Releases.Where(r => r.OfficialUnity is null && r.GoUnity is null).Select(r => r.Tag).ToArray(),
                BaselineTag = run.Releases.FirstOrDefault()?.GoUnity is not null ? SpeedBenchWorkflow.GoLabel : run.Releases.FirstOrDefault()?.OfficialUnity is not null ? SpeedBenchWorkflow.OfficialLabel : run.Releases.FirstOrDefault()?.Tag,
                EditorPath = run.Local?.EditorPath ?? settings.EditorPath,
                OfficialUnity = official is null ? null : new(official.CliVersion, official.PipelineVersion), GoUnity = go is null ? null : new(go.CliVersion) };
            ApplyOfficialSelection(); ApplyOptions(options); undoInputs = null; undoExperiments = null; ResetButton.Content = "벤치 설정 초기화";
            foreach (var box in ReleaseList.Children.OfType<CheckBox>()) box.IsChecked = false;
            DrawReleases(await repository.CachedList(ct));
            BaselineList.SelectedItem = settings.BaselineTag;
            await FindEditors(ct); await Save(ct, true);
            Tabs.SelectedIndex = 0; Log("기록의 실험 조건을 준비 탭에 불러왔습니다. 입력을 확인한 뒤 벤치 시작을 누르세요.");
            string[] missing = run.Releases.Where(r => r.OfficialUnity is null && r.GoUnity is null && !Selected().Any(s => s.Tag == r.Tag)).Select(r => r.Tag).ToArray();
            if (missing.Length > 0) ReleaseStatus.Text = "목록에 없는 버전: " + string.Join(", ", missing) + " · 릴리스 새로 확인이 필요합니다.";
        });
    }
    private void ChartExperimentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingFilters || ChartCondition is null) return;
        updatingFilters = true;
        ChartCondition.ItemsSource = reportCharts.Where(c => c.Experiment == ChartExperiment.SelectedItem as string).ToArray();
        ChartCondition.SelectedIndex = 0; updatingFilters = false; RefreshCondition();
    }
    private void RefreshCondition()
    {
        if (shownReport is not { } report || ChartCondition.SelectedItem is not ReportChart chart) return;
        string key = ConditionKey(chart.Experiment, chart.Condition);
        bool changed = key != displayedConditionKey;
        if (changed) RememberResultPosition();
        conditionPositions.TryGetValue(key, out var position);
        bool stability = ChartMetric.SelectedIndex == 1;
        updatingFilters = true;
        if (changed)
        {
            ReleaseFilter.SelectedItem = position?.Release ?? "전체 버전";
            if (ReleaseFilter.SelectedIndex < 0) ReleaseFilter.SelectedIndex = 0;
            StatusFilter.SelectedIndex = position?.Status ?? 0;
            TrialSearch.Text = position?.Query ?? "";
        }
        var summaryRows = report.Summaries.Where(s => s.Experiment == chart.Experiment && s.Condition == chart.Condition)
            .OrderBy(s => Array.FindIndex(report.Run.Releases, r => r.Tag == s.Release)).ToArray();
        var pairRows = report.Comparisons.Where(c => c.Experiment == chart.Experiment && c.Condition == chart.Condition).ToArray();
        var stabilityRows = report.Stability.Where(s => s.Experiment == chart.Experiment && s.Condition == chart.Condition)
            .OrderBy(s => Array.FindIndex(report.Run.Releases, r => r.Tag == s.Release)).ToArray();
        if (Summary.ItemsSource is not ReportSummary[] previousSummary || !previousSummary.SequenceEqual(summaryRows)) Summary.ItemsSource = summaryRows;
        if (PairSummary.ItemsSource is not ReportComparison[] previousPairs || !previousPairs.SequenceEqual(pairRows)) PairSummary.ItemsSource = pairRows;
        if (StabilityTable.ItemsSource is not StabilitySummary[] previousStability || !previousStability.SequenceEqual(stabilityRows)) StabilityTable.ItemsSource = stabilityRows;
        trialCondition = chart.Condition; ExperimentFilter.SelectedItem = chart.Experiment;
        displayedConditionKey = key; displayedExperiment = chart.Experiment; displayedCondition = chart.Condition;
        updatingFilters = false;
        ResultChart.Show(chart, stability);
        ApplyResultLayout();
        ChartTitle.Text = stability ? "유효 완료율 (%)" : SpeedReport.UnitLabel(chart.Experiment);
        SelectedConditionCaption.Text = chart.Experiment + " / " + chart.Condition;
        ChartBasis.Text = stability ? "유효 완료 / 평가 대상 시행. 환경·호환성 오류와 중단·미수행은 제외합니다." : "그래프: 대상별 전체 유효 시행 평균 / 위 비교와 포함 범위가 다를 수 있습니다.";
        ConditionProtocol.Text = string.Join("\n", report.ProtocolDescription(chart.Experiment, chart.Condition).Split('\n').Take(2));
        ConditionProtocol.ToolTip = report.ProtocolDescription(chart.Experiment, chart.Condition);
        ConditionMemo.Text = report.ConditionMemo(chart.Experiment, chart.Condition);
        RefreshStatistics(report, chart);
        UpdateResultInsight(report, chart, summaryRows, pairRows, stabilityRows, stability);
        InspectFailuresButton.Visibility = chart.Series.Any(s => s.Valid < s.Planned) ? Visibility.Visible : Visibility.Collapsed;
        ChartHint.Text = stability ? "유효 / 평가 대상 시행. 환경 준비·호환성 오류, 사용자 중단·미수행 제외. 정리 실패 포함. 100%가 안정성을 보장하지 않습니다." :
            "막대 선택 → 해당 버전 시행 상세 · 실패는 0ms가 아닙니다. 조건별 축 범위가 다릅니다. 최소·최대·변동 폭은 시행 상세에서 확인하세요.";
        ApplyTrialFilters();
        if (changed)
        {
            if (position?.Trial is { } id && Trials.Items.Cast<ReportTrial>().FirstOrDefault(t => t.Trial.Id == id) is { } trial)
                Trials.SelectedItem = trial;
            resultView = position is { View: < 3 } ? position.View : 0;
            SelectResultView(resultView);
        }
        UpdateResultHeading();
    }
    private void ResultViewChanged(object sender, RoutedEventArgs e)
    {
        if (!changingResultView && sender is RadioButton { Tag: string value } && TrialView is not null) ShowResultView(int.Parse(value, CultureInfo.InvariantCulture));
    }
    private void ShowResultView(int index)
    {
        if (OverviewView is null) return;
        if (resultView != index) resultOffsets[OffsetKey(resultView)] = ResultDetailsScroll.VerticalOffset;
        resultView = index;
        TimeView.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PairView.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        TrialView.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        OverviewView.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        OverviewButton.SetResourceReference(Control.BackgroundProperty, index == 3 ? "Tint" : "NavigationSurface");
        ResultNavigation.Visibility = index == 3 ? Visibility.Collapsed : Visibility.Visible;
        ExportCurrentChart.IsEnabled = index != 3 && ChartCondition.SelectedItem is not null;
        changingResultView = true;
        foreach (RadioButton button in ResultNavigation.Children) button.IsChecked = button.Tag as string == index.ToString(CultureInfo.InvariantCulture);
        changingResultView = false;
        UpdateResultHeading();
        ResultDetailsScroll.UpdateLayout();
        ResultDetailsScroll.ScrollToVerticalOffset(resultOffsets.GetValueOrDefault(OffsetKey(index)));
    }
    private void SelectResultView(int index)
    {
        ShowResultView(index);
    }
    private void ChartReleaseSelected(string release)
    {
        updatingFilters = true; TrialSearch.Text = ""; ReleaseFilter.SelectedItem = release; StatusFilter.SelectedIndex = 0; updatingFilters = false;
        ApplyTrialFilters(); SelectResultView(2);
    }
    private void InspectFailuresClicked(object sender, RoutedEventArgs e)
    {
        updatingFilters = true; TrialSearch.Text = ""; ReleaseFilter.SelectedIndex = 0; StatusFilter.SelectedIndex = 5; updatingFilters = false;
        ApplyTrialFilters(); SelectResultView(2);
    }
    private void ExportChartClicked(object sender, RoutedEventArgs e)
    {
        if (shownReport is not { } report || ChartCondition.SelectedItem is not ReportChart chart) return;
        var dialog = new SaveFileDialog { Filter = "SVG 그래프 이미지|*.svg", FileName = $"UnityBridge-{chart.Experiment}-{(ChartMetric.SelectedIndex == 1 ? "완료율" : "시간")}.svg" };
        if (dialog.ShowDialog(this) != true) return;
        try { SpeedCharts.WriteSvg(dialog.FileName, report, [chart], ChartMetric.SelectedIndex == 1); ReportStatus.Text = "현재 조건의 그래프를 저장했습니다: " + dialog.FileName; }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { ReportStatus.Text = "그래프를 저장하지 못했습니다: " + error.Message; }
    }
    private void NestedScrollWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject element) return;
        ScrollViewer? scroll = element as ScrollViewer ?? FindScroll(element);
        // WPF consumes wheel events even in horizontal-only viewers and fully visible text/tables.
        // Let a nested list scroll normally, then hand the wheel to the page at either boundary.
        if (scroll is null || (e.Delta > 0 ? scroll.VerticalOffset > 0 : scroll.VerticalOffset < scroll.ScrollableHeight)) return;
        DependencyObject? parent = VisualTreeHelper.GetParent(element);
        while (parent is not null && parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
        if (parent is ScrollViewer outer) { outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta / 3d); e.Handled = true; }
    }
    private static ScrollViewer? FindScroll(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        { var child = VisualTreeHelper.GetChild(element, i); if (child is ScrollViewer scroll) return scroll; if (FindScroll(child) is { } nested) return nested; }
        return null;
    }
}
