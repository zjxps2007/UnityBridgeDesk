using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace UnityBridgeDesk.Desktop.Controls;

/// <summary>A single marker moves between native tab headers; input and layout never wait for it.</summary>
public static class DeskTabMotion
{
    private static readonly ConditionalWeakTable<TabControl, State> states = new();
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(DeskTabMotion),
        new PropertyMetadata(false, (d, e) =>
        {
            if (d is not TabControl tabs) return;
            if ((bool)e.NewValue) states.GetValue(tabs, t => new(t)).Connect();
            else if (states.TryGetValue(tabs, out var state)) state.Disconnect();
        }));
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    private sealed class State(TabControl tabs)
    {
        private bool connected, positioned;
        private FrameworkElement? marker;
        public void Connect()
        {
            if (connected) return;
            connected = true;
            tabs.Loaded += Loaded; tabs.Unloaded += Unloaded; tabs.SizeChanged += Sized;
            tabs.SelectionChanged += Selected; tabs.LayoutUpdated += LaidOut;
        }
        public void Disconnect()
        {
            if (!connected) return;
            connected = false;
            tabs.Loaded -= Loaded; tabs.Unloaded -= Unloaded; tabs.SizeChanged -= Sized;
            tabs.SelectionChanged -= Selected; tabs.LayoutUpdated -= LaidOut;
            if (marker is not null) DeskMotion.Reset(marker);
        }
        private void Loaded(object sender, RoutedEventArgs e) { tabs.LayoutUpdated -= LaidOut; tabs.LayoutUpdated += LaidOut; Update(false); }
        private void Unloaded(object sender, RoutedEventArgs e)
        { tabs.LayoutUpdated -= LaidOut; positioned = false; if (marker is not null) DeskMotion.Reset(marker); }
        private void LaidOut(object? sender, EventArgs e) => Update(false);
        private void Sized(object sender, SizeChangedEventArgs e) { positioned = false; Update(false); }
        private void Selected(object sender, SelectionChangedEventArgs e)
        { if (ReferenceEquals(e.OriginalSource, tabs)) Update(true); }
        private double destination = double.NaN;
        private void Update(bool animate)
        {
            if (tabs.Template?.FindName("PART_SelectionIndicator", tabs) is not FrameworkElement line ||
                tabs.ItemContainerGenerator.ContainerFromIndex(tabs.SelectedIndex) is not TabItem { ActualWidth: > 0 } item ||
                line.Parent is not FrameworkElement parent) return;
            if (!ReferenceEquals(marker, line)) { marker = line; positioned = false; }
            double x = item.TranslatePoint(new Point(item.ActualWidth / 2 - line.Width / 2, 0), parent).X;
            if (positioned && Math.Abs(destination - x) < .1) return;
            DeskMotion.MoveTo(line, x, animate && positioned);
            line.Visibility = Visibility.Visible; destination = x; positioned = true;
        }
    }
}
