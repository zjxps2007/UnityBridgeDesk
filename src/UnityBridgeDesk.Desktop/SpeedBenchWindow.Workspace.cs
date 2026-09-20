using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UnityBridgeDesk.Desktop.Shell;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop;

public partial class SpeedBenchWindow
{
    private void ApplyWorkspaceTheme(DeskPalette palette)
    {
        string[] colors = palette switch
        {
            DeskPalette.Mint => ["#FBFDFB", "#EFF6F2"],
            DeskPalette.Rose => ["#FEFBFC", "#F8EFF3"],
            _ => ["#FCFBFE", "#F4F1F8"]
        };
        foreach (string key in new[] { "Accent", "AccentInk", "Tint", "Line" }) Resources.Remove(key);
        if (settings.PlainScreen)
        {
            colors = ["#FCFCFB", "#F3F3F2"];
            string[] keys = ["Accent", "AccentInk", "Tint", "Line"];
            string[] neutral = ["#DDDADF", "#514B5D", "#EFEEF1", "#E2E1E3"];
            for (int i = 0; i < keys.Length; i++) Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(neutral[i]));
        }
        else Resources["Line"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette switch { DeskPalette.Rose => "#E9E0E4", DeskPalette.Mint => "#DFE8E3", _ => "#E5E0EB" }));
        Resources["Workspace"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[0]));
        Resources["NavigationSurface"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[1]));
    }
    private void ApplyPreparationLayout(double width)
    {
        if (PreparationOptions is null) return;
        bool columns = width - SidebarColumn.Width.Value >= 1040;
        PreparationSecondColumn.Width = new GridLength(columns ? 1 : 0, columns ? GridUnitType.Star : GridUnitType.Pixel);
        Grid.SetColumn(PreparationOptions, columns ? 1 : 0);
        Grid.SetRow(PreparationOptions, columns ? 0 : 1);
        PreparationOptions.Margin = new Thickness(columns ? 36 : 0, 0, 0, 0);
    }
    private void PreparationLinkClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(PreparationLinks, source) is ListBoxItem item)
            NavigatePreparation(item);
    }
    private void PreparationLinkKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || PreparationLinks.SelectedItem is not ListBoxItem item) return;
        NavigatePreparation(item); e.Handled = true;
    }
    private void NavigatePreparation(ListBoxItem item)
    { if (item.Tag is string name && FindName(name) is FrameworkElement section) section.BringIntoView(); }

    private async void CompanionToggleClicked(object sender, RoutedEventArgs e)
    {
        settings = IsFoxControl(sender) ? settings with { ShowFoxCompanion = !settings.ShowFoxCompanion } : settings with { ShowCompanion = !settings.ShowCompanion }; UpdateCompanion();
        try { await Save(CancellationToken.None, appearanceOnly: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Log("캐릭터 표시 설정을 저장하지 못했습니다: " + error.Message); }
    }
    private void UpdateCompanion()
    {
        CompanionHome.Visibility = (settings.ShowCompanion || settings.ShowFoxCompanion) && SidebarBody.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateMotion();
    }
    private async void CompanionRoamClicked(object sender, RoutedEventArgs e)
    {
        settings = IsFoxControl(sender) ? settings with { FoxRoaming = !settings.FoxRoaming } : settings with { CompanionRoaming = !settings.CompanionRoaming }; UpdateMotion();
        await SaveCompanionAppearance();
    }
    private async void CompanionActivityClicked(object sender, RoutedEventArgs e)
    {
        settings = settings with { CompanionActivity = (Math.Clamp(settings.CompanionActivity, 0, 2) + 1) % 3 }; UpdateMotion();
        await SaveCompanionAppearance();
    }
    private static bool IsFoxControl(object sender) => sender is FrameworkElement { Tag: "fox" };
    private void CompanionPlayClicked(object sender, RoutedEventArgs e) => (IsFoxControl(sender) ? FoxMascot : Mascot).React();
    private void CompanionHomeClicked(object sender, RoutedEventArgs e) => (IsFoxControl(sender) ? fox : asha)?.ReturnHome();
    private async Task SaveCompanionAppearance()
    {
        try { await Save(CancellationToken.None, appearanceOnly: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Log("캐릭터 설정을 저장하지 못했습니다: " + error.Message); }
    }
    private void ResultViewportChanged(object sender, SizeChangedEventArgs e) => ApplyResultLayout();

    private void UpdateResultInsight(SpeedReport report, ReportChart chart, ReportSummary[] summaries,
        ReportComparison[] comparisons, StabilitySummary[] stabilityRows, bool stability)
    {
        int planned = summaries.Sum(s => s.Planned), valid = summaries.Sum(s => s.Valid);
        ConditionCoverage.Text = $"선택 조건 · 통계 포함 {valid}/{planned}회 · 제외 {planned - valid}회";
        InspectFailuresButton.Content = $"제외 {planned - valid}회 확인";
        InsightLabel.Text = stability ? "선택 조건의 유효 완료" : "기준 " + report.Baseline;
        if (report.Run.Options.Research?.Stage is "aa" or "sensitivity")
        {
            InsightLabel.Text = "동일 릴리스 측정기 검증";
            InsightValue.Text = report.Run.Options.Research?.Stage == "sensitivity" ? "추가 지연의 전달 확인" : "A/A 허용 차이 확인";
            InsightDetail.Text = string.Join("\n", comparisons.Select(c => c.Inference));
            InsightDetail.ToolTip = "같은 바이너리를 두 대상으로 실행한 결과입니다. 제품 간 속도 우열이나 벤치마크 인증을 뜻하지 않습니다.";
            return;
        }
        if (stability)
        {
            // Keep evaluation denominators consistent with the chart; cancellation and environment errors are excluded.
            int evaluated = chart.Series.Sum(s => s.Finished);
            InsightValue.Text = $"{valid} / {evaluated}회 유효 완료";
            InsightDetail.Text = "환경 준비·호환성 오류와 중단·미수행은 평가 분모에서 제외합니다. 정리 실패는 포함합니다.";
            return;
        }
        if (comparisons.Length == 1 && comparisons[0] is { BaselineMs: { } a, CandidateMs: { } b, Valid: > 0 } pair)
        {
            double difference = a - b;
            InsightLabel.Text = pair.Candidate + " / 기준 " + report.Baseline + " 대비";
            InsightValue.Text = Math.Abs(difference) < .005 ? "표시 정밀도 내 평균 시간 동일" :
                $"평균 {SpeedReport.Time(Math.Abs(difference))} ms {(difference > 0 ? "짧음" : "길음")}";
            InsightDetail.Text = $"기준 {SpeedReport.Time(a)} / 대상 {SpeedReport.Time(b)} {pair.Unit}   양쪽 유효 {pair.Valid}/{pair.Planned}쌍" +
                (pair.ReductionPercent is { } percent ? $" · 완료 시간 {Math.Abs(percent).ToString("F1", CultureInfo.InvariantCulture)}% {(percent >= 0 ? "감소" : "증가")}" : "") +
                (pair.Valid == 1 ? "\n1쌍 측정: 반복 변동 확인 전" : "") +
                $"\n감소율 95% 구간 {pair.ConfidenceRange} · {pair.Inference}";
        }
        else if (comparisons.Length > 1)
        {
            InsightValue.Text = $"{summaries.Length}개 대상의 시간 비교";
            InsightDetail.Text = $"기준 {report.Baseline} · 비교 가능한 후보 {comparisons.Count(p => p.Valid > 0)}/{comparisons.Length}개. 후보별 평균 차이는 ‘기준 버전 대비’에서 확인하세요.";
        }
        else
        {
            InsightValue.Text = "평균 차이를 비교할 수 없습니다";
            InsightDetail.Text = "같은 조건·블록에서 양쪽 모두 유효한 시행이 필요합니다. 제외된 시행을 확인하세요.";
        }
        InsightDetail.ToolTip = "이번 실행에서 관찰한 값입니다. 아래 그래프는 각 대상의 전체 유효 시행 평균이므로 공동 유효 블록 비교에 쓰인 평균과 다를 수 있습니다.";
    }
}
