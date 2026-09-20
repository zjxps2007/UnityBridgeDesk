using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnityBridgeDesk.Desktop.Controls;
using UnityBridgeDesk.Desktop.Shell;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop;

public partial class SpeedBenchWindow
{
    private int workspacePage;
    private bool settingsOpen;
    private IInputElement? settingsReturnFocus;
    private int? openingHistoryRequest;
    private bool syncingAppearance;
    private readonly Dictionary<int, BitmapImage> landscapeImages = [];

    private void ShowWorkspacePage(int page)
    {
        if (HistoryPage is null || workspacePage == page) return;
        // Finishing a file read must not pull the user out of the page they moved to.
        historyRequest++;
        if (openingHistoryRequest is not null)
        {
            openingHistoryRequest = null; OpenHistoryButton.IsEnabled = History.SelectedItem is not null;
            HistoryCount.Text = "선택한 기록을 열어 결과를 확인하세요.";
        }
        workspacePage = page;
        asha?.SetPage(page == 1 ? "history" : "work:" + Tabs.SelectedIndex);
        fox?.SetPage(page == 1 ? "history" : "work:" + Tabs.SelectedIndex);
        Tabs.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavigation();
        if (page == 1) DeskMotion.Reveal(HistoryPage);
        asha?.RefreshGeometry(); fox?.RefreshGeometry();
    }
    private void DraftClicked(object sender, RoutedEventArgs e) { ShowWorkspacePage(0); Tabs.SelectedIndex = 0; }
    private void RecordsClicked(object sender, RoutedEventArgs e) { ShowWorkspacePage(1); HistorySearch.Focus(); }
    private void WorkspaceBackClicked(object sender, RoutedEventArgs e)
    {
        ShowWorkspacePage(0); Tabs.Focus();
    }
    private void CurrentRunClicked(object sender, RoutedEventArgs e) { ShowWorkspacePage(0); Tabs.SelectedIndex = 1; }
    private void SettingsClicked(object sender, RoutedEventArgs e)
    {
        if (settingsOpen) return;
        settingsReturnFocus = Keyboard.FocusedElement;
        settingsOpen = true;
        SyncAppearance();
        SettingsOverlay.Visibility = Visibility.Visible;
        WorkspaceSurface.IsEnabled = false;
        UpdateMotion();
        SettingsCloseButton.Focus();
    }
    private void SettingsCloseClicked(object sender, RoutedEventArgs e) => CloseSettings();
    private void SettingsBackdropClicked(object sender, MouseButtonEventArgs e)
    { CloseSettings(); e.Handled = true; }
    private void CloseSettings()
    {
        if (!settingsOpen) return;
        AppearanceActivity.IsDropDownOpen = false; AppearanceFoxActivity.IsDropDownOpen = false;
        settingsOpen = false;
        SettingsOverlay.Visibility = Visibility.Collapsed;
        WorkspaceSurface.IsEnabled = true;
        UpdateMotion();
        if (settingsReturnFocus is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } target) target.Focus();
        else ManagementMenuButton.Focus();
        settingsReturnFocus = null;
    }

    private void ApplyLandscape()
    {
        bool plain = settings.PlainScreen;
        LandscapeCover.Visibility = plain ? Visibility.Collapsed : Visibility.Visible;
        if (!plain)
        {
            int palette = Math.Clamp(settings.Palette, 0, 2);
            if (!landscapeImages.TryGetValue(palette, out var bitmap))
            {
                string file = palette switch { 1 => "cloud-station-rose-lofi.png", 2 => "cloud-station-mint-lofi.png", _ => "cloud-station-lofi.png" };
                bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri($"pack://application:,,,/UnityBridgeDesk;component/Assets/{file}");
                bitmap.EndInit(); bitmap.Freeze(); landscapeImages.Add(palette, bitmap);
            }
            LandscapeCover.Source = bitmap;
        }
        var color = ((SolidColorBrush)Resources["Workspace"]).Color;
        var wash = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        wash.GradientStops.Add(new(color, 0)); wash.GradientStops.Add(new(color, .38));
        wash.GradientStops.Add(new(Color.FromArgb(175, color.R, color.G, color.B), .68));
        wash.GradientStops.Add(new(Color.FromArgb(plain ? (byte)255 : (byte)30, color.R, color.G, color.B), 1));
        wash.Freeze(); CoverWash.Background = wash;
        Resources["HeaderPaper"] = new SolidColorBrush(Color.FromArgb(235, color.R, color.G, color.B));
        HeaderRow.Height = new(plain || WorkspaceSurface.ActualHeight is > 0 and < 740 ? 76 : 104);
        SyncAppearance();
    }
    private void SyncAppearance()
    {
        if (AppearanceActivity is null || AppearanceMotion is null) return;
        syncingAppearance = true;
        try
        {
            RadioButton[] choices = [LilacScreen, RoseScreen, MintScreen, PlainScreen];
            choices[settings.PlainScreen ? 3 : Math.Clamp(settings.Palette, 0, 2)].IsChecked = true;
            AppearanceCompanion.IsChecked = settings.ShowCompanion;
            AppearanceFox.IsChecked = settings.ShowFoxCompanion;
            AppearanceFoxRoaming.IsChecked = settings.FoxRoaming;
            AppearanceFoxActivity.SelectedIndex = Math.Clamp(settings.FoxActivity, 0, 2);
            AppearanceFoxRoaming.IsEnabled = AppearanceFoxHome.IsEnabled = settings.ShowFoxCompanion;
            AppearanceFoxActivity.IsEnabled = settings.ShowFoxCompanion && settings.FoxRoaming;
            AppearanceRoaming.IsChecked = settings.CompanionRoaming;
            AppearanceActivity.SelectedIndex = Math.Clamp(settings.CompanionActivity, 0, 2);
            AppearanceMotion.IsChecked = settings.AnimateInterface;
            AppearanceRoaming.IsEnabled = AppearanceHomeButton.IsEnabled = settings.ShowCompanion;
            AppearanceActivity.IsEnabled = settings.ShowCompanion && settings.CompanionRoaming;
        }
        finally { syncingAppearance = false; }
    }
    private async void ScreenChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (syncingAppearance || !loaded || sender is not RadioButton { Tag: string tag, IsChecked: true } || !int.TryParse(tag, out int selected)) return;
        settings = settings with { PlainScreen = selected == 3, Palette = selected == 3 ? settings.Palette : selected };
        ApplyTheme(); await SaveAppearance();
    }
    private async void AppearanceOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (syncingAppearance || !loaded || AppearanceMotion is null) return;
        settings = settings with { ShowCompanion = AppearanceCompanion.IsChecked == true,
            CompanionRoaming = AppearanceRoaming.IsChecked == true, CompanionActivity = Math.Clamp(AppearanceActivity.SelectedIndex, 0, 2),
            AnimateInterface = AppearanceMotion.IsChecked == true,
            ShowFoxCompanion = AppearanceFox.IsChecked == true, FoxRoaming = AppearanceFoxRoaming.IsChecked == true,
            FoxActivity = Math.Clamp(AppearanceFoxActivity.SelectedIndex, 0, 2) };
        UpdateCompanion(); SyncAppearance(); await SaveAppearance();
    }
    private async Task SaveAppearance()
    {
        try { await Save(CancellationToken.None, appearanceOnly: true); AppearanceStatus.Text = "화면 설정을 저장했습니다."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { AppearanceStatus.Text = "화면은 적용했지만 설정을 저장하지 못했습니다: " + error.Message; }
    }
    private async void HistoryOpenClicked(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, History) && e is MouseButtonEventArgs &&
            (e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(History, source) is null)) return;
        if (History.SelectedItem is not HistoryItem item || !History.IsEnabled || openingHistoryRequest == historyRequest) return;
        int request = ++historyRequest;
        openingHistoryRequest = request;
        OpenHistoryButton.IsEnabled = false; HistoryCount.Text = "기록을 읽고 있습니다…";
        try
        {
            var run = await SpeedFiles.Read<SpeedRun>(item.Path);
            if (request != historyRequest || closed) return;
            ShowRun(run); ShowWorkspacePage(0); Tabs.SelectedIndex = 2;
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException)
        { if (request == historyRequest) HistoryCount.Text = "기록을 읽지 못했습니다: " + error.Message; }
        finally
        {
            if (openingHistoryRequest == request)
            { openingHistoryRequest = null; OpenHistoryButton.IsEnabled = History.SelectedItem is not null; }
        }
    }
    private void ResolvePreparationClicked(object sender, RoutedEventArgs e)
    {
        ShowWorkspacePage(0); Tabs.SelectedIndex = 0;
        FrameworkElement? target = OptionFields.FirstOrDefault(b => b != StressScenarios &&
            b.Parent is FrameworkElement { IsEnabled: true } && FindName(b.Name + "Error") is TextBlock { Text.Length: > 0 });
        if (target is null)
        {
            try { ReadOptions(); } catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException)
            { target = S01.IsChecked == true ? StressScenarios : ExperimentSection; }
        }
        target ??= SelectedTargetCount is < 2 or > 8 ? ReleaseSection : EditorList;
        for (DependencyObject? parent = target; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is Expander expander) expander.IsExpanded = true;
        target.BringIntoView(); target.Focus();
    }
    private void HistoryListKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { HistoryOpenClicked(sender, e); e.Handled = true; } }
}
