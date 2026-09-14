using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Desktop.Shell;
using UnityBridgeDesk.Infrastructure.Storage;
using UnityBridgeDesk.Tests;

[assembly: Parallelize(Workers = 4, Scope = ExecutionScope.MethodLevel)]

namespace UnityBridgeDesk.Desktop.Tests;

[TestClass]
public sealed class ShellTests
{
    [TestMethod]
    public void FixedTabsIgnoreLegacyMinimizeAndWorkspaceWhilePreservingDraftsAndExecution()
    {
        var shell = new ShellSession(drafts: new("설치", "유지할 지시", "벤치", true, true, "메모"));
        shell.Open(ToolKind.AiWork); shell.MoveToWorkspace(ToolKind.AiWork, 2); shell.Minimize(ToolKind.AiWork);
        var layout = shell.Preferences.Windows; var drafts = shell.Drafts;
        var execution = SampleData.Validating(); shell.Observe(execution);
        foreach (var tool in new[] { ToolKind.Installation, ToolKind.AiWork, ToolKind.Benchmark, ToolKind.AiWork })
        { shell.SelectTool(tool); Assert.AreEqual(tool, shell.ActiveTool); }
        Assert.AreEqual(layout, shell.Preferences.Windows);
        Assert.AreEqual(drafts, shell.Drafts);
        Assert.IsFalse(execution.StopToken.IsCancellationRequested);
        Assert.AreEqual(1, shell.ActiveExecutions.Count());
        shell.CycleTool(); Assert.AreEqual(ToolKind.Benchmark, shell.ActiveTool);
        shell.CycleTool(); Assert.AreEqual(ToolKind.Installation, shell.ActiveTool);
        shell.CycleTool(backwards: true); Assert.AreEqual(ToolKind.Benchmark, shell.ActiveTool);
        Assert.Throws<ArgumentException>(() => shell.SelectTool(ToolKind.Unknown));
        Assert.Throws<ArgumentException>(() => (shell.Preferences with { ActiveTool = ToolKind.Unknown }).Validate());
    }

    [TestMethod]
    public async Task TabSelectionRestoresAlongsideOldThemeAndDraftFiles()
    {
        string root = SampleData.TestDirectory();
        var storage = new ShellPersistence(root); var shell = (await storage.LoadAsync()).CreateSession();
        shell.SetPalette(DeskPalette.Rose); shell.SetDrafts(shell.Drafts with { AiPrompt = "복원할 한글 지시" });
        // An old-format document has no explicit selected tab.
        shell.Open(ToolKind.AiWork); shell.MoveToWorkspace(ToolKind.AiWork, 2); shell.SwitchWorkspace(2);
        await storage.SaveAsync(shell);
        var migrated = (await storage.LoadAsync()).CreateSession();
        Assert.AreEqual(ToolKind.AiWork, migrated.ActiveTool);
        migrated.SelectTool(ToolKind.Installation); await storage.SaveAsync(migrated);
        var restored = (await new ShellPersistence(root).LoadAsync()).CreateSession();
        Assert.AreEqual(ToolKind.Installation, restored.ActiveTool);
        Assert.AreEqual(DeskPalette.Rose, restored.Preferences.Palette);
        Assert.AreEqual("복원할 한글 지시", restored.Drafts.AiPrompt);
        Assert.IsEmpty(restored.ActiveExecutions);
    }

