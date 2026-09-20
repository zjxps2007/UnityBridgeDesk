using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop.Tests;

internal static class SettingsPopupChecks
{
    public static void Verify(SpeedBenchWindow window, FrameworkElement content, string directory)
    {
        T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Invoke(string name, params object[] args) => typeof(SpeedBenchWindow).GetMethod(name, flags)!.Invoke(window, args);
        void Open() => Find<Button>("ManagementMenuButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Escape() => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        var tabs = Find<TabControl>("Tabs");
        var experiment = Find<ListBox>("ChartExperiment").SelectedItem;
        var condition = Find<ListBox>("ChartCondition").SelectedItem;
        var trial = Find<ListBox>("TrialIndex").SelectedItem;
        var query = Find<TextBox>("TrialSearch").Text;
        var scroll = Find<ScrollViewer>("ResultDetailsScroll");
        var offset = scroll.VerticalOffset;
        var initialTab = tabs.SelectedIndex;
        var workspace = Find<Grid>("WorkspaceSurface");
        var overlay = Find<Grid>("SettingsOverlay");
        var dialog = Find<Border>("SettingsDialog");
        var close = Find<Button>("SettingsCloseButton");
        var categories = Find<TabControl>("SettingsTabs");
        var asha = (AshaCompanion)typeof(SpeedBenchWindow).GetField("asha", flags)!.GetValue(window)!;
        Open(); content.UpdateLayout();
        Assert.AreEqual(Visibility.Visible, overlay.Visibility);
        Assert.IsFalse(workspace.IsEnabled);
        Assert.IsFalse(asha.IsRunning);
        Assert.IsFalse(Find<DeskMascot>("Mascot").MotionEnabled);
        Assert.AreEqual(Visibility.Visible, tabs.Visibility);
        Assert.AreEqual(offset, scroll.VerticalOffset);
        Assert.AreEqual(initialTab, tabs.SelectedIndex);
        Assert.AreEqual(query, Find<TextBox>("TrialSearch").Text);
        Assert.AreSame(trial, Find<ListBox>("TrialIndex").SelectedItem);
        close.Focus();
        if (Keyboard.FocusedElement is DependencyObject)
        {
            for (int i = 0; i < 18; i++)
            {
                (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(i < 9 ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous));
                Assert.IsTrue(Keyboard.FocusedElement is DependencyObject focused && dialog.IsAncestorOf(focused), "Keyboard focus must stay inside settings.");
            }
        }
        categories.SelectedIndex = 1; content.UpdateLayout();
        var activity = Find<ComboBox>("AppearanceActivity"); activity.IsDropDownOpen = true;
        Escape(); Assert.IsFalse(activity.IsDropDownOpen);
        Assert.AreEqual(Visibility.Visible, overlay.Visibility, "First Escape closes the activity dropdown, not the settings popup.");
        Escape(); Assert.AreEqual(Visibility.Collapsed, overlay.Visibility);
        Assert.IsTrue(workspace.IsEnabled);
        Assert.AreSame(experiment, Find<ListBox>("ChartExperiment").SelectedItem);
        Assert.AreSame(condition, Find<ListBox>("ChartCondition").SelectedItem);
        Assert.AreSame(trial, Find<ListBox>("TrialIndex").SelectedItem);
        // The modal may close while a benchmark is active; decoration must remain suspended.
        typeof(SpeedBenchWindow).GetField("benchmarkActive", flags)!.SetValue(window, true);
        Open(); close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsFalse(asha.IsRunning); Assert.AreEqual(Visibility.Collapsed, Find<System.Windows.Controls.Canvas>("CompanionLayer").Visibility);
        typeof(SpeedBenchWindow).GetField("benchmarkActive", flags)!.SetValue(window, false); Invoke("UpdateMotion");
        Open();
        double savedWidth = window.Width, savedHeight = window.Height;
        foreach (var size in new[] { new Size(960, 660), new Size(1440, 850) })
        {
            window.Width = size.Width; window.Height = size.Height; window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            content.UpdateLayout();
            var origin = dialog.TranslatePoint(new(), content);
            Assert.IsTrue(origin.X >= 23 && origin.Y >= 23);
            Assert.IsTrue(origin.X + dialog.ActualWidth <= content.ActualWidth - 23 && origin.Y + dialog.ActualHeight <= content.ActualHeight - 23,
                $"Dialog {origin} {dialog.ActualWidth}x{dialog.ActualHeight}; viewport {content.ActualWidth}x{content.ActualHeight}");
            for (int category = 0; category < 3; category++)
            {
                categories.SelectedIndex = category; content.UpdateLayout();
                foreach (double scale in new[] { 1d, 1.5d, 2d })
                {
                    var image = new RenderTargetBitmap((int)(content.ActualWidth * scale), (int)(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    image.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var file = File.Create(Path.Combine(directory, $"settings-{size.Width}-{category}-{scale}.png")); encoder.Save(file);
                }
                Assert.IsGreaterThanOrEqualTo(32d, close.ActualHeight);
                Assert.IsTrue(close.TranslatePoint(new(), dialog).Y >= 0);
            }
            categories.SelectedIndex = 0; content.UpdateLayout();
            var screenScroll = Find<ScrollViewer>("ScreenSettingsScroll");
            screenScroll.ScrollToBottom(); content.UpdateLayout();
            var motion = Find<CheckBox>("AppearanceMotion");
            var point = motion.TranslatePoint(new(), screenScroll);
            Assert.IsTrue(point.Y >= 0 && point.Y + motion.ActualHeight <= screenScroll.ActualHeight, "Bottom setting must remain reachable through scrolling.");
        }
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(Visibility.Collapsed, overlay.Visibility);
        window.Width = savedWidth; window.Height = savedHeight;
        content.Measure(new(1230, 800)); content.Arrange(new(0, 0, 1230, 800)); content.UpdateLayout();
    }
}
