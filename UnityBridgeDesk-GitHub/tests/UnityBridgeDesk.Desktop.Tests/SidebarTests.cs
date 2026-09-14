using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Desktop.Shell;
using UnityBridgeDesk.Infrastructure.Execution;
using UnityBridgeDesk.Infrastructure.Storage;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Desktop.Tests;

[TestClass]
public sealed class SidebarTests
{
    [TestMethod]
    public void ProgressKeepsRunSnapshotAndIgnoresOtherOrFinishedRuns()
    {
        var id = RunId.New();
        var summary = new SidebarSummary("실행 프로젝트", "0.2.0 ↔ 0.2.1", "F01", "검토 완료", 8);
        var run = new SidebarRun(new(ToolKind.Benchmark, id, "대기 중", true), summary);
        run.Apply(new RuntimeProgress(RunId.New(), 8, 8, "다른 버전", "F02", "", 1, "완료"));
        Assert.AreEqual(0, run.Completed);
        run.Apply(new RuntimeProgress(id, 3, 8, "0.2.1", "F01", "warm", 2, "환경 준비"));
        Assert.AreEqual(3, run.Completed); Assert.AreEqual("환경 준비", run.Stage);
        var editedDraft = summary with { Project = "나중에 선택한 프로젝트", Trials = 22 };
        Assert.AreNotEqual(editedDraft, run.Summary);
        Assert.IsTrue(run.Apply(new RuntimeNotice(ToolKind.Benchmark, id, "실패", false)));
        run.Apply(new RuntimeProgress(id, 8, 8, "0.2.1", "F01", "warm", 2, "정리 완료"));
        Assert.IsFalse(run.Running); Assert.AreEqual("실패", run.Stage); Assert.AreEqual(3, run.Completed);
        Assert.IsFalse(run.Apply(new RuntimeNotice(ToolKind.Benchmark, id, "실패 상세", false)));
    }

    [TestMethod]
    public async Task RecentCardUsesLatestFailureAndKeepsToolsSeparate()
    {
        string root = SampleData.TestDirectory();
        var older = new RunStatus(RunId.New(), ToolKind.Benchmark, "older-project", "완료", DateTimeOffset.UtcNow.AddMinutes(-3), null);
        var latest = new RunStatus(RunId.New(), ToolKind.Benchmark, "latest-project", "실패", DateTimeOffset.UtcNow.AddMinutes(-1), "준비 확인 실패");
        var ai = new RunStatus(RunId.New(), ToolKind.AiWork, "ai-project", "중단됨", DateTimeOffset.UtcNow, null);
        foreach (var status in new[] { older, latest, ai })
            await new AtomicJsonStore<RunStatus>(Path.Combine(root, "runs", status.RunId.Value.ToString("N"), "status.json"), _ => { }).SaveAsync(status);
        var cards = await SidebarRecent.ReadLatestAsync(root);
        Assert.AreEqual(latest.RunId, cards[ToolKind.Benchmark].RunId);
        Assert.AreEqual("실패", cards[ToolKind.Benchmark].Status);
        StringAssert.Contains(cards[ToolKind.Benchmark].Counts, "시행 기록 없음");
        Assert.AreEqual(ai.RunId, cards[ToolKind.AiWork].RunId);
        Assert.IsFalse(cards.ContainsKey(ToolKind.Installation));
        string corrupt = Path.Combine(root, "runs", latest.RunId.Value.ToString("N"), "executions", "broken");
        Directory.CreateDirectory(corrupt); await File.WriteAllTextAsync(Path.Combine(corrupt, "result.json"), "{broken");
        cards = await SidebarRecent.ReadLatestAsync(root);
        Assert.AreEqual(latest.RunId, cards[ToolKind.Benchmark].RunId);
        StringAssert.Contains(cards[ToolKind.Benchmark].Counts, "읽지 못했어요");
    }

    [TestMethod]
    public async Task LegacyMemoSettingsRemainReadableWhenSavingClockPreference()
    {
        string root = SampleData.TestDirectory(); var storage = new ShellPersistence(root);
        var session = (await storage.LoadAsync()).CreateSession();
        session = new ShellSession(session.Preferences with { NoteExpanded = true }, session.Drafts with { DesktopNote = "이전 저장본의 메모" });
        session.SetClockVisible(false);
        var saved = await storage.SaveAsync(session);
        Assert.AreEqual(SaveStatus.Saved, saved.Preferences);
        var restored = (await storage.LoadAsync()).CreateSession();
        Assert.IsTrue(restored.Preferences.NoteExpanded);
        Assert.AreEqual("이전 저장본의 메모", restored.Drafts.DesktopNote);
        Assert.IsFalse(restored.Preferences.ShowClock);
    }
}