    [TestMethod]
    public async Task LegacyPanelPositionsRemainReadableAlongsideThemeAndDrafts()
    {
        string root=SampleData.TestDirectory();var storage=new ShellPersistence(root);
        var shell=(await storage.LoadAsync()).CreateSession();
        Assert.IsEmpty(shell.Preferences.PanelBounds);
        shell=new ShellSession(shell.Preferences with{PanelBounds=shell.Preferences.PanelBounds.SetItem(ToolKind.Benchmark,new(100,50,700,400)).SetItem(ToolKind.AiWork,new(20,30,600,350))});
        shell.SetPalette(DeskPalette.Rose);
        shell.SetDrafts(shell.Drafts with{AiPrompt="보존할 작업 지시"});
        await storage.SaveAsync(shell);
        var restored=(await new ShellPersistence(root).LoadAsync()).CreateSession();
        Assert.AreEqual(new DeskBounds(100,50,700,400),restored.Preferences.PanelBounds[ToolKind.Benchmark]);
        Assert.AreEqual(new DeskBounds(0,0,500,300),restored.Preferences.PanelBounds[ToolKind.Benchmark].Fit(500,300));
        Assert.AreEqual(DeskPalette.Rose,restored.Preferences.Palette);
        Assert.AreEqual("보존할 작업 지시",restored.Drafts.AiPrompt);
        Assert.AreEqual(new DeskBounds(20,30,600,350),restored.Preferences.PanelBounds[ToolKind.AiWork]);
    }

    [TestMethod]
    public void CloseMinimizeWorkspaceAndThemeKeepAllDrafts()
    {
        var draft = new DeskDrafts("설치 메모", "바닥에 큐브를 만들어 주세요.", "낙하 실험", true, true, "작업실 메모");
        var shell = new ShellSession(drafts: draft);
        foreach (var tool in new[] { ToolKind.Installation, ToolKind.AiWork, ToolKind.Benchmark })
        {
            shell.Open(tool); shell.Minimize(tool); shell.Open(tool); shell.Close(tool); shell.Open(tool);
        }
        shell.SwitchWorkspace(2); shell.SetPalette(DeskPalette.Rose); shell.SetPlacement(WindowPlacement.Tiled);
        shell.ToggleBackground(); shell.ToggleBackground(); shell.SwitchWorkspace(0);
        Assert.AreEqual(draft, shell.Drafts);
        Assert.AreEqual(3, shell.Preferences.Windows.Count(shell.IsVisible));
    }

    [TestMethod]
    public void DockFindsExistingWindowInOtherWorkspace()
    {
        var shell = new ShellSession();
        shell.Open(ToolKind.AiWork); shell.MoveToWorkspace(ToolKind.AiWork, 2);
        shell.Open(ToolKind.AiWork);
        Assert.AreEqual(2, shell.Preferences.Workspace);
        Assert.IsTrue(shell.IsVisible(shell.Window(ToolKind.AiWork)));
        shell.Close(ToolKind.AiWork); shell.SwitchWorkspace(1); shell.Open(ToolKind.AiWork);
        Assert.AreEqual(1, shell.Window(ToolKind.AiWork).Workspace);
    }

    [TestMethod]
    public void ViewOperationsNeverCancelOwnedElsewhereExecution()
    {
        var shell = new ShellSession();
        var state = SampleData.Validating(); shell.Observe(state);
        shell.Close(ToolKind.Benchmark); shell.SwitchWorkspace(2); shell.SetPalette(DeskPalette.Mint);
        Assert.IsFalse(state.StopToken.IsCancellationRequested);
        Assert.AreEqual(ExecutionPhase.Validating, state.Snapshot.Phase);
        Assert.AreEqual("작업 진행 중", shell.Activity(ToolKind.Benchmark));
        Assert.AreEqual(1, shell.ActiveExecutions.Count());
        shell.Open(ToolKind.Benchmark);
        Assert.IsTrue(shell.IsVisible(shell.Window(ToolKind.Benchmark)));
    }

    [TestMethod]
    public void TilingAndMaximizeRestoreOriginalFreeBounds()
    {
        var shell = new ShellSession();
        shell.SetViewport(1200, 600); shell.Open(ToolKind.AiWork);
        var original = shell.BoundsFor(ToolKind.AiWork);
        shell.ToggleMaximize(ToolKind.AiWork);
        Assert.AreEqual(new DeskBounds(0, 0, 1200, 600), shell.BoundsFor(ToolKind.AiWork));
        shell.ToggleMaximize(ToolKind.AiWork);
        Assert.AreEqual(original, shell.BoundsFor(ToolKind.AiWork));
        shell.SetPlacement(WindowPlacement.Tiled); shell.SetPlacement(WindowPlacement.Free);
        Assert.AreEqual(original, shell.BoundsFor(ToolKind.AiWork));
    }

