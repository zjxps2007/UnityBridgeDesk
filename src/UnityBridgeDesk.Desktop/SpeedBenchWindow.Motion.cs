using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop;

public partial class SpeedBenchWindow
{
    private int previousWorkflow = -1;
    private DispatcherOperation? workflowMotion;
    private AshaCompanion? asha, fox; // asha is the legacy cat controller, now named Ara.
    private void InitializeMotion()
    {
        asha = new AshaCompanion(WorkspaceSurface, CompanionLayer, CompanionHost, CompanionHome, Mascot);
        fox = new AshaCompanion(WorkspaceSurface, CompanionLayer, FoxHost, CompanionHome, FoxMascot);
        asha.OtherCompanion = fox; fox.OtherCompanion = asha;
        Activated += MotionContextChanged; Deactivated += MotionContextChanged; StateChanged += MotionContextChanged;
        SystemParameters.StaticPropertyChanged += SystemMotionChanged;
        Closed += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= SystemMotionChanged;
            workflowMotion?.Abort(); workflowMotion = null;
            DeskMotion.SetAllowed(this, false); asha.Dispose(); fox.Dispose(); Mascot.DisposeMotion(); FoxMascot.DisposeMotion();
        };
        UpdateMotion();
    }
    private void MotionContextChanged(object? sender, EventArgs e) => UpdateMotion();
    private void SystemMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) UpdateMotion();
    }
    private void UpdateMotion()
    {
        if (Mascot is null) return;
        CompanionHost.Visibility = settings.ShowCompanion ? Visibility.Visible : Visibility.Collapsed;
        FoxHost.Visibility = settings.ShowFoxCompanion ? Visibility.Visible : Visibility.Collapsed;
        CompanionLayer.Visibility = (settings.ShowCompanion || settings.ShowFoxCompanion) && !benchmarkActive && !settingsOpen ? Visibility.Visible : Visibility.Collapsed;
        bool allowed = settings.AnimateInterface && SystemParameters.ClientAreaAnimation && !benchmarkActive && !settingsOpen && !closed && IsActive && WindowState != WindowState.Minimized;
        if (!allowed) { workflowMotion?.Abort(); workflowMotion = null; }
        DeskMotion.SetAllowed(this, allowed); Mascot.MotionEnabled = allowed && settings.ShowCompanion;
        FoxMascot.MotionEnabled = allowed && settings.ShowFoxCompanion;
        asha?.Configure(allowed && settings.ShowCompanion, settings.CompanionRoaming, settings.CompanionActivity);
        fox?.Configure(allowed && settings.ShowFoxCompanion, settings.FoxRoaming, settings.FoxActivity);
        if (AppearanceMotion is not null)
            AppearanceMotion.ToolTip = SystemParameters.ClientAreaAnimation ? "짧은 화면 전환·선택 밑줄·캐릭터 움직임" : "Windows에서 애니메이션을 꺼 둔 상태입니다.";
        SyncAppearance();
    }
    private async void MotionToggleClicked(object sender, RoutedEventArgs e)
    {
        settings = settings with { AnimateInterface = !settings.AnimateInterface }; UpdateMotion();
        try { await Save(CancellationToken.None, appearanceOnly: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Log("움직임 설정을 저장하지 못했습니다: " + error.Message); }
    }
    private void RevealWorkflow()
    {
        asha?.SetPage(workspacePage == 1 ? "history" : "work:" + Tabs.SelectedIndex);
        fox?.SetPage(workspacePage == 1 ? "history" : "work:" + Tabs.SelectedIndex);
        asha?.RefreshAfterLayout(); fox?.RefreshAfterLayout();
        if (Tabs is not { } tabs) return;
        int next = tabs.SelectedIndex, before = previousWorkflow; previousWorkflow = next;
        workflowMotion?.Abort(); workflowMotion = null;
        if (workspacePage != 0 || before < 0 || before == next || !DeskMotion.GetAllowed(this)) return;
        // TabControl swaps and lays out SelectedContent after raising SelectionChanged.
        // Animate the new content once it is present, not the outgoing page.
        workflowMotion = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            workflowMotion = null;
            if (closed || tabs.SelectedIndex != next || !DeskMotion.GetAllowed(this)) return;
            if (tabs.Template?.FindName("PART_SelectedContentHost", tabs) is FrameworkElement content)
                DeskMotion.Reveal(content);
        }));
    }
}
