using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Desktop.Shell;

namespace UnityBridgeDesk.Desktop.Tools;

public sealed partial class OperationPanel
{
    public event Action? SummaryChanged;
    private RunId? requestedHistoryRun;

    public SidebarSummary SidebarSummary()
    {
        var project = frozen?.Project ?? catalog.Document.Projects.FirstOrDefault(x => x.Project.Id == catalog.Document.SelectedProject)?.Project;
        string name = project?.DisplayName ?? "프로젝트 미선택";
        string versions = string.Join(" ↔ ", frozen?.Releases.Select(x => x.Label) ?? releases.SelectedItems.Cast<ReleaseRow>().Select(x => x.Label));
        if (versions.Length == 0) versions = "버전 미선택";
        if (loading) return new(name, versions, "입력을 불러오는 중…", "저장한 조건을 확인하고 있어요.");
        if (tool != ToolKind.Benchmark)
            return new(name, versions, tool == ToolKind.Installation ? "선택한 프로젝트에 패키지 적용" : "모델 · " + (Value("model") is { Length: > 0 } model ? model : "미지정"), frozen is null ? "구성 확인 후 시작할 수 있어요." : "구성 검토 완료 · 시작 대기", 1);
        try
        {
            var spec = options?.Benchmark ?? CurrentBenchOptions();
            var modes = frozen?.Modes ?? SelectedModes;
            var cases = spec.Cases(modes);
            int releaseCount = frozen?.Releases.Length ?? releases.SelectedItems.Count;
            int count = cases.Length * releaseCount * spec.Repeats;
            string ids = string.Join(" · ", cases.Select(x => x.Id).Distinct());
            return new(name, versions, $"{ids}\n{cases.Length}조건 × {releaseCount}버전 × {spec.Repeats}회",
                count > 1000 ? "1,000개 이하로 조건을 줄여 주세요." : frozen is null ? "구성 확인 후 시작할 수 있어요." : "구성 검토 완료 · 시작 대기", count);
        }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidOperationException)
        { return new(name, versions, "실험·반복 입력 확인 필요", "유효한 조건을 입력하면 시행 수가 보여요."); }
    }

    public void ShowProgress() => tabs.SelectedIndex = 1;
    public async Task ShowHistoryAsync(RunId runId)
    {
        requestedHistoryRun = runId;
        tabs.SelectedIndex = 2;
        if (tool == ToolKind.Benchmark) await RefreshBenchmarkHistory(runId);
        else await RefreshHistory(runId);
    }
}