    [TestMethod]
    [DataRow(1920, 1080, 1.0)]
    [DataRow(1920, 1080, 1.5)]
    [DataRow(1920, 1080, 2.0)]
    [DataRow(1366, 768, 1.5)]
    [DataRow(1280, 800, 2.0)]
    public void ViewportAndDpiChangesKeepWindowsAndCaptionControlsReachable(int pixelsWidth, int pixelsHeight, double scale)
    {
        var shell = new ShellSession();
        foreach (var tool in new[] { ToolKind.Installation, ToolKind.AiWork, ToolKind.Benchmark }) shell.Open(tool);
        var width = pixelsWidth / scale - 50;
        var height = pixelsHeight / scale - 220;
        shell.SetViewport(width, height);
        foreach (var placement in new[] { WindowPlacement.Free, WindowPlacement.Tiled })
        {
            shell.SetPlacement(placement);
            foreach (var tool in shell.Preferences.Windows.Select(x => x.Tool))
            {
                var bounds = shell.BoundsFor(tool);
                Assert.IsGreaterThanOrEqualTo(0, bounds.X);
                Assert.IsGreaterThanOrEqualTo(0, bounds.Y);
                Assert.IsLessThanOrEqualTo(width + 0.01, bounds.X + bounds.Width);
                Assert.IsLessThanOrEqualTo(height + 0.01, bounds.Y + bounds.Height);
                Assert.IsGreaterThan(150, bounds.Width);
                Assert.IsGreaterThan(42, bounds.Height);
            }
        }
    }

    [TestMethod]
    public void MovingBeyondEdgesClampsAndDraggingTileSwitchesToFree()
    {
        var shell = new ShellSession(); shell.SetViewport(800, 500);
        shell.Move(ToolKind.Benchmark, 100000, -100000);
        var bounds = shell.BoundsFor(ToolKind.Benchmark);
        Assert.AreEqual(0, bounds.Y); Assert.AreEqual(800, bounds.X + bounds.Width);
        shell.SetPlacement(WindowPlacement.Tiled); shell.Move(ToolKind.Benchmark, 10, 10);
        Assert.AreEqual(WindowPlacement.Free, shell.Preferences.Placements[0]);
    }

    [TestMethod]
    public void FocusOrderIsUniqueAndCyclesWithoutChangingInput()
    {
        var shell = new ShellSession(); shell.Open(ToolKind.AiWork); shell.Open(ToolKind.Installation);
        Assert.AreEqual(ToolKind.Installation, shell.Focused!.Tool);
        shell.CycleFocus(); Assert.AreNotEqual(ToolKind.Installation, shell.Focused!.Tool);
        for (int i = 0; i < 200; i++) shell.Focus(ToolKind.AiWork);
        Assert.AreEqual(3, shell.Preferences.Windows.Select(x => x.ZOrder).Distinct().Count());
        Assert.AreEqual(3, shell.Preferences.Windows.Max(x => x.ZOrder));
    }

    [TestMethod]
    public void WorkspacePlacementIsIndependentAndTileUnmaximizes()
    {
        var shell = new ShellSession(); shell.ToggleMaximize(ToolKind.Benchmark); shell.SetPlacement(WindowPlacement.Tiled);
        Assert.IsFalse(shell.Window(ToolKind.Benchmark).IsMaximized);
        shell.SwitchWorkspace(1); Assert.AreEqual(WindowPlacement.Free, shell.Preferences.Placements[1]);
        shell.SwitchWorkspace(0); Assert.AreEqual(WindowPlacement.Tiled, shell.Preferences.Placements[0]);
    }

