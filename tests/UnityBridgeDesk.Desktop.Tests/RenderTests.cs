using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Diagnostics;
using UnityBridgeDesk.Infrastructure.Benchmark;
using UnityBridgeDesk.Infrastructure.Storage;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Desktop.Shell;
using UnityBridgeDesk.Desktop.Themes;
using UnityBridgeDesk.Desktop.Tools;
using UnityBridgeDesk.Desktop.Catalog;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.Execution;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RenderTests
{
    [ThreadStatic] private static Exception? dispatcherFailure;
    [TestMethod]
    public async Task ActualWpfControlsRenderKoreanAtThreeDpiScalesAndKeepNativeInput()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var directory = SampleData.TestDirectory();
        var thread = new Thread(() =>
        {
            Exception? failure=null;
            Application? app=null;
            var dispatcher=Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            // Async UI exceptions belong in the failed test result, not an unhandled testhost crash.
            dispatcher.UnhandledException+=(_,e)=>{dispatcherFailure??=e.Exception;e.Handled=true;};
            try
            {
                // Only load the visual resources. Never run App.OnStartup against the user's data.
                app = new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source=new Uri("/UnityBridgeDesk;component/Themes/Controls.xaml",UriKind.Relative) });
                VerifySpeedBenchShell(directory);
                var session = new ShellSession();
                foreach (var palette in Enum.GetValues<DeskPalette>())
                {
                    Palette.Apply(palette);
                    var frame = new ToolFrame(ToolKind.AiWork, session);
                    var input = (TextBox)frame.FindName("DraftInput");
                    input.Text = "큐브를 떨어뜨려 주세요.\n한글 입력과 줄바꿈을 보존합니다.";
                    input.Select(3, 4);
                    foreach (double scale in new[] { 1.0, 1.5, 2.0 })
                    {
                        frame.Width = 690; frame.Height = 560;
                        frame.Measure(new Size(690, 560)); frame.Arrange(new Rect(0, 0, 690, 560)); frame.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)(690 * scale), (int)(560 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                        bitmap.Render(frame);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(directory, $"wpf-{palette}-{(int)(scale * 100)}.png")); encoder.Save(file);
                        Assert.AreEqual((int)(690 * scale), bitmap.PixelWidth);
                        Assert.AreEqual(3, input.SelectionStart); Assert.AreEqual(4, input.SelectionLength);
                        Assert.AreEqual(input.Text, session.Drafts.AiPrompt);
                    }
                }
                using var catalog = new CatalogService(Path.Combine(directory, "catalog-render"));
                Complete(catalog.LoadAsync());
                string projectPath = Path.Combine(directory, "한글 경로 UI 시험");
                foreach (string folder in new[] { "Assets", "Packages", "ProjectSettings" }) Directory.CreateDirectory(Path.Combine(projectPath, folder));
                Complete(catalog.RegisterProjectAsync(projectPath, "파스텔 실험실 · UI 시험"));
                var project = catalog.Document.Projects.Single().Project;
                Complete(catalog.SelectProjectAsync(project.Id));
                var target = TargetSummary.From(catalog.Document);
                foreach (var tool in new[] { ToolKind.Installation, ToolKind.AiWork, ToolKind.Benchmark })
                {
                    var frame = new ToolFrame(tool, session); frame.UpdateTarget(target);
                    Assert.AreEqual(project.Id, frame.Target!.ProjectId);
                    Assert.AreEqual(target.Project, ((TextBlock)frame.FindName("ProjectValue")).Text);
                    Assert.Contains("설치 상태와 별개", ((TextBlock)frame.FindName("SecondFieldLabel")).Text);
                }
                var catalogWindow = new CatalogWindow(catalog);
                var content = (FrameworkElement)catalogWindow.Content;
                var tabs = (TabControl)catalogWindow.FindName("Tabs");
                foreach (int index in new[] { 0, 1, 2, 3 })
                {
                    tabs.SelectedIndex = index;
                    content.Measure(new Size(880, 650)); content.Arrange(new Rect(0, 0, 880, 650)); content.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(1320, 975, 144, 144, PixelFormats.Pbgra32); bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, $"catalog-tab-{index}-150.png")); encoder.Save(file);
                    Assert.IsGreaterThan(200, tabs.ActualHeight);
                }
                catalogWindow.Close();
                var runtime=new DeskRuntime(directory,new WorkerRunner(Path.Combine(directory,"unused-fixture-worker.exe")));
                foreach(var kind in new[]{ToolKind.Installation,ToolKind.AiWork,ToolKind.Benchmark})
                {
                    var window=new OperationPanel(runtime,catalog,kind,session);
                    var surface=(DockPanel)window.Content;var panel=surface.Children.OfType<TabControl>().Single();
                    foreach(int index in new[]{0,1,2})
                    foreach(double scale in new[]{1.0,1.5,2.0})
                    {
                        panel.SelectedIndex=index;surface.Measure(new Size(660,500));surface.Arrange(new Rect(0,0,660,500));surface.UpdateLayout();
                        foreach(TabItem stage in panel.Items) VerifySelectionFits(stage);
                        var bitmap=new RenderTargetBitmap((int)(660*scale),(int)(500*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(surface);
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file=File.Create(Path.Combine(directory,$"operation-{kind}-{index}-{scale*100}.png"));encoder.Save(file);
                        Assert.IsGreaterThan(400,panel.ActualHeight);
                        var actionBar=surface.Children.OfType<Border>().Single(x=>AutomationProperties.GetAutomationId(x)=="WorkflowActions");
                        Assert.IsLessThan(1d,Math.Abs(actionBar.TranslatePoint(new Point(),surface).Y+actionBar.ActualHeight-surface.ActualHeight));
                        var primary=Descendants(actionBar).OfType<Button>().First();
                        Assert.IsGreaterThanOrEqualTo(36d,primary.ActualHeight);
                        Assert.IsTrue(primary.TranslatePoint(new Point(),surface).Y>=0);
                    }
                    window.Dispose();
                }
                VerifyGuidedResults(directory);
                VerifySidebar(directory);
                VerifyOperationActions(directory);
                VerifyDiscoveredPaths(directory);
                VerifyAmbiguousPaths(directory);
                VerifyReleaseSelectionRestoration(directory);
                VerifyAutomaticBridgeSetup(directory);
                VerifyBenchmarkReset(directory);
                // Exercise the real shell with legacy floating layouts and a cached execution view.
                string tabRoot = Path.Combine(directory, "tab-shell");
                var delayedFiles = new DeferredWrites();
                var storage = new ShellPersistence(tabRoot,delayedFiles);
                var loaded = Complete(storage.LoadAsync());
                var tabSession = loaded.CreateSession();
                tabSession=new ShellSession(tabSession.Preferences with{PanelBounds=tabSession.Preferences.PanelBounds.SetItem(ToolKind.Benchmark,new(100,80,560,300))},tabSession.Drafts);
                tabSession.Open(ToolKind.AiWork); tabSession.MoveToWorkspace(ToolKind.AiWork, 2); tabSession.Minimize(ToolKind.AiWork);
                var tabCatalog = new CatalogService(tabRoot); Complete(tabCatalog.LoadAsync());
                var main = new MainWindow(tabSession, storage, loaded, tabCatalog);
                var rootGrid = (Grid)main.FindName("RootGrid");
                var toolTabs = (TabControl)main.FindName("ToolTabs");
                var host = (Grid)main.FindName("ToolHost");
                var selector=(Border)main.FindName("SelectorDropdown");
                var trigger=(Button)main.FindName("SelectorTrigger");
                Assert.AreEqual(Visibility.Visible,host.Visibility);
                Assert.AreEqual(Visibility.Hidden,selector.Visibility);
                var cached = host.Children.Cast<ToolFrame>().ToArray();
                var aiFrame = cached.Single(f => f.Tool == ToolKind.AiWork);
                var aiInput = (TextBox)aiFrame.FindName("DraftInput");
                aiInput.Text = "탭 사이에 보존할 한글 지시"; aiInput.Select(3, 4);
                var executionInput = new TextBox { Text = "진행 화면의 입력" };
                var bench = cached.Single(f => f.Tool == ToolKind.Benchmark);
                bench.ShowExecution(executionInput);
                var running = SampleData.Validating(); tabSession.Observe(running);
                foreach (var size in new[] { new Size(1280, 860), new Size(760, 560), new Size(640, 480) })
                foreach (int index in new[] { 0, 1, 2 })
                {
                    toolTabs.SelectedIndex = index;
                    rootGrid.Measure(size); rootGrid.Arrange(new Rect(size)); rootGrid.UpdateLayout();
                    var selected = cached.Single(f => f.Visibility == Visibility.Visible);
                    Assert.AreEqual(new[] { ToolKind.Installation, ToolKind.AiWork, ToolKind.Benchmark }[index], selected.Tool);
                    Assert.AreEqual(3, host.Children.Count);
                    Assert.IsLessThan(1.0, Math.Abs(host.ActualWidth - selected.ActualWidth));
                    Assert.IsLessThan(1.0, Math.Abs(host.ActualHeight - selected.ActualHeight));
                    Assert.IsNull(selected.FindName("TitleDrag")); Assert.IsNull(selected.FindName("ResizeGrip"));
                    foreach (TabItem tab in toolTabs.Items)
                    {
                        VerifySelectionFits(tab);
                        var position = tab.TranslatePoint(new Point(), rootGrid);
                        Assert.IsGreaterThanOrEqualTo(0.0, position.X);
                        Assert.IsLessThanOrEqualTo(size.Width, position.X + tab.ActualWidth);
                    }
                    foreach (double scale in new[] { 1.0, 1.5, 2.0 })
                    {
                        var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                        bitmap.Render(rootGrid);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(directory, $"tabs-{index}-{size.Width}-{scale * 100}.png")); encoder.Save(file);
                    }
                }
                Assert.AreEqual(3, aiInput.SelectionStart); Assert.AreEqual(4, aiInput.SelectionLength);
                Assert.AreEqual("탭 사이에 보존할 한글 지시", tabSession.Drafts.AiPrompt);
                Assert.AreSame(executionInput, ((ContentControl)bench.FindName("ExecutionHost")).Content);
                Assert.AreEqual(Visibility.Visible, ((DockPanel)bench.FindName("ExecutionSurface")).Visibility);
                Assert.AreEqual(Visibility.Visible, ((TextBlock)main.FindName("BenchActivity")).Visibility);
                Assert.IsFalse(running.StopToken.IsCancellationRequested);
                // Old panel positions cannot shrink or displace the fixed work surface.
                Assert.IsNull(bench.FindName("PanelHandle"));
                Assert.IsNull(bench.FindName("PanelFillButton"));
                Assert.IsNull(bench.FindName("PanelTitleBar"));
                Assert.IsNull(bench.FindName("Footer"));
                Assert.AreEqual(new Point(),bench.TranslatePoint(new Point(),host));
                Assert.AreEqual(host.RenderSize,bench.RenderSize);
                toolTabs.SelectedIndex=1;toolTabs.SelectedIndex=2;
                Assert.AreSame(executionInput,((ContentControl)bench.FindName("ExecutionHost")).Content);
                var hostPosition=host.TranslatePoint(new Point(),rootGrid);
                var hostSize=host.RenderSize;
                trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(()=>selector.Opacity==1);
                rootGrid.UpdateLayout();
                Assert.AreEqual(Visibility.Visible,selector.Visibility);
                Assert.AreEqual(Visibility.Visible,host.Visibility);
                Assert.AreEqual(hostPosition,host.TranslatePoint(new Point(),rootGrid));
                Assert.AreEqual(hostSize,host.RenderSize);
                Assert.IsFalse(running.StopToken.IsCancellationRequested);
                var reopenTab=(TabItem)toolTabs.Items[2];
                toolTabs.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonDownEvent,Source=reopenTab});
                PumpUntil(()=>selector.Visibility==Visibility.Hidden);
                Assert.AreEqual(Visibility.Visible,host.Visibility);
                Assert.AreEqual(hostSize,host.RenderSize);
                Assert.IsNull(bench.FindName("PanelPinButton"));
                Assert.IsNull(bench.FindName("PanelHideButton"));
                Assert.AreSame(executionInput,((ContentControl)bench.FindName("ExecutionHost")).Content);
                // Make shutdown overlap an unfinished disk write, regardless of disk speed.
                bool closed=false;main.Closed+=(_,_)=>closed=true;
                delayedFiles.Block=true;
                main.Close();PumpUntil(()=>delayedFiles.Waiting);
                Assert.IsFalse(closed,"The window must wait for pending saves before closing.");
                delayedFiles.Release();PumpUntil(()=>closed);
                var restored=Complete(new ShellPersistence(tabRoot).LoadAsync()).CreateSession();
                Assert.AreEqual(tabSession.Drafts.AiPrompt,restored.Drafts.AiPrompt);
            }
            catch (Exception error) { failure=error; }
            finally
            {
                try{app?.Shutdown();dispatcher.InvokeShutdown();}
                catch(Exception error){failure??=error;}
                SynchronizationContext.SetSynchronizationContext(null);
            }
            failure??=dispatcherFailure;
            if(failure is null)finished.TrySetResult();else finished.TrySetException(failure);
        });
        thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try{await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));}
        finally{Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)),"WPF test thread must exit before testhost teardown.");}
        Console.WriteLine("WPF render evidence: " + directory);
    }

    private static void VerifySelectionFits(TabItem tab)
    {
        var surface=(Border)tab.Template.FindName("SelectionSurface",tab);
        Assert.IsNotNull(surface);
        // The old header panel could allocate less space than the rounded surface needed.
        Assert.IsLessThan(1d,Math.Abs(tab.ActualWidth-surface.ActualWidth),"The selection background must fit its clickable bounds.");
        Assert.IsLessThan(1d,Math.Abs(tab.ActualHeight-surface.ActualHeight),"The selection background must not be clipped vertically.");
        var header=(ContentPresenter)surface.Child;
        var origin=header.TranslatePoint(new Point(),surface);
        Assert.IsGreaterThanOrEqualTo(0d,origin.X);
        Assert.IsGreaterThanOrEqualTo(0d,origin.Y);
        Assert.IsLessThanOrEqualTo(surface.ActualWidth,origin.X+header.ActualWidth);
        Assert.IsLessThanOrEqualTo(surface.ActualHeight,origin.Y+header.ActualHeight);
    }

    private static void VerifyGuidedResults(string directory)
    {
        string root=Path.Combine(directory,"guided-results");
        var plan=SampleData.Draft().Freeze();
        string runRoot=Path.Combine(root,"runs",plan.RunId.Value.ToString("N"));
        var status=new RunStatus(plan.RunId,ToolKind.Benchmark,"fixture-project","완료",DateTimeOffset.UtcNow,null);
        new AtomicJsonStore<RunStatus>(Path.Combine(runRoot,"status.json"),_=>{}).SaveAsync(status).GetAwaiter().GetResult();
        foreach(double[] calls in new[]{new double[]{100,300},new double[]{1000}})
        {
            var route=SampleData.Route(plan);
            var trial=new TrialResult(route,"0.2.1","F01","warm",1,"draft-v1","Synthetic","Succeeded",null,
                120000,calls.Sum(),0,20000,null,null,0,null,null,null,[..calls.Select(x=>new TimingSample("echo",x,2))],"fixture",false){ReleaseId=plan.Releases[0].Id};
            new AtomicJsonStore<TrialResult>(Path.Combine(runRoot,"executions",route.ExecutionId.Value.ToString("N"),"result.json"),_=>{}).SaveAsync(trial).GetAwaiter().GetResult();
        }
        var failed=RunId.New();
        new AtomicJsonStore<RunStatus>(Path.Combine(root,"runs",failed.Value.ToString("N"),"status.json"),_=>{})
            .SaveAsync(new(failed,ToolKind.Benchmark,"failed-fixture","실패",DateTimeOffset.UtcNow.AddMinutes(-1),"준비 확인 실패 · 합성 기록" )).GetAwaiter().GetResult();
        using var catalog=new CatalogService(root);Complete(catalog.LoadAsync());
        var runtime=new DeskRuntime(root,new WorkerRunner(Path.Combine(root,"never-executed.exe")));
        using var panel=new OperationPanel(runtime,catalog,ToolKind.Benchmark,new ShellSession());
        var surface=(DockPanel)panel.Content;var tabs=surface.Children.OfType<TabControl>().Single();
        var content=(StackPanel)((ScrollViewer)((TabItem)tabs.Items[2]).Content).Content;
        var history=content.Children.OfType<ListBox>().Single();
        var cards=content.Children.OfType<StackPanel>().Single();
        var heading=content.Children.OfType<TextBlock>().First();
        var before=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            PumpUntil(()=>heading.Text.StartsWith("완료 · 성공 2"));
            Assert.AreEqual(plan.RunId,((HistoryItem)history.SelectedItem).Status!.RunId);
            string labels=string.Join("\n",Labels(cards));
            StringAssert.Contains(labels,"평균 600.0 ms/호출");
            StringAssert.Contains(labels,"독립 시행 2회");
            tabs.SelectedIndex=2;
            panel.Measure(new Size(760,560));panel.Arrange(new Rect(0,0,760,560));panel.UpdateLayout();
            var bitmap=new RenderTargetBitmap(760,560,96,96,PixelFormats.Pbgra32);bitmap.Render(panel);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var file=File.Create(Path.Combine(directory,"guided-results-loaded-100.png")))encoder.Save(file);
            // Rapid selection cannot leave a previous success card under a failed run heading.
            history.SelectedItem=history.Items.Cast<HistoryItem>().Single(x=>x.Status!.RunId==failed);
            history.SelectedItem=history.Items.Cast<HistoryItem>().Single(x=>x.Status!.RunId==plan.RunId);
            history.SelectedItem=history.Items.Cast<HistoryItem>().Single(x=>x.Status!.RunId==failed);
            PumpUntil(()=>heading.Text.StartsWith("실패 · 성공 0"));
            Assert.IsFalse(Labels(cards).Any(x=>x.Contains("평균 600.0")));
            Assert.IsTrue(Labels(cards).Any(x=>x.Contains("완료된 측정값이 없습니다")));
            // Unfinished input survives disposing and reopening the actual panel, without review or execution.
            var repeat=Descendants(panel).OfType<TextBox>().Single(x=>AutomationProperties.GetAutomationId(x)=="Runner-repeats");
            repeat.Text="1.";
            var save=panel.FlushInputAsync();PumpUntil(()=>save.IsCompleted);Assert.IsTrue(save.GetAwaiter().GetResult());
            using var reopened=new OperationPanel(runtime,catalog,ToolKind.Benchmark,new ShellSession());
            reopened.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var restored=Descendants(reopened).OfType<TextBox>().Single(x=>AutomationProperties.GetAutomationId(x)=="Runner-repeats");
            var primary=Descendants(reopened).OfType<Button>().Single(x=>AutomationProperties.GetAutomationId(x)=="BenchmarkPrimary");
            PumpUntil(()=>primary.IsEnabled);
            Assert.AreEqual("1.",restored.Text);
            Assert.AreEqual("구성 확인",primary.Content);
            Assert.IsFalse(runtime.IsRunning);
        }
        finally{SynchronizationContext.SetSynchronizationContext(before);}
    }
    private static void VerifySidebar(string directory)
    {
        string root=Path.Combine(directory,"guided-results");
        var storage=new ShellPersistence(root);var loaded=Complete(storage.LoadAsync());
        var session=loaded.CreateSession();
        var catalog=new CatalogService(root);Complete(catalog.LoadAsync());
        var runtime=new DeskRuntime(root,new WorkerRunner(Path.Combine(root,"never-executed.exe")));
        var before=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var main=new MainWindow(session,storage,loaded,catalog,runtime);
            var grid=(Grid)main.FindName("RootGrid");
            var host=(Grid)main.FindName("ToolHost");
            var frame=host.Children.OfType<ToolFrame>().Single(x=>x.Tool==ToolKind.Benchmark);
            var panel=(OperationPanel)((ContentControl)frame.FindName("ExecutionHost")).Content;
            panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            main.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var recentTitle=(TextBlock)main.FindName("SidebarRecentTitle");
            var recentCounts=(TextBlock)main.FindName("SidebarRecentCounts");
            var detail=(TextBlock)main.FindName("SidebarDetail");
            PumpUntil(()=>recentTitle.Text=="완료" && detail.Text.Contains("입력 확인"));
            StringAssert.Contains(recentCounts.Text,"성공 2");
            var repeat=Descendants(panel).OfType<TextBox>().Single(x=>AutomationProperties.GetAutomationId(x)=="Runner-repeats");
            repeat.Text="3";
            Assert.AreEqual(0,panel.SidebarSummary().Trials);
            StringAssert.Contains(((TextBlock)main.FindName("SidebarCount")).Text,"0개 시행 예정");
            ((Button)main.FindName("SidebarResultButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var tabs=((DockPanel)panel.Content).Children.OfType<TabControl>().Single();
            PumpUntil(()=>tabs.SelectedIndex==2 && Descendants(panel).OfType<ListBox>().Any(x=>x.SelectedItem is HistoryItem item && item.Status?.Status=="완료"));
            Assert.IsFalse(runtime.IsRunning,"Opening the recent card must only navigate to that result.");
            grid.Measure(new Size(1280,860));grid.Arrange(new Rect(0,0,1280,860));grid.UpdateLayout();
            var bitmap=new RenderTargetBitmap(1280,860,96,96,PixelFormats.Pbgra32);bitmap.Render(grid);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var file=File.Create(Path.Combine(directory,"sidebar-results-1280.png")))encoder.Save(file);
            Assert.AreEqual(48d,((TextBlock)main.FindName("ClockText")).FontSize);
            Assert.IsNull(main.FindName("SidebarNote")); Assert.IsNull(main.FindName("DesktopNote")); Assert.IsNull(main.FindName("ShowNoteCheck"));
            Assert.IsNotNull(main.Icon);
            using(var icon=Application.GetResourceStream(new Uri("/UnityBridgeDesk;component/Assets/desk.ico",UriKind.Relative)).Stream)
            {
                var decoded=BitmapDecoder.Create(icon,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                CollectionAssert.AreEquivalent(new[]{16,20,24,32,40,48,64,128,256},decoded.Frames.Select(x=>x.PixelWidth).ToArray());
            }
            grid.Measure(new Size(1150,560));grid.Arrange(new Rect(0,0,1150,560));grid.UpdateLayout();
            var scroll=(ScrollViewer)main.FindName("SidebarScroll");var companion=(Border)main.FindName("Companion");
            Assert.IsGreaterThan(0d,scroll.ScrollableHeight,"Small windows must allow scrolling all sidebar cards.");
            Assert.IsLessThanOrEqualTo(560d,companion.TranslatePoint(new Point(),grid).Y+companion.ActualHeight);
            bool closed=false;main.Closed+=(_,_)=>closed=true;main.Close();PumpUntil(()=>closed);
        }
        finally{SynchronizationContext.SetSynchronizationContext(before);}
    }

    private static void VerifyOperationActions(string directory)
    {
        var before=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            foreach(var tool in new[]{ToolKind.Installation,ToolKind.AiWork})
            {
                string root=Path.Combine(directory,"fixed-actions-"+tool);
                using var catalog=new CatalogService(root);Complete(catalog.LoadAsync());
                var runtime=new DeskRuntime(root,new WorkerRunner(Path.Combine(root,"never-executed.exe")));
                using var panel=new OperationPanel(runtime,catalog,tool,new ShellSession());
                panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                var primary=Descendants(panel).OfType<Button>().Single(x=>AutomationProperties.GetAutomationId(x)=="OperationPrimary");
                PumpUntil(()=>primary.IsEnabled);
                Assert.AreEqual("구성 확인",primary.Content);
                primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(()=>primary.IsEnabled);
                var editor=Descendants(panel).OfType<TextBox>().Single(x=>AutomationProperties.GetAutomationId(x)=="Runner-editor");
                StringAssert.Contains(editor.ToolTip?.ToString(),"실행 파일을 찾을 수 없습니다");
                Assert.AreEqual("구성 확인",primary.Content);
                Assert.IsFalse(runtime.IsRunning,"Reviewing incomplete input must never start a worker.");
                var tabs=((DockPanel)panel.Content).Children.OfType<TabControl>().Single();
                tabs.SelectedIndex=2;Assert.AreEqual("새 작업 준비",primary.Content);
                primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(0,tabs.SelectedIndex);
                Assert.AreEqual("구성 확인",primary.Content);
            }
        }
        finally{SynchronizationContext.SetSynchronizationContext(before);}
    }
    private static void VerifyDiscoveredPaths(string directory)
    {
        var before=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            string root=Path.Combine(directory,"automatic-paths"), native=Path.Combine(root,"native"), home=Path.Combine(root,"home",".codex");
            Directory.CreateDirectory(native);Directory.CreateDirectory(home);
            string executable=Path.Combine(native,"codex.exe");File.WriteAllText(executable,"synthetic path fixture, never executed");
            File.WriteAllText(Path.Combine(home,"auth.json"),"not read by discovery");
            var scanner=new LocalDiscovery(new(Path.Combine(root,"home"),Path.Combine(root,"roaming"),Path.Combine(root,"local"),Path.Combine(root,"programs"),Path.Combine(root,"app"),native));
            using var catalog=new CatalogService(root);Complete(catalog.LoadAsync());
            var runtime=new DeskRuntime(root,new WorkerRunner(Path.Combine(root,"never-executed.exe")));
            using var panel=new OperationPanel(runtime,catalog,ToolKind.AiWork,new ShellSession(),scanner);
            panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var primary=Descendants(panel).OfType<Button>().Single(x=>AutomationProperties.GetAutomationId(x)=="OperationPrimary");
            PumpUntil(()=>primary.IsEnabled);
            TextBox Field(string key)=>Descendants(panel).OfType<TextBox>().Single(x=>AutomationProperties.GetAutomationId(x)=="Runner-"+key);
            Assert.AreEqual(executable,Field("codex").Text);Assert.AreEqual(home,Field("auth").Text);
            Assert.AreEqual("",Field("editor").Text);Assert.AreEqual("",Field("model").Text);
            Expander PathDetails(string key)=>Descendants(panel).OfType<Expander>().Single(x=>AutomationProperties.GetAutomationId(x)=="PathDetails-"+key);
            string Status(string key)=>Descendants(panel).OfType<TextBlock>().Single(x=>AutomationProperties.GetAutomationId(x)=="ConnectionStatus-"+key).Text;
            Assert.IsFalse(PathDetails("codex").IsExpanded,"Detected paths must not require manual setup.");
            Assert.IsFalse(PathDetails("auth").IsExpanded);
            StringAssert.Contains(Status("codex"),"실행 파일 확인됨");StringAssert.Contains(Status("auth"),"로그인 파일 위치 확인됨");
            StringAssert.Contains(Status("editor"),"프로젝트 선택");
            // A valid manual executable must survive another scan, even with an unconventional filename.
            string manual=Path.Combine(native,"my-codex.exe");File.WriteAllText(manual,"never executed");Field("codex").Text=manual;
            var retry=Descendants(panel).OfType<Button>().Single(x=>AutomationProperties.GetAutomationId(x)=="FindPath-codex");
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpUntil(()=>retry.IsEnabled);
            Assert.AreEqual(manual,Field("codex").Text);
            Field("auth").Text=native;StringAssert.Contains(Status("auth"),"로그인 파일을 찾지 못");
            Field("auth").Text=home;
            Assert.IsFalse(Descendants(panel).OfType<CheckBox>().Single(x=>x.Content?.ToString()=="이 작업 폴더의 파일 변경과 네트워크 사용 허용").IsChecked==true);
            var save=panel.FlushInputAsync();PumpUntil(()=>save.IsCompleted);Assert.IsTrue(save.Result);
            var memory=new LocalSetupStore(root).LoadAsync();PumpUntil(()=>memory.IsCompleted);
            Assert.AreEqual(manual,memory.Result.Paths["codex"]);
            Assert.IsFalse(runtime.IsRunning);
        }
        finally{SynchronizationContext.SetSynchronizationContext(before);}
    }
    private static void VerifyAmbiguousPaths(string directory)
    {
        var before=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            string root=Path.Combine(directory,"ambiguous-paths");
            string bin=Path.Combine(root,"local","OpenAI","Codex","bin");
            foreach(string version in new[]{"first-build","second-build"})
            {string path=Path.Combine(bin,version);Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"codex.exe"),"fixture");}
            using var catalog=new CatalogService(root);Complete(catalog.LoadAsync());
            var runtime=new DeskRuntime(root,new WorkerRunner(Path.Combine(root,"never-executed.exe")));
            var scanner=new LocalDiscovery(new(Path.Combine(root,"home"),Path.Combine(root,"roaming"),Path.Combine(root,"local"),Path.Combine(root,"programs"),Path.Combine(root,"app"),""));
            using var panel=new OperationPanel(runtime,catalog,ToolKind.AiWork,new ShellSession(),scanner);
            panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var primary=Descendants(panel).OfType<Button>().Single(x=>AutomationProperties.GetAutomationId(x)=="OperationPrimary");PumpUntil(()=>primary.IsEnabled);
            var input=Descendants(panel).OfType<TextBox>().Single(x=>AutomationProperties.GetAutomationId(x)=="Runner-codex");
            var choices=Descendants(panel).OfType<ComboBox>().Single(x=>AutomationProperties.GetAutomationId(x)=="PathChoices-codex");
            var details=Descendants(panel).OfType<Expander>().Single(x=>AutomationProperties.GetAutomationId(x)=="PathDetails-codex");
            Assert.AreEqual("",input.Text,"Ambiguous discoveries must not silently choose an executable.");
            Assert.AreEqual(2,choices.Items.Count);Assert.IsTrue(details.IsExpanded);Assert.AreEqual(-1,choices.SelectedIndex);
            choices.SelectedIndex=1;Assert.IsTrue(File.Exists(input.Text));Assert.IsTrue(input.Text.StartsWith(bin+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase));
            string selected=input.Text;choices.SelectedIndex=0;Assert.AreNotEqual(selected,input.Text);Assert.IsTrue(File.Exists(input.Text));
            foreach(string key in new[]{"codex","auth"})
            {
                var card=Descendants(panel).OfType<Border>().Single(x=>AutomationProperties.GetAutomationId(x)=="Connection-"+key);
                card.Width=440;card.Measure(new Size(440,double.PositiveInfinity));card.Arrange(new Rect(new Point(),card.DesiredSize));card.UpdateLayout();
                var bitmap=new RenderTargetBitmap(660,(int)Math.Ceiling(card.ActualHeight*1.5),144,144,PixelFormats.Pbgra32);bitmap.Render(card);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file=File.Create(Path.Combine(directory,"connection-"+key+"-150.png"));encoder.Save(file);
            }
            Assert.IsGreaterThanOrEqualTo(120d,input.ActualWidth);Assert.IsFalse(runtime.IsRunning);
        }
        finally{SynchronizationContext.SetSynchronizationContext(before);}
    }
    private static void VerifyReleaseSelectionRestoration(string directory)
    {
        foreach(var kind in new[]{ToolKind.Installation,ToolKind.AiWork,ToolKind.Benchmark})
        {
            string root=Path.Combine(directory,"release-selection-"+kind);
            var observation=new ArtifactObservation(InspectionStatus.Missing,"selection fixture",DateTimeOffset.UtcNow,null,null,null,null,null);
            var a=new CatalogArtifact(Guid.NewGuid(),"CLI A",ArtifactKind.CliExecutable,Path.Combine(root,"missing-a.exe"),observation,observation);
            var b=new CatalogArtifact(Guid.NewGuid(),"CLI B",ArtifactKind.CliExecutable,Path.Combine(root,"missing-b.exe"),observation,observation);
            var first=new CatalogRelease(ReleaseId.New(),"Release A",ComparisonAxis.CliOnly,a.Id,null);
            var second=new CatalogRelease(ReleaseId.New(),"Release B",ComparisonAxis.CliOnly,b.Id,null);
            var document=CatalogDocument.Empty with{Artifacts=[a,b],Releases=[first,second],SelectedRelease=first.Id};
            Complete(new AtomicJsonStore<CatalogDocument>(Path.Combine(root,"catalog","catalog.json"),x=>x.Validate()).SaveAsync(document));
            using var catalog=new CatalogService(root);Complete(catalog.LoadAsync());
            var runtime=new DeskRuntime(root,new WorkerRunner(Path.Combine(root,"never-executed.exe")));
            var scanner=new LocalDiscovery(new(Path.Combine(root,"home"),Path.Combine(root,"roaming"),Path.Combine(root,"local"),Path.Combine(root,"programs"),Path.Combine(root,"app"),""));
            ListBox List(OperationPanel panel)=>Descendants(panel).OfType<ListBox>().Single(x=>x.Items.Count==2&&x.Items[0].ToString()=="Release A");
            void Load(OperationPanel panel)
            {
                panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                var primary=Descendants(panel).OfType<Button>().Single(x=>AutomationProperties.GetAutomationId(x)==(kind==ToolKind.Benchmark?"BenchmarkPrimary":"OperationPrimary"));
                PumpUntil(()=>primary.IsEnabled);
            }
            using(var panel=new OperationPanel(runtime,catalog,kind,new ShellSession(),scanner))
            {
                Load(panel);Assert.AreEqual("Release A",List(panel).SelectedItem?.ToString());
                if(kind==ToolKind.Benchmark)List(panel).SelectedItems.Add(List(panel).Items[1]);
                else
                {
                    Assert.IsTrue(Complete(catalog.SelectReleaseAsync(second.Id)).Success);
                    Assert.AreEqual("Release B",List(panel).SelectedItem?.ToString());
                    Descendants(panel).OfType<Button>().Single(x=>x.Content?.ToString()=="보관함의 현재 선택 불러오기").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual("Release B",List(panel).SelectedItem?.ToString());
                }
                Assert.IsTrue(Complete(panel.FlushInputAsync()));
            }
            using(var restored=new OperationPanel(runtime,catalog,kind,new ShellSession(),scanner))
            {
                Load(restored);
                Assert.AreEqual(kind==ToolKind.Benchmark?2:1,List(restored).SelectedItems.Count);
                if(kind!=ToolKind.Benchmark)Assert.AreEqual("Release B",List(restored).SelectedItem?.ToString());
            }
            Assert.IsFalse(runtime.IsRunning);
        }
    }
    private static void VerifyAutomaticBridgeSetup(string directory)
    {
        foreach(bool ambiguous in new[]{false,true})
        {
            string root=Path.Combine(directory,"bridge-auto-"+ambiguous), app=Path.Combine(root,"app");
            var definitions=new List<BridgeSetupRelease>();
            foreach(string version in new[]{"0.2.0","0.2.1"})
            {
                string folder=Path.Combine(app,version);Directory.CreateDirectory(folder);
                string cli=Path.Combine(folder,"unity-bridge.exe");File.Copy(Environment.ProcessPath!,cli);
                string connector=Path.Combine(folder,"unity-bridge-connector");Directory.CreateDirectory(connector);
                File.WriteAllText(Path.Combine(connector,"package.json"),System.Text.Json.JsonSerializer.Serialize(new{name=LocalInspector.ConnectorPackageName,version}));
                var inspector=new LocalInspector();
                definitions.Add(new(version,Complete(inspector.InspectArtifactAsync(cli,ArtifactKind.CliExecutable)).Sha256!,
                    Complete(inspector.InspectArtifactAsync(connector,ArtifactKind.ConnectorFolder)).Sha256!,new string('a',40)));
            }
            string projects=Path.Combine(root,"home","Unity Projects");
            foreach(string name in ambiguous?new[]{"project-a","project-b"}:new[]{"project-a"})
            {
                string path=Path.Combine(projects,name);
                foreach(string part in new[]{"Assets","Packages","ProjectSettings"})Directory.CreateDirectory(Path.Combine(path,part));
                File.WriteAllText(Path.Combine(path,"Packages","manifest.json"),"{\"dependencies\":{}}");
                File.WriteAllText(Path.Combine(path,"ProjectSettings","ProjectVersion.txt"),"m_EditorVersion: 6000.3.23f1");
            }
            var scanner=new LocalDiscovery(new(Path.Combine(root,"home"),Path.Combine(root,"roaming"),Path.Combine(root,"local"),Path.Combine(root,"programs"),app,""));
            using var client=new System.Net.Http.HttpClient(new NoSetupNetwork());
            using var catalog=new CatalogService(Path.Combine(root,"data"));Complete(catalog.LoadAsync());
            var runtime=new DeskRuntime(catalog.DataRoot,new WorkerRunner(Path.Combine(root,"never-executed.exe")));
            using var panel=new OperationPanel(runtime,catalog,ToolKind.Benchmark,new ShellSession(),scanner,new BridgeEnvironmentSetup(catalog.DataRoot,client,definitions));
            panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var prepare=Descendants(panel).OfType<Button>().Single(x=>AutomationProperties.GetAutomationId(x)=="BridgeAutoSetup");
            var status=Descendants(panel).OfType<TextBlock>().Single(x=>AutomationProperties.GetAutomationId(x)=="BridgeAutoSetupStatus");
            var primary=Descendants(panel).OfType<Button>().Single(x=>AutomationProperties.GetAutomationId(x)=="BenchmarkPrimary");
            PumpUntil(()=>prepare.IsEnabled);
            prepare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.IsFalse(primary.IsEnabled);
            PumpUntil(()=>prepare.IsEnabled);
            Assert.StartsWith("CLI·Connector 준비 완료",status.Text);
            Assert.AreEqual(2,catalog.Document.Releases.Length);
            var list=Descendants(panel).OfType<ListBox>().Single(x=>x.Items.Count==2&&x.Items[0].ToString()=="UnityBridge 0.2.0");
            Assert.AreEqual(2,list.SelectedItems.Count);
            Assert.AreEqual("구성 확인",primary.Content,"Missing Editor must not be presented as ready to run.");
            if(ambiguous){Assert.IsNull(catalog.Document.SelectedProject);Assert.Contains("프로젝트 선택",status.Text);}
            else Assert.IsNotNull(catalog.Document.SelectedProject);
            Assert.IsFalse(runtime.IsRunning);Assert.IsFalse(Directory.Exists(Path.Combine(catalog.DataRoot,"workspaces")));
            Assert.IsTrue(catalog.Document.Artifacts.All(x=>x.Path.StartsWith(Path.Combine(catalog.DataRoot,"releases"))));
            Assert.AreEqual("{\"dependencies\":{}}",File.ReadAllText(Path.Combine(projects,"project-a","Packages","manifest.json")));
            prepare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpUntil(()=>prepare.IsEnabled);
            Assert.AreEqual(2,catalog.Document.Releases.Length);
            panel.Width=680;panel.Height=620;panel.Measure(new Size(680,620));panel.Arrange(new Rect(0,0,680,620));panel.UpdateLayout();
            var bitmap=new RenderTargetBitmap(1020,930,144,144,PixelFormats.Pbgra32);bitmap.Render(panel);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(directory,"bridge-auto-"+ambiguous+"-150.png"));encoder.Save(file);
        }
    }
    private sealed class NoSetupNetwork : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken ct)=>throw new InvalidOperationException("Synthetic UI setup must use local fixtures.");
    }
    private static void VerifySpeedBenchShell(string directory)
    {
        string root=Path.Combine(directory,"speed-window");
        Complete(UnityBridgeDesk.Infrastructure.SpeedBench.SpeedFiles.Write(Path.Combine(root,"speed","releases","release-list.json"),new[] {
            new UnityBridgeDesk.Infrastructure.SpeedBench.ReleaseChoice(1,"v0.2.1","0.2.1","Stable",false,"https://github.com/zjxps2007/UnityBridge/releases/download/v0.2.1/unity-bridge-windows-amd64.exe",null,"fixture"),
            new UnityBridgeDesk.Infrastructure.SpeedBench.ReleaseChoice(2,"v0.2.2-rc.2","0.2.2-rc.2","RC2",true,"https://github.com/zjxps2007/UnityBridge/releases/download/v0.2.2-rc.2/unity-bridge-windows-amd64.zip",null,"fixture") }));
        var window=new SpeedBenchWindow(root,new LocalDiscovery(new(root,root,root,root,root,"")));
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var form=(StackPanel)window.FindName("PreparationForm");PumpUntil(()=>form.IsEnabled);
        var tabs=(TabControl)window.FindName("Tabs");Assert.AreEqual(3,tabs.Items.Count);
        var rc=((StackPanel)window.FindName("ReleaseList")).Children.OfType<CheckBox>().Single(c=>c.Content.ToString()!.Contains("rc.2"));
        Assert.IsTrue(rc.IsEnabled);Assert.IsFalse(rc.IsChecked);Assert.Contains("런타임",rc.Content.ToString()!);rc.IsChecked=true;
        Assert.IsTrue(((TextBlock)window.FindName("EditorStatus")).Text.Contains("찾지 못했습니다"));
        Assert.AreEqual(48.0,((TextBlock)window.FindName("Clock")).FontSize);
        Assert.IsNull(window.FindName("GuestPassword")); Assert.IsNull(window.FindName("VmList"));
        var repeats=(TextBox)window.FindName("Repeats");repeats.Text="invalid";
        var reset=(Button)window.FindName("ResetButton");reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpUntil(()=>form.IsEnabled);
        Assert.AreEqual("2",repeats.Text);reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpUntil(()=>form.IsEnabled);
        Assert.AreEqual("invalid",repeats.Text);
        var start=Descendants((DependencyObject)window.Content).OfType<Button>().Single(x=>AutomationProperties.GetAutomationId(x)=="LocalBenchmarkStart");
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpUntil(()=>form.IsEnabled);
        Assert.IsTrue(((TextBlock)window.FindName("State")).Text.Contains("Unity"));
        Assert.IsFalse(Directory.Exists(Path.Combine(root,"speed","runs")));
        var content=(FrameworkElement)window.Content;
        foreach(int index in new[]{0,1,2})
        {
            tabs.SelectedIndex=index;content.Measure(new Size(1230,800));content.Arrange(new Rect(0,0,1230,800));content.UpdateLayout();
            if(index==0)
            {
                Assert.IsGreaterThanOrEqualTo(36d,start.ActualHeight);
                Assert.IsTrue(start.TranslatePoint(new Point(),content).Y>=0);
                Assert.IsTrue(start.TranslatePoint(new Point(),content).Y+start.ActualHeight<=content.ActualHeight-38);
            }
            var image=new RenderTargetBitmap(1845,1200,144,144,PixelFormats.Pbgra32);image.Render(content);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
            using var file=File.Create(Path.Combine(directory,$"speed-local-{index}-150.png"));encoder.Save(file);
        }


        bool closed=false;window.Closed+=(_,_)=>closed=true;window.Close();PumpUntil(()=>closed);
        string saved=File.ReadAllText(Path.Combine(root,"speed","local-settings.json"));
        Assert.Contains("EditorPath",saved);Assert.DoesNotContain("GuestUser",saved);
    }

    private static void VerifyBenchmarkReset(string directory)
    {
        string root=Path.Combine(directory,"benchmark-reset"),project=Path.Combine(root,"project");
        foreach(string part in new[]{"Assets","Packages","ProjectSettings"})Directory.CreateDirectory(Path.Combine(project,part));
        File.WriteAllText(Path.Combine(project,"ProjectSettings","ProjectVersion.txt"),"m_EditorVersion: 6000.3.23f1");
        string cli=Path.Combine(root,"unity-bridge.exe");File.Copy(Environment.ProcessPath!,cli);
        using var catalog=new CatalogService(root);Complete(catalog.LoadAsync());
        Assert.IsTrue(Complete(catalog.UseDiscoveredProjectAsync(project)).Success);
        Assert.IsTrue(Complete(catalog.UseDiscoveredReleaseAsync(cli,null,"retained version")).Success);
        var originalCatalog=catalog.Document;
        var runtime=new DeskRuntime(root,new WorkerRunner(Path.Combine(root,"never-executed.exe")));
        var scanner=new LocalDiscovery(new(Path.Combine(root,"home"),Path.Combine(root,"roaming"),Path.Combine(root,"local"),Path.Combine(root,"programs"),Path.Combine(root,"app"),""));
        var session=new ShellSession(drafts:DeskDrafts.Empty with{AiPrompt="other AI instruction",InstallationNote="other installation note"});
        var storage=new ShellPersistence(root);Complete(storage.LoadAsync());
        string priorResult=Path.Combine(root,"runs","keep-result.txt"),otherSettings=Path.Combine(root,"settings","runner-AiWork.json");
        Directory.CreateDirectory(Path.GetDirectoryName(priorResult)!);Directory.CreateDirectory(Path.GetDirectoryName(otherSettings)!);
        File.WriteAllText(priorResult,"original result");File.WriteAllText(otherSettings,"original AI preferences");
        Button FindAction(OperationPanel panel,string id)=>Descendants(panel).OfType<Button>().Single(x=>AutomationProperties.GetAutomationId(x)==id);
        TextBox Field(OperationPanel panel,string key)=>Descendants(panel).OfType<TextBox>().Single(x=>AutomationProperties.GetAutomationId(x)=="Runner-"+key);
        CheckBox Mode(OperationPanel panel,string name)=>Descendants(panel).OfType<CheckBox>().Single(x=>x.Content?.ToString()==name);
        void Load(OperationPanel panel){panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));PumpUntil(()=>FindAction(panel,"BenchmarkReset").IsEnabled);}
        using(var panel=new OperationPanel(runtime,catalog,ToolKind.Benchmark,session,scanner))
        {
            Load(panel);
            foreach(string key in new[]{"editor","codex"})Field(panel,key).Text=cli;
            Field(panel,"auth").Text=root;
            Field(panel,"repeats").Text="1."; // A reset must work even when the current form cannot be parsed.
            Field(panel,"timeout").Text="999";Field(panel,"prepare").Text="321";Field(panel,"model").Text="chosen-model";
            Field(panel,"reasoning").Text="high";Field(panel,"calls").Text="77";
            var ai=Mode(panel,"AI 제작");ai.IsChecked=true;ai.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            var fixedMode=Mode(panel,"고정 명령");fixedMode.IsChecked=false;fixedMode.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Mode(panel,"실패한 복제본 보관").IsChecked=false;Mode(panel,"성공한 복제본도 보관").IsChecked=true;
            var memo=Descendants(panel).OfType<TextBox>().Single(x=>AutomationProperties.GetAutomationId(x)=="BenchmarkMemo");memo.Text="keep my experiment note";
            var reset=FindAction(panel,"BenchmarkReset");reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpUntil(()=>reset.IsEnabled);
            Assert.AreEqual("2",Field(panel,"repeats").Text);Assert.AreEqual("180",Field(panel,"timeout").Text);Assert.AreEqual("600",Field(panel,"prepare").Text);
            Assert.AreEqual("",Field(panel,"model").Text);Assert.AreEqual("medium",Field(panel,"reasoning").Text);Assert.AreEqual("100",Field(panel,"calls").Text);
            Assert.IsTrue(fixedMode.IsChecked);Assert.IsFalse(ai.IsChecked);Assert.AreEqual("",memo.Text);
            Assert.IsTrue(Mode(panel,"실패한 복제본 보관").IsChecked);Assert.IsFalse(Mode(panel,"성공한 복제본도 보관").IsChecked);
            Assert.AreEqual(cli,Field(panel,"editor").Text);Assert.AreEqual(cli,Field(panel,"codex").Text);Assert.AreEqual(root,Field(panel,"auth").Text);
            Assert.AreEqual(originalCatalog,catalog.Document);Assert.AreEqual("구성 확인",FindAction(panel,"BenchmarkPrimary").Content);
            FindAction(panel,"BenchmarkResetUndo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpUntil(()=>reset.IsEnabled);
            Assert.AreEqual("1.",Field(panel,"repeats").Text);Assert.AreEqual("999",Field(panel,"timeout").Text);Assert.AreEqual("chosen-model",Field(panel,"model").Text);
            Assert.AreEqual("keep my experiment note",memo.Text);Assert.IsTrue(ai.IsChecked);Assert.IsFalse(fixedMode.IsChecked);
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PumpUntil(()=>reset.IsEnabled);
            Complete(panel.FlushInputAsync());Complete(storage.SaveAsync(session));
            panel.Width=680;panel.Height=720;panel.Measure(new Size(680,720));panel.Arrange(new Rect(0,0,680,720));panel.UpdateLayout();
            var bitmap=new RenderTargetBitmap(1020,1080,144,144,PixelFormats.Pbgra32);bitmap.Render(panel);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output=File.Create(Path.Combine(directory,"benchmark-reset-150.png"));encoder.Save(output);
        }
        var restoredSession=Complete(storage.LoadAsync()).CreateSession();
        using(var reopened=new OperationPanel(runtime,catalog,ToolKind.Benchmark,restoredSession,scanner))
        {
            Load(reopened);
            Assert.AreEqual("2",Field(reopened,"repeats").Text);Assert.AreEqual("180",Field(reopened,"timeout").Text);
            Assert.AreEqual("",Field(reopened,"model").Text);Assert.AreEqual(cli,Field(reopened,"editor").Text);
            Assert.IsTrue(Mode(reopened,"고정 명령").IsChecked);Assert.IsFalse(Mode(reopened,"AI 제작").IsChecked);
            Assert.AreEqual("",restoredSession.Drafts.BenchmarkNote);Assert.AreEqual("other AI instruction",restoredSession.Drafts.AiPrompt);Assert.AreEqual("other installation note",restoredSession.Drafts.InstallationNote);
            Assert.AreEqual("retained version",Descendants(reopened).OfType<ListBox>().Single(x=>x.Items.Count==1&&x.Items[0].ToString()=="retained version").SelectedItem?.ToString());
            Assert.AreEqual(Visibility.Collapsed,FindAction(reopened,"BenchmarkResetUndo").Visibility);
        }
        Assert.AreEqual("original result",File.ReadAllText(priorResult));Assert.AreEqual("original AI preferences",File.ReadAllText(otherSettings));Assert.IsFalse(runtime.IsRunning);
    }
    private static IEnumerable<string> Labels(DependencyObject parent)
    {
        if(parent is TextBlock text)yield return text.Text;
        foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach(string label in Labels(child))yield return label;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach(var descendant in Descendants(child))yield return descendant;
    }
    private static void PumpUntil(Func<bool> condition)
    {
        var clock=Stopwatch.StartNew();var frame=new DispatcherFrame();
        var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(10)};
        timer.Tick+=(_,_)=>{if(condition()||dispatcherFailure is not null||clock.Elapsed>TimeSpan.FromSeconds(10))frame.Continue=false;};
        timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
        if(dispatcherFailure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(dispatcherFailure).Throw();
        Assert.IsTrue(condition(),"비동기 결과 화면이 제한 시간 안에 갱신되어야 합니다.");
    }
    private static void Complete(Task task){PumpUntil(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
    private static T Complete<T>(Task<T> task){PumpUntil(()=>task.IsCompleted);return task.GetAwaiter().GetResult();}
    private sealed class DeferredWrites : IAtomicFileOperations
    {
        private readonly AtomicFileOperations actual=new();
        private readonly TaskCompletionSource release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block { get; set; }
        public bool Waiting { get; private set; }
        public void Release()=>release.TrySetResult();
        public async Task WriteNewAndFlushAsync(string path,ReadOnlyMemory<byte> bytes,CancellationToken ct)
        {
            if(Block){Waiting=true;await release.Task.WaitAsync(ct);}
            await actual.WriteNewAndFlushAsync(path,bytes,ct);
        }
        public void Commit(string temporary,string destination,string? backup)=>actual.Commit(temporary,destination,backup);
    }
}

