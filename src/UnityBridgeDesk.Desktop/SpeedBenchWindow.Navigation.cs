using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop;

public sealed class ExperimentLabelConverter : IValueConverter
{
    public static string Label(string code) => code switch
    { "F01" => "F01  작은 요청", "F02" => "F02  명령 분할", "F03" => "F03  응답 크기", "F04" => "F04  C# 실행", "S01" => "S01  명령 부하", _ => code };
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Label(value as string ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public partial class SpeedBenchWindow
{
    private bool? sidebarExpanded;
    private bool syncingIndex;
    private readonly ObservableCollection<LiveTrialItem> liveTrials = [];
    private readonly Dictionary<Guid, LiveTrialItem> liveById = [];
    private ICollectionView? liveView;
    private Guid? currentTrial;

    private void InitializeNavigation()
    {
        liveView = CollectionViewSource.GetDefaultView(liveTrials);
        liveView.Filter = MatchesLiveFilter; LiveTrialIndex.ItemsSource = liveView;
        UpdateNavigation(); ApplySidebarLayout(Width);
    }
    private void UpdateNavigation()
    {
        if (SidebarBody is null || Tabs is null) return;
        int phase = Tabs.SelectedIndex;
        SidebarHeading.Text = workspacePage == 1 ? "기록 보관함" : phase switch { 1 => "진행 찾아보기", 2 => "결과 찾아보기", _ => "준비 목차" };
        PreparationIndex.Visibility = workspacePage == 0 && phase == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProgressIndex.Visibility = workspacePage == 0 && phase == 1 ? Visibility.Visible : Visibility.Collapsed;
        ResultIndex.Visibility = workspacePage == 0 && phase == 2 ? Visibility.Visible : Visibility.Collapsed;
        SidebarHeading.Visibility = workspacePage == 0 && SidebarBody.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        DraftShortcut.SetResourceReference(BackgroundProperty, workspacePage == 0 && phase == 0 ? "Tint" : "NavigationSurface");
        RecordsShortcut.SetResourceReference(BackgroundProperty, workspacePage == 1 ? "Tint" : "NavigationSurface");
        SidebarFooter.Visibility = SidebarBody.Visibility == Visibility.Visible && WorkspaceSurface.ActualHeight >= 780 && (workspacePage != 0 || phase == 0) ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ApplySidebarLayout(double width)
    {
        if (SidebarBody is null) return;
        bool expanded = sidebarExpanded ?? width >= 1060;
        SidebarColumn.Width = new GridLength(expanded ? 232 : 52);
        Sidebar.Padding = expanded ? new Thickness(16,8,16,12) : new Thickness(7,8,8,12);
        SidebarBody.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        NavigationTitle.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        SidebarHeading.Visibility = expanded && workspacePage == 0 ? Visibility.Visible : Visibility.Collapsed;
        SidebarFooter.Visibility = expanded && WorkspaceSurface.ActualHeight >= 780 && (workspacePage != 0 || Tabs.SelectedIndex == 0) ? Visibility.Visible : Visibility.Collapsed;
        DraftNavLabel.Visibility = RecordsNavLabel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        SidebarChevron.SetResourceReference(System.Windows.Shapes.Path.DataProperty,
            expanded ? "SidebarCollapseChevron" : "SidebarExpandChevron");
        SidebarToggle.ToolTip = expanded ? "탐색 목록 접기 · Alt+0" : "탐색 목록 펼치기 · Alt+0";
        System.Windows.Automation.AutomationProperties.SetName(SidebarToggle,
            expanded ? "탐색 목록 접기" : "탐색 목록 펼치기");
        NextStep.Visibility = Visibility.Collapsed;
        CompanionHome.Visibility = expanded && (settings.ShowCompanion || settings.ShowFoxCompanion) ? Visibility.Visible : Visibility.Collapsed;
        CompanionHost.Visibility = settings.ShowCompanion ? Visibility.Visible : Visibility.Collapsed;
        FoxHost.Visibility = settings.ShowFoxCompanion ? Visibility.Visible : Visibility.Collapsed;
        asha?.RefreshGeometry(); fox?.RefreshGeometry();
    }
    private async void SidebarToggleClicked(object sender, RoutedEventArgs e)
    {
        sidebarExpanded = SidebarBody.Visibility != Visibility.Visible;
        double width = Content is FrameworkElement { ActualWidth: > 0 } surface ? surface.ActualWidth : Width;
        ApplySidebarLayout(width); ApplyPreparationLayout(width);
        settings = settings with { SidebarExpanded = sidebarExpanded };
        if (loaded) await SaveAppearance();
    }
    private void ResultIndexSizeChanged(object sender, SizeChangedEventArgs e)
    { ResultIndexTools.MaxHeight = Math.Max(60, e.NewSize.Height - 224); }
    private void PreparationLinkSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PreparationLinks.SelectedItem is ListBoxItem { Tag: string name } && FindName(name) is FrameworkElement section)
            section.BringIntoView();
    }
    private void TrialSearchChanged(object sender, TextChangedEventArgs e)
    { if (!updatingFilters && TrialIndex is not null && shownReport is not null) ApplyTrialFilters(); }
    private static bool MatchesTrialQuery(string query, int order, string text) =>
        query.Length == 0 || (int.TryParse(query.TrimStart('#'), out int number)
            ? order == number : text.Contains(query, StringComparison.OrdinalIgnoreCase));
    private void TrialIndexSelected(object sender, SelectionChangedEventArgs e)
    {
        if (syncingIndex || updatingFilters || TrialIndex.SelectedItem is not ReportTrial row) return;
        syncingIndex = true; Trials.SelectedItem = row; syncingIndex = false;
        SelectResultView(2);
    }
    private void SynchronizeTrialIndex(ReportTrial? selected)
    {
        if (syncingIndex || TrialIndex is null) return;
        syncingIndex = true; TrialIndex.SelectedItem = selected; syncingIndex = false;
    }
    private void ResetLiveIndex()
    {
        liveById.Clear(); liveTrials.Clear(); currentTrial = null;
        LiveSearch.Text = ""; LiveFilter.SelectedIndex = 0;
        FollowTrialButton.IsEnabled = false;
        LiveInspection.Text = "실험 파일을 준비하고 있습니다. 시행 계획이 확정되면 좌측에 표시됩니다.";
        LiveScope.Text = "시행 계획 준비 중";
    }
    private LiveTrialItem LiveItem(SpeedTrial trial)
    {
        if (liveById.TryGetValue(trial.Id, out var existing)) return existing;
        var item = new LiveTrialItem(trial); liveById.Add(trial.Id, item); liveTrials.Add(item); return item;
    }
    private void UpdateLiveIndex(SpeedLiveProgress progress)
    {
        if (progress.Plan is { } plan) foreach (var trial in plan.OrderBy(t => t.Order)) LiveItem(trial);
        if (progress.Result is { } result) LiveItem(result.Trial).Complete(result);
        if (progress.Trial is { } active)
        {
            currentTrial = active.Id;
            if (progress.Result is null) LiveItem(active).Running(progress.Stage);
            FollowTrialButton.IsEnabled = true;
        }
        RefreshLiveFilter();
        if (LiveTrialIndex.SelectedItem is LiveTrialItem selected) LiveInspection.Text = selected.Details;
        else if (currentTrial is { } id && LiveFilter.SelectedIndex == 0 && LiveSearch.Text.Length == 0)
            LiveTrialIndex.SelectedItem = liveById[id];
    }
    private bool MatchesLiveFilter(object value)
    {
        if (value is not LiveTrialItem item) return false;
        string query = LiveSearch.Text.Trim().TrimStart('#');
        bool status = LiveFilter.SelectedIndex switch { 1 => item.StatusCode == "running", 2 => item.StatusCode is "failed" or "cleanup-failed", _ => true };
        return status && MatchesTrialQuery(query, item.Order, $"{item.Release} {item.Caption} {item.Status}");
    }
    private void LiveFilterChanged(object sender, RoutedEventArgs e)
    { if (liveView is not null) RefreshLiveFilter(true); }
    private void RefreshLiveFilter(bool force = false)
    {
        if (liveView is null) return;
        var selected = LiveTrialIndex.SelectedItem;
        if (force || LiveFilter.SelectedIndex > 0 || LiveSearch.Text.Length > 0) liveView.Refresh();
        if (selected is not null && liveView.Contains(selected)) LiveTrialIndex.SelectedItem = selected;
        LiveScope.Text = $"{liveView.Cast<object>().Count()} / {liveTrials.Count}개 시행";
        int failed = liveTrials.Count(t => t.StatusCode is "failed" or "cleanup-failed");
        LiveFailuresButton.Content = $"실패·정리 실패 {failed}회"; LiveFailuresButton.IsEnabled = failed > 0;
        if (LiveTrialIndex.SelectedItem is null) LiveInspection.Text = liveTrials.Count == 0 ? "아직 시행 계획이 없습니다." : "목록에서 시행을 선택하세요. 검색 결과가 없으면 검색어와 상태 필터를 확인하세요.";
    }
    private void LiveTrialSelected(object sender, SelectionChangedEventArgs e)
    { if (LiveInspection is not null && LiveTrialIndex.SelectedItem is LiveTrialItem row) LiveInspection.Text = row.Details; }
    private void LiveFailuresClicked(object sender, RoutedEventArgs e)
    { LiveSearch.Text = ""; LiveFilter.SelectedIndex = 2; }
    private void FollowTrialClicked(object sender, RoutedEventArgs e)
    {
        if (currentTrial is not { } id || !liveById.TryGetValue(id, out var item)) return;
        LiveSearch.Text = ""; LiveFilter.SelectedIndex = 0;
        LiveTrialIndex.SelectedItem = item; LiveTrialIndex.ScrollIntoView(item);
    }
    private void FinishLiveIndex(SpeedRun run)
    {
        foreach (var trial in run.Plan) LiveItem(trial);
        foreach (var result in run.Results) LiveItem(result.Trial).Complete(result);
        foreach (var item in liveTrials.Where(t => t.Result is null)) item.End();
        RefreshLiveFilter();
        if (LiveTrialIndex.SelectedItem is LiveTrialItem selected) LiveInspection.Text = selected.Details;
    }
    private sealed class LiveTrialItem(SpeedTrial trial) : INotifyPropertyChanged
    {
        public SpeedTrial Trial { get; } = trial;
        public int Order => Trial.Order;
        public string Release => Trial.Tag;
        public double? WorkMs => Result is { } result && SpeedAnalysis.IsValid(result) ? result.Guest?.WorkMs : null;
        public string Caption => Trial.Experiment + " · " + SpeedReport.Condition(Trial.Experiment, Trial.Variant);
        public SpeedTrialResult? Result { get; private set; }
        public string StatusCode { get; private set; } = "queued";
        public string Status { get; private set; } = "대기";
        public string Details => Result is { } result ? new ReportTrial(Trial, result).Details :
            $"#{Order} · {Release}\n{Caption}\n상태: {Status}\n" + (StatusCode == "running" ? "측정과 정리가 끝난 뒤 성공 여부를 확정합니다." : "아직 완료된 측정 결과가 없습니다.");
        public void Running(SpeedLiveStage stage)
        {
            if (Result is not null) return;
            StatusCode = "running"; Status = stage switch { SpeedLiveStage.Preparation => "준비 중", SpeedLiveStage.Unity => "Unity 준비", SpeedLiveStage.Measurement => "측정 중", _ => "정리 중" }; Changed();
        }
        public void Complete(SpeedTrialResult result) { Result = result; StatusCode = result.Status; Status = SpeedReport.StatusLabel(result.Status); Changed(); }
        public void End() { StatusCode = "not-run"; Status = "미수행"; Changed(); }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed() => PropertyChanged?.Invoke(this, new(null));
    }
}