    [TestMethod]
    public void InvalidSavedGeometryAndDuplicateToolsAreRejected()
    {
        var defaults = ShellPreferences.Default;
        Assert.Throws<ArgumentException>(() => (defaults with { Windows = defaults.Windows.SetItem(1, defaults.Windows[0]) }).Validate());
        Assert.Throws<ArgumentException>(() => new DeskBounds(double.NaN, 0, 400, 300).Validate());
        Assert.Throws<ArgumentException>(() => (defaults with { Workspace = 3 }).Validate());
    }

    [TestMethod]
    public async Task RestartRestoresThemeLayoutAndKoreanDraftsInSeparateFiles()
    {
        var root = SampleData.TestDirectory(); var storage = new ShellPersistence(root);
        var shell = (await storage.LoadAsync()).CreateSession();
        shell.Open(ToolKind.AiWork); shell.MoveToWorkspace(ToolKind.AiWork, 2); shell.SwitchWorkspace(2);
        shell.SetPalette(DeskPalette.Mint); shell.SetClockVisible(false);
        shell.SetDrafts(shell.Drafts with { AiPrompt = "큐브를 떨어뜨려 주세요.\n충돌을 확인해요.", AiCreation = true });
        var saved = await storage.SaveAsync(shell);
        Assert.AreEqual(SaveStatus.Saved, saved.Preferences); Assert.AreEqual(SaveStatus.Saved, saved.Drafts);
        var reopened = (await new ShellPersistence(root).LoadAsync()).CreateSession();
        Assert.AreEqual(DeskPalette.Mint, reopened.Preferences.Palette);
        Assert.AreEqual(2, reopened.Preferences.Workspace); Assert.AreEqual(shell.Drafts, reopened.Drafts);
        Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(root, "settings", "shell.json"))).Contains("aiPrompt", StringComparison.Ordinal));
        Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(root, "drafts", "desk.json"))).Contains("palette", StringComparison.Ordinal));
        Assert.IsEmpty(reopened.ActiveExecutions);
    }

    [TestMethod]
    public async Task CorruptPreferencesDoNotEraseGoodDraftsAndRequireExplicitReset()
    {
        var root = SampleData.TestDirectory(); var storage = new ShellPersistence(root);
        var shell = (await storage.LoadAsync()).CreateSession();
        shell.SetDrafts(shell.Drafts with { AiPrompt = "보존할 초안" }); await storage.SaveAsync(shell);
        var path = Path.Combine(root, "settings", "shell.json"); await File.WriteAllTextAsync(path, "broken");
        var next = new ShellPersistence(root); var result = await next.LoadAsync();
        Assert.AreEqual(ReadStatus.Corrupt, result.Preferences.Status);
        Assert.AreEqual("보존할 초안", result.Drafts.Value!.AiPrompt);
        await next.SaveAsync(result.CreateSession()); Assert.AreEqual("broken", await File.ReadAllTextAsync(path));
        next.AllowReset(); Assert.AreEqual(SaveStatus.Saved, (await next.SaveAsync(result.CreateSession())).Preferences);
    }

    [TestMethod]
    public async Task FutureFormatCannotBeOverwrittenByReset()
    {
        var root = SampleData.TestDirectory(); Directory.CreateDirectory(Path.Combine(root, "settings"));
        var path = Path.Combine(root, "settings", "shell.json");
        const string future = "{\"formatVersion\":99,\"data\":{}}"; await File.WriteAllTextAsync(path, future);
        var storage = new ShellPersistence(root); var loaded = await storage.LoadAsync(); storage.AllowReset();
        Assert.AreEqual(SaveStatus.UnsupportedVersion, (await storage.SaveAsync(loaded.CreateSession())).Preferences);
        Assert.AreEqual(future, await File.ReadAllTextAsync(path));
    }
}
