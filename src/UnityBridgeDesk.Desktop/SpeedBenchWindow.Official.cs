using System.Windows;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop;

public partial class SpeedBenchWindow
{
    private bool applyingOfficial;
    private int SelectedTargetCount => Selected().Length + (IncludeOfficial.IsChecked == true ? 1 : 0) + (IncludeGo.IsChecked == true ? 1 : 0);
    private GoUnitySelection? ReadGoSelection() => IncludeGo.IsChecked == true ? new(GoVersion.Text.Trim()) : null;
    private OfficialUnitySelection? ReadOfficialSelection() => IncludeOfficial.IsChecked == true
        ? new(string.IsNullOrWhiteSpace(OfficialCliVersion.Text) ? null : OfficialCliVersion.Text.Trim(),
            string.IsNullOrWhiteSpace(OfficialPipelineVersion.Text) ? null : OfficialPipelineVersion.Text.Trim()) : null;
    private void ApplyOfficialSelection()
    {
        applyingOfficial = true;
        try
        {
            IncludeOfficial.IsChecked = settings.OfficialUnity is not null;
            OfficialCliVersion.Text = settings.OfficialUnity?.CliVersion ?? "";
            OfficialPipelineVersion.Text = settings.OfficialUnity?.PipelineVersion ?? "";
            IncludeGo.IsChecked = settings.GoUnity is not null;
            GoVersion.Text = settings.GoUnity?.Version ?? "0.4.1";
        }
        finally { applyingOfficial = false; }
        UpdatePreparation();
    }
    private void OfficialSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (applyingOfficial || OfficialPipelineVersion is null || GoVersion is null || EditorList is null) return;
        if ((IncludeOfficial.IsChecked == true || IncludeGo.IsChecked == true) && EditorList.SelectedItem is LocalCandidate current &&
            current.Version?.StartsWith("6000.", StringComparison.Ordinal) != true &&
            EditorList.Items.OfType<LocalCandidate>().FirstOrDefault(c => c.Version?.StartsWith("6000.", StringComparison.Ordinal) == true) is { } unity6)
        {
            EditorList.SelectedItem = unity6;
            EditorStatus.Text = "CLI 비교에 사용할 Unity " + unity6.Version + "을 선택했습니다.";
        }
        UpdatePreparation();
    }
}
