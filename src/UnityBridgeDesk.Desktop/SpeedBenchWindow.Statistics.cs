using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop;

public partial class SpeedBenchWindow
{
    private async void QuickPilotClicked(object sender, RoutedEventArgs e) => await SetPilot(2, 1, 3);
    private async void StatisticalPilotClicked(object sender, RoutedEventArgs e) => await SetPilot(20, 5, 20);
    private async Task SetPilot(int repeats, int warmups, int calls)
    {
        if (operation is not null) return;
        await Work(async ct =>
        {
            Repeats.Text = repeats.ToString(CultureInfo.InvariantCulture);
            Warmups.Text = warmups.ToString(CultureInfo.InvariantCulture);
            Calls.Text = calls.ToString(CultureInfo.InvariantCulture);
            await Save(ct);
            Log($"반복 설정을 변경했습니다: 새 프로젝트 {repeats}회, 사전 실행 {warmups}회, 측정 {calls}회. 입력 확인 후 시작하세요.");
        });
    }
    private void RefreshStatistics(SpeedReport report, ReportChart chart)
    {
        var comparisons = report.Comparisons.Where(c => c.Experiment == chart.Experiment && c.Condition == chart.Condition);
        ReplicationSummary.Text = string.Join("\n", comparisons.Select(c => $"{c.Candidate} · {c.Inference}\n{c.Planning}"));
        var advice = report.Replication.Where(r => r.Experiment == chart.Experiment && r.Condition == chart.Condition);
        ReplicationDetail.Text = string.Join("\n\n", advice.Select(r => $"{r.Release}: {r.VarianceDisplay}. {r.SequenceCheck}.\n호출 수 제안 {r.CallsDisplay} · {r.Note}"));
        var followup = SpeedStatistics.FollowupOptions(report);
        FollowupPlanButton.IsEnabled = operation is null && followup is not null;
        var blocked = report.Comparisons.Where(c => !c.CanPlan).ToArray();
        FollowupStatus.Text = followup is not null ? $"전체 조건·버전을 복원하고 순서 균형을 맞춰 조건·버전당 {followup.Repeats}회로 준비합니다. 사전 실행과 호출 수는 유지하며 벤치는 직접 시작합니다." :
            blocked.Length > 0 ? "전체 비교에 공통 설정을 적용하려면 다음 조건도 확인해야 합니다:\n" +
                string.Join("\n", blocked.Take(3).Select(c => $"{c.Experiment} {c.Condition} · {c.Candidate}: {c.Planning}")) + (blocked.Length > 3 ? $"\n그 외 {blocked.Length - 3}개 비교도 확인이 필요합니다." : "") :
                "비교 자료가 없거나, 버전 순서를 균등하게 맞춘 반복 수가 앱 상한 100회를 넘습니다.";
        string? selected = TrialOrderRelease.SelectedItem as string;
        TrialOrderRelease.ItemsSource = report.Run.Releases.Select(r => r.Tag).ToArray();
        TrialOrderRelease.SelectedItem = selected;
        if (TrialOrderRelease.SelectedIndex < 0) TrialOrderRelease.SelectedIndex = 0;
        RefreshTrialOrder();
    }
    private void TrialOrderReleaseChanged(object sender, SelectionChangedEventArgs e) => RefreshTrialOrder();
    private void RefreshTrialOrder()
    {
        if (shownReport is not { } report || ChartCondition.SelectedItem is not ReportChart chart || TrialOrderSequence is null) return;
        TrialOrderSequence.SetTrials(report.Trials.Where(t => t.Experiment == chart.Experiment && t.Condition == chart.Condition && t.Release == TrialOrderRelease.SelectedItem as string).ToArray());
    }
    private async void FollowupPlanClicked(object sender, RoutedEventArgs e)
    {
        if (operation is not null || shownReport is not { } report || SpeedStatistics.FollowupOptions(report) is not { } options) return;
        await ReuseRun(report.Run, options);
        Log($"예비 측정으로 다음 실험을 준비했습니다. 조건·버전당 {options.Repeats}회, 호출 수는 유지합니다. 새 실험이며 기존 기록은 변경하지 않습니다.");
    }
}
