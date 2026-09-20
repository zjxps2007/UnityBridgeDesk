using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop;

public partial class SpeedBenchWindow
{
    private bool applyingResearch;
    private (int Stage, string Question, string Tolerance, bool? Quiet, string StudyGroup, string SessionNote)? undoResearch;
    private static readonly string[] ResearchStages = ["exploratory", "pilot", "confirmatory", "aa", "sensitivity"];
    private bool IsAa => ResearchStage?.SelectedIndex is 3 or 4;
    private ResearchSettings ReadResearch()
    {
        if (ResearchStage is null || ResearchQuestion is null || ResearchTolerance is null || ResearchQuiet is null) return new();
        bool parsed = double.TryParse(ResearchTolerance.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double tolerance);
        if (ResearchStage.SelectedIndex == 3 && !parsed) throw new ArgumentException("A/A 허용 차이를 숫자로 입력하세요.");
        if (ResearchStage.SelectedIndex != 3 && (!parsed || !double.IsFinite(tolerance) || tolerance is <= 0 or > 20)) tolerance = 1;
        return new(ResearchStages[Math.Max(0, ResearchStage.SelectedIndex)], ResearchQuestion.Text.Trim(), tolerance, ResearchQuiet.IsChecked == true,
            string.IsNullOrWhiteSpace(ResearchStudyGroup.Text) ? null : ResearchStudyGroup.Text.Trim(),
            string.IsNullOrWhiteSpace(ResearchSessionNote.Text) ? null : ResearchSessionNote.Text.Trim());
    }
    private void ApplyResearch(ResearchSettings? value)
    {
        applyingResearch = true;
        try
        {
            value ??= new();
            ResearchStage.SelectedIndex = Math.Max(0, Array.IndexOf(ResearchStages, value.Stage));
            ResearchQuestion.Text = value.Question;
            ResearchTolerance.Text = value.TolerancePercent.ToString(CultureInfo.CurrentCulture);
            ResearchQuiet.IsChecked = value.QuietProgress;
            ResearchStudyGroup.Text = value.StudyGroup ?? ""; ResearchSessionNote.Text = value.SessionNote ?? "";
        }
        finally { applyingResearch = false; }
        UpdateResearchFields();
    }
    private void ResearchChanged(object sender, RoutedEventArgs e)
    {
        if (applyingResearch) return;
        UpdateResearchFields();
        if (ReferenceEquals(sender, ResearchStage) && ResearchStage.SelectedIndex >= 2 && ResearchExpander is not null) ResearchExpander.IsExpanded = true;
        if (IsLoaded) UpdatePreparation();
    }
    private async void CalibrateClicked(object sender, RoutedEventArgs e)
    {
        if (operation is not null) return;
        await Work(async ct =>
        {
            CalibrationResultButton.Visibility = Visibility.Collapsed;
            CalibrationStatus.Text = "진단 시작 · 진행 기록은 취소해도 보관합니다.";
            try
            {
            var progress = new Progress<string>(message => { CalibrationStatus.Text = message; Log(message); });
            string folder = await Task.Run(() => MeasurementCalibration.Run(System.IO.Path.Combine(WorkerDirectory, "UnityBridgeDesk.Worker.exe"),
                System.IO.Path.Combine(dataRoot, "speed", "calibration"), progress, ct), ct);
            Log("계측 진단 저장: " + folder + " · 실제 Unity 검증과 별개입니다.");
            calibrationSummaryPath = System.IO.Path.Combine(folder, MeasurementCalibration.SummaryName);
            CalibrationStatus.Text = await System.IO.File.ReadAllTextAsync(calibrationSummaryPath, CancellationToken.None);
            CalibrationResultButton.Visibility = Visibility.Visible;
            }
            catch (Exception error)
            {
                CalibrationStatus.Text = error is OperationCanceledException ? "진단 준비를 중단했습니다." : "진단을 완료하지 못했습니다: " + error.Message;
                throw;
            }
        });
    }
    private string? calibrationSummaryPath;
    private void OpenCalibrationClicked(object sender, RoutedEventArgs e)
    {
        if (calibrationSummaryPath is not null && System.IO.File.Exists(calibrationSummaryPath))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(calibrationSummaryPath) { UseShellExecute = true });
    }
    private void UpdateResearchFields()
    {
        if (ResearchPurposeHint is null || ResearchAaOptions is null || ResearchInputError is null) return;
        int stage = ResearchStage.SelectedIndex;
        ResearchAaOptions.Visibility = stage == 3 ? Visibility.Visible : Visibility.Collapsed;
        ResearchPurposeHint.Text = stage switch
        {
            1 => "변동을 관찰해 다음 실험의 반복 수를 정합니다. 예비 자료는 본시험과 구분합니다.",
            2 => "질문·주 비교·반복 수를 먼저 정합니다. 개별 95% 구간이며 여러 비교의 동시 우열을 보장하지 않습니다.",
            3 => "Bridge 1개를 A·B로 독립 실행합니다. 차이가 사전 허용 폭 안인지 확인하며 제품 순위를 매기지 않습니다.",
            4 => "Bridge 1개 · F01만 사용합니다. B 요청에 100ms 대기를 넣고 실제 내부 증가와 외부 증가를 비교합니다.",
            _ => "2~8개 대상의 같은 작업을 비교합니다. 먼저 F01과 기본 반복으로 동작을 확인할 수 있습니다."
        };
        ResearchQuestionLabel.Text = stage >= 2 ? "연구 질문·판단 기준 (필수)" : "연구 질문 (선택)";
        ResearchQuestionHint.Text = stage switch
        {
            2 => "주로 비교할 버전·조건과 의미 있는 차이, 반복 수의 이유를 적으세요. 유리한 결과를 보고 조기 종료하지 않습니다.",
            3 => "예: 같은 파일의 실행 위치 차이를 확인한다. 허용 폭은 연구에서 의미 있는 최소 차이로 정한다.",
            4 => "무엇을 확인할지 적으세요. 요청한 100ms와 실제 대기는 다를 수 있어 Unity 내부 시간도 함께 기록합니다.",
            _ => "측정할 작업과 이 결과로 판단하려는 내용을 남길 수 있습니다."
        };
        ResearchInputError.Text = "";
        try
        {
            var value = ReadResearch();
            if (stage == 3 && (!double.IsFinite(value.TolerancePercent) || value.TolerancePercent is <= 0 or > 20))
                ResearchInputError.Text = "A/A 허용 차이는 0% 초과~20%로 입력하세요.";
            else if (stage >= 2 && string.IsNullOrWhiteSpace(value.Question)) ResearchInputError.Text = "연구 질문과 판단 기준을 입력하세요.";
        }
        catch (ArgumentException ex) { ResearchInputError.Text = ex.Message; }
    }
    private FrameworkElement? ResearchInvalidInput()
    {
        UpdateResearchFields();
        if (ResearchInputError.Text.Length > 0) return ResearchStage.SelectedIndex == 3 &&
            (!double.TryParse(ResearchTolerance.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) || !double.IsFinite(value) || value is <= 0 or > 20) ? ResearchTolerance : ResearchQuestion;
        if (IsAa && (Selected().Length != 1 || IncludeOfficial.IsChecked == true || IncludeGo.IsChecked == true)) return ReleaseSection;
        if (ResearchStage.SelectedIndex == 4 && (F01.IsChecked != true || ExperimentFields.Count(c => c.IsChecked == true) != 1)) return ExperimentSection;
        if (ResearchStage.SelectedIndex >= 2 && int.TryParse(Repeats.Text, out int n) && (n < 5 || SelectedTargetCount > 0 && n % SelectedTargetCount != 0)) return Repeats;
        return null;
    }
    private async void NewResearchSessionClicked(object sender, RoutedEventArgs e)
    {
        if (operation is not null || shownReport is not { } report) return;
        var options = report.Run.Options with { Research = (report.Run.Options.Research ?? new()) with { SessionNote = null } };
        await ReuseRun(report.Run, options);
        Log("같은 조건을 새 세션으로 준비했습니다. 메모와 환경을 확인한 뒤 시작하세요. 이전 결과와 자동 합산하지 않습니다.");
    }
    private async void ExportResearchClicked(object sender, RoutedEventArgs e)
    {
        if (operation is not null || shownReport is not { } report) return;
        var dialog = new SaveFileDialog { Filter = "연구 자료 ZIP|*.zip", FileName = $"Desk-research-{report.Run.Id.ToString("N")[..8]}.zip",
            Title = "연구 자료 저장 · 새 파일 이름을 선택하세요" };
        if (dialog.ShowDialog(this) != true) return;
        await Work(async ct =>
        {
            await Task.Run(() => ResearchBundle.Write(dialog.FileName, report, ct, System.IO.Path.Combine(dataRoot, "speed", "local-runs", report.Run.Id.ToString("N"))), ct);
            ReportStatus.Text = "연구 자료 저장 완료 · 원시 기록·사전 계획·독립 검산기 포함";
            Log("연구 자료 ZIP: " + dialog.FileName);
        });
    }
}
