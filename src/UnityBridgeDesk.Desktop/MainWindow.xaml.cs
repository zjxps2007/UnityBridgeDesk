using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Desktop.Shell;
using UnityBridgeDesk.Desktop.Themes;
using UnityBridgeDesk.Desktop.Tools;
using UnityBridgeDesk.Infrastructure.Storage;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Desktop.Catalog;
using UnityBridgeDesk.Infrastructure.Execution;

namespace UnityBridgeDesk.Desktop;

public partial class MainWindow : Window
{
    private readonly ShellSession session;
    private readonly ShellPersistence persistence;
    private readonly CatalogService catalog;
    private readonly DeskRuntime? runtime;
    private readonly Dictionary<ToolKind,OperationPanel> operations=[];
    private readonly Dictionary<ToolKind, ToolFrame> frames = [];
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private bool ready;
    private bool allowClose;
    private bool closing;
    private bool synchronizingWidgets;
    private bool synchronizingTabs;
    private DeskPalette? appliedPalette;
    private IInputElement? focusBeforeOverlay;

    public MainWindow(ShellSession session, ShellPersistence persistence, ShellLoadResult loaded, CatalogService catalog, DeskRuntime? runtime=null)
    {
        this.session = session; this.persistence = persistence; this.catalog = catalog;this.runtime=runtime;
        Palette.Apply(session.Preferences.Palette);
        appliedPalette = session.Preferences.Palette;
        InitializeComponent();
        if(runtime is not null)
        {
            runtime.StateChanged+=ShellRuntimeState;
            runtime.Changed+=ShellRuntimeNotice;
            runtime.ProgressChanged+=ShellRuntimeProgress;
        }
        Width = Math.Min(session.Preferences.AppWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(session.Preferences.AppHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(760, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(560, SystemParameters.WorkArea.Height);
        foreach (var tool in new[] { ToolKind.Installation, ToolKind.AiWork, ToolKind.Benchmark })
        {
            var frame = new ToolFrame(tool, session);
            frame.CatalogRequested += OpenCatalog;
            if(runtime is not null)
            {
                frame.EnableExecution();
                frame.ExecutionRequested+=()=>
                {
                    frame.ShowExecution(EnsureOperation(tool));
                };
            }
            frames.Add(tool, frame); ToolHost.Children.Add(frame);
        }
        InitializePanels();
        InitializeSidebar();
        ready = true;
        session.Changed += SessionChanged;
        catalog.Changed += UpdateTargets; UpdateTargets();
        saveTimer.Tick += async (_, _) => { saveTimer.Stop(); await SaveAsync(); };
        clockTimer.Tick += (_, _) => UpdateClock();
        clockTimer.Start(); UpdateClock();
        UpdateShell();
        Loaded += async (_, _) => { UpdateShell(); await RefreshSidebarHistoryAsync(); };
        Closed += (_, _) => { DisposeSidebar();saveTimer.Stop(); clockTimer.Stop(); session.Changed -= SessionChanged; catalog.Changed -= UpdateTargets;catalog.Dispose();foreach(var panel in operations.Values)panel.Dispose(); };
        if (loaded.Preferences.Status is not (ReadStatus.Current or ReadStatus.Missing) || loaded.Drafts.Status is not (ReadStatus.Current or ReadStatus.Missing))
        {
            RecoveryNotice.Visibility = Visibility.Visible;
            RecoveryText.Text = loaded.Preferences.Status == ReadStatus.RecoveredBackup || loaded.Drafts.Status == ReadStatus.RecoveredBackup
                ? "이전 정상 저장본을 불러왔습니다. 설정과 초안을 확인해 주세요."
                : "설정 또는 초안을 읽지 못했습니다. 원본을 보존하고 임시 상태로 열었어요. 새로운 형식의 파일은 덮어쓰지 않아요.";
        }
    }
    private void UpdateTargets()
    {
        var target = TargetSummary.From(catalog.Document);
        TargetLabel.Text = target.ProjectId is null ? "보관함 · 대상 선택" : target.Project;
        TargetButton.ToolTip = target.ProjectPath + "\n프로젝트 · 버전 보관함";
        foreach (var frame in frames.Values) frame.UpdateTarget(target);
        UpdateSidebar();
    }
    private void OpenCatalog()
    {
        CloseOverlay();
        new CatalogWindow(catalog) { Owner = this }.ShowDialog();
    }
    private void CatalogClick(object sender, RoutedEventArgs e) => OpenCatalog();
    private void SessionChanged(ShellChange change)
    {
        if (change != ShellChange.Draft) UpdateShell();
        else UpdateSidebar();
        saveTimer.Stop(); saveTimer.Start();
        SaveStatusText.Text = "초안·설정 저장 대기";
    }
    private void UpdateShell()
    {
        if (!ready) return;
        if (appliedPalette != session.Preferences.Palette)
        { Palette.Apply(session.Preferences.Palette); appliedPalette = session.Preferences.Palette; }
        var preferences = session.Preferences;
        synchronizingTabs = true;
        ToolTabs.SelectedItem = ToolTabs.Items.Cast<TabItem>().Single(tab => (string)tab.Tag == session.ActiveTool.ToString());
        synchronizingTabs = false;
        SelectedToolLabel.Text=ToolFrame.NameFor(session.ActiveTool);
        foreach (var pair in frames)
        {
            pair.Value.Visibility = session.ActiveTool == pair.Key ? Visibility.Visible : Visibility.Collapsed;
            pair.Value.UpdateActivity();
        }
        if(session.ActiveTool==ToolKind.Benchmark&&runtime is not null&&!operations.ContainsKey(ToolKind.Benchmark))
        {
            frames[ToolKind.Benchmark].ShowExecution(EnsureOperation(ToolKind.Benchmark));
        }
        ClockWidget.Visibility = preferences.ShowClock ? Visibility.Visible : Visibility.Collapsed;
        Widgets.Visibility = RootGrid.ActualWidth >= 1100 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (tool, label) in new[] { (ToolKind.Installation, InstallActivity), (ToolKind.AiWork, AiActivity), (ToolKind.Benchmark, BenchActivity) })
            label.Visibility = session.ActiveExecutions.Any(x => x.Snapshot.Route.Tool == tool) ? Visibility.Visible : Visibility.Collapsed;
        ActivityLabel.Text = runtime?.IsRunning==true || session.ActiveExecutions.Any() ? "작업 진행 중" : "실행 없음";
        UpdateSidebar();
    }
    private async Task<bool> SaveAsync()
    {
        await saveGate.WaitAsync();
        try
        {
            var saved = await persistence.SaveAsync(session);
            bool success = saved.Preferences == SaveStatus.Saved && saved.Drafts == SaveStatus.Saved;
            SaveStatusText.Text = success ? "초안·설정 저장됨" : "저장하지 못한 변경이 있어요 · 기존 파일 보존";
            return success;
        }
        finally { saveGate.Release(); }
    }
    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        DateText.Text = now.ToString("ddd  dd  MMM", CultureInfo.InvariantCulture).ToUpperInvariant();
    }
    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!ready) return;
        if (WindowState == WindowState.Normal) session.SetAppSize(ActualWidth, ActualHeight);
        OverlayCard.MaxHeight = Math.Max(200, ActualHeight - 60);
        UpdateShell();
    }
    private void RootSizeChanged(object sender, SizeChangedEventArgs e) { if (ready) UpdateShell(); }
    private void OpenTool(ToolKind tool)
    {
        CloseOverlay();session.SelectTool(tool);HideSelector();
        Dispatcher.BeginInvoke(DispatcherPriority.Input,()=>{if(session.ActiveTool==tool&&Overlay.Visibility!=Visibility.Visible)frames[tool].FocusInput();});
    }
    private void ToolTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || synchronizingTabs || e.Source != ToolTabs) return;
        if (ToolTabs.SelectedItem is TabItem tab)session.SelectTool(Enum.Parse<ToolKind>((string)tab.Tag));
    }
    private void ShowOverlay(string kind)
    {
        HideSelector();
        focusBeforeOverlay = Keyboard.FocusedElement;
        LauncherPanel.Visibility = kind == "launcher" ? Visibility.Visible : Visibility.Collapsed;
        CustomizePanel.Visibility = kind == "customize" ? Visibility.Visible : Visibility.Collapsed;
        OverlayTitle.Text = kind == "customize" ? "작업실 꾸미기" : "도구 찾기";
        Overlay.Visibility = Visibility.Visible;
        if (kind == "launcher") { LauncherSearch.Clear(); PopulateLauncher(); LauncherSearch.Focus(); }
        if (kind == "customize")
        {
            synchronizingWidgets = true;
            ShowClockCheck.IsChecked = session.Preferences.ShowClock;
            synchronizingWidgets = false;
            LilacButton.Focus();
        }
    }
    private void PopulateLauncher()
    {
        if (!ready) return;
        LauncherResults.Children.Clear();
        foreach (var tool in frames.Keys.Where(x => (ToolFrame.NameFor(x) + " " + x).Contains(LauncherSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            var button = new Button { Content = ToolFrame.NameFor(tool) + "   ↗", Margin = new(0, 0, 0, 8), Padding = new(14), HorizontalContentAlignment = HorizontalAlignment.Left, Tag = tool };
            button.Click += (_, _) => OpenTool(tool); LauncherResults.Children.Add(button);
        }
        if (LauncherResults.Children.Count == 0) LauncherResults.Children.Add(new TextBlock { Text = "일치하는 도구가 없어요. 다른 이름으로 검색해 보세요.", TextWrapping = TextWrapping.Wrap });
    }
    private void CloseOverlay()
    {
        if (Overlay.Visibility != Visibility.Visible) return;
        Overlay.Visibility = Visibility.Collapsed;
        if (focusBeforeOverlay is UIElement element && element.IsVisible) element.Focus();
        else LauncherButton.Focus();
    }
    private void LauncherClick(object sender, RoutedEventArgs e) => ShowOverlay("launcher");
    private void CustomizeClick(object sender, RoutedEventArgs e) => ShowOverlay("customize");
    private void OverlayCloseClick(object sender, RoutedEventArgs e) => CloseOverlay();
    private void OverlayMouseDown(object sender, MouseButtonEventArgs e) { if (e.OriginalSource == Overlay) CloseOverlay(); }
    private void LauncherSearchChanged(object sender, TextChangedEventArgs e) => PopulateLauncher();
    private void LauncherSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && LauncherResults.Children.OfType<Button>().FirstOrDefault() is { } first)
        { OpenTool((ToolKind)first.Tag); e.Handled = true; }
    }
    private void PaletteClick(object sender, RoutedEventArgs e) => session.SetPalette(Enum.Parse<DeskPalette>((string)((Button)sender).Tag));
    private void WidgetsChanged(object sender, RoutedEventArgs e)
    {
        if (ready && !synchronizingWidgets && Overlay.Visibility == Visibility.Visible) session.SetClockVisible(ShowClockCheck.IsChecked == true);
    }
    private async void RecoverySaveClick(object sender, RoutedEventArgs e)
    {
        persistence.AllowReset();
        if (await SaveAsync()) RecoveryNotice.Visibility = Visibility.Collapsed;
    }
    private void ShellKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        // No global hooks. Do not intercept native editing, IME composition, Enter, clipboard or Alt+F4.
        if (key == Key.Escape && Overlay.Visibility == Visibility.Visible) { CloseOverlay(); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Control && key == Key.K) { ShowOverlay("launcher"); e.Handled = true; return; }
        if (Overlay.Visibility == Visibility.Visible) return;
        if (key == Key.Escape && selectorOpen){HideSelector();e.Handled=true;return;}
        if (modifiers == ModifierKeys.Alt && key is >= Key.D1 and <= Key.D3)
            OpenTool(new[] { ToolKind.Installation, ToolKind.AiWork, ToolKind.Benchmark }[key - Key.D1]);
        else if (key == Key.Tab && (modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
        { session.CycleTool(modifiers.HasFlag(ModifierKeys.Shift));OpenTool(session.ActiveTool); }
        else return;
        e.Handled = true;
    }
    private void AppTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.ButtonBase) return;
        if (e.OriginalSource is not TextBlock and not Border) return;
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void AppMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void AppMaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void AppCloseClick(object sender, RoutedEventArgs e) => Close();
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        RootGrid.IsEnabled=false;
        if (runtime?.IsRunning==true)
        {
            closing=true;ActivityLabel.Text="작업 중단 · 소유한 프로세스 정리 중";
            try{await runtime.StopAllAsync();}
            catch(Exception error){ActivityLabel.Text="종료 중 기록 확인 필요: "+error.Message;}
            finally{closing=false;}
        }
        closing = true; saveTimer.Stop();
        var inputSaved=await Task.WhenAll(operations.Values.Select(panel=>panel.FlushInputAsync()));
        bool saved = await SaveAsync()&&inputSaved.All(value=>value);
        if (!saved && MessageBox.Show(this, "일부 변경을 저장하지 못했어요. 저장하지 않고 종료할까요?", "저장 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        { closing = false;RootGrid.IsEnabled=true; return; }
        allowClose = true; Close();
    }
}
