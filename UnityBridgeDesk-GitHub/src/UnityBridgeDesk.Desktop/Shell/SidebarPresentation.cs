using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Execution;
using UnityBridgeDesk.Infrastructure.Benchmark;
using UnityBridgeDesk.Infrastructure.Storage;

namespace UnityBridgeDesk.Desktop.Shell;

public sealed record SidebarSummary(string Project, string Releases, string Detail, string Hint, int? Trials = null);
public sealed class SidebarRun(RuntimeNotice notice, SidebarSummary summary)
{
    public RunId RunId { get; } = notice.RunId;
    public ToolKind Tool { get; } = notice.Tool;
    public SidebarSummary Summary { get; } = summary;
    public bool Running { get; private set; } = true;
    public string Stage { get; private set; } = notice.Message;
    public string Context { get; private set; } = "";
    public int Completed { get; private set; }
    public int? Total { get; private set; } = summary.Trials;
    private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
    public TimeSpan Elapsed => elapsed.Elapsed;
    public bool Apply(RuntimeNotice update)
    {
        if (!Running || update.RunId != RunId || update.Tool != Tool) return false;
        if (!update.Running) { Running = false; Stage = update.Message; elapsed.Stop(); return true; }
        if (update.Message is "대기 중" or "준비 중" or "실행 중") Stage = update.Message;
        return false;
    }
    public void Apply(RuntimeProgress update)
    {
        if (!Running || update.RunId != RunId) return;
        Total = Math.Max(1, update.Total); Completed = Math.Clamp(update.Completed, 0, Total.Value);
        Stage = update.Stage;
        Context = $"{update.Release} · {update.Experiment} · 반복 {update.Repeat}";
    }
}
public sealed record SidebarRecent(RunId RunId, ToolKind Tool, string Project, DateTimeOffset UpdatedAt, string Status, string Counts)
{
    public static SidebarRecent From(HistoryItem item, IReadOnlyList<TrialResult> trials)
    {
        var status = item.Status ?? throw new ArgumentException("A readable run is required.");
        int success = trials.Count(x => x.Outcome == "Succeeded");
        int failed = trials.Count(x => x.Outcome is "Failed" or "TimedOut");
        int stopped = trials.Count(x => x.Outcome is "Cancelled" or "Interrupted");
        int unknown = trials.Count - success - failed - stopped;
        string counts = trials.Count == 0 ? "시행 기록 없음 · 상세 확인" : $"성공 {success} · 실패 {failed} · 중단 {stopped}" + (unknown > 0 ? $" · 미확인 {unknown}" : "");
        return new(status.RunId, status.Tool, Path.GetFileName(status.Project.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), status.UpdatedAt,
            status.Status is "대기 중" or "준비 중" or "실행 중" ? "미완료 기록" : status.Status, counts);
    }

    public static async Task<IReadOnlyDictionary<ToolKind, SidebarRecent>> ReadLatestAsync(string root)
    {
        var items = await new HistoryStore(root).ReadAsync();
        var result = new Dictionary<ToolKind, SidebarRecent>();
        foreach (var group in items.Where(x => x.Status is not null).GroupBy(x => x.Status!.Tool))
        {
            var item = group.OrderByDescending(x => x.Status!.UpdatedAt).First();
            try { result[group.Key] = From(item, HistoryStore.ReadTrials(item.Directory)); }
            catch (Exception error) when (error is IOException or System.Text.Json.JsonException or ArgumentException or UnauthorizedAccessException)
            {
                result[group.Key] = From(item, []) with { Counts = "시행 기록을 읽지 못했어요 · 상세 확인" };
            }
        }
        return result;
    }
}
