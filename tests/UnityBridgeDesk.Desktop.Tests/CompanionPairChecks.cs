using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop.Tests;

internal static class CompanionPairChecks
{
    public static void Verify(string directory)
    {
        var root = new Grid { Width = 850, Height = 600, Background = new SolidColorBrush(Color.FromRgb(249, 247, 252)) };
        var content = new Canvas(); root.Children.Add(content);
        void Put(FrameworkElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); content.Children.Add(element); }
        Put(new TextBlock { Text = "아라 · 고양이                      아샤 · 여우", FontSize = 22, Foreground = Brushes.DarkSlateBlue }, 80, 38);
        var text = new TextBlock { Text = "측정할 작업 · 화면의 텍스트와 겹쳐 놓아도 아래로 떨어집니다.", Width = 700, FontSize = 16 };
        Put(text, 55, 260);
        var box = new TextBox { Width = 240, Height = 38, Text = "설정 입력칸", FontSize = 14 }; Put(box, 250, 330);
        var shelf = new Border { Width = 760, Height = 2, Background = Brushes.Plum };
        AshaSurface.SetEdge(shelf, AshaEdge.Top); Put(shelf, 35, 510);
        var home = new Border { Width = 100, Height = 100 }; Put(home, 90, 390);
        var layer = new Canvas(); root.Children.Add(layer);
        var cat = new DeskMascot { Width = 96, Height = 96 };
        var fox = new DeskMascot { Character = CompanionKind.Fox, Width = 96, Height = 96 };
        var catHost = new Border { Width = 96, Height = 96, Child = cat };
        var foxHost = new Border { Width = 96, Height = 96, Child = fox };
        layer.Children.Add(catHost); layer.Children.Add(foxHost);
        var window = new Window { Content = root, SizeToContent = SizeToContent.WidthAndHeight, Left = -22000, Top = -22000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        using var ara = new AshaCompanion(root, layer, catHost, home, cat, 27);
        using var asha = new AshaCompanion(root, layer, foxHost, home, fox, 65);
        ara.OtherCompanion = asha; asha.OtherCompanion = ara;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Place(AshaCompanion companion, Point p) => typeof(AshaCompanion).GetMethod("Place", flags)!.Invoke(companion, [p]);
        void Release(AshaCompanion companion, DeskMascot mascot, Point p)
        {
            Place(companion, p); typeof(AshaCompanion).GetField("dragging", flags)!.SetValue(companion, true);
            typeof(AshaCompanion).GetMethod("Released", flags)!.Invoke(companion,
                [mascot, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent }]);
        }
        void Render(string file)
        {
            window.UpdateLayout(); var image = new RenderTargetBitmap(1275, 900, 144, 144, PixelFormats.Pbgra32); image.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(Path.Combine(directory, file)); png.Save(output);
        }
        try
        {
            window.Show(); window.UpdateLayout(); Render("companion-pair-initial.png"); Pump(40);
            ara.RefreshGeometry(); asha.RefreshGeometry();
            Assert.IsTrue(ara.IsStanding); Assert.IsTrue(asha.IsStanding);
            Assert.AreNotEqual(ara.Position, asha.Position, "Two characters should not be summoned onto the same spot.");
            Assert.Contains("아라", AutomationProperties.GetName(cat)); Assert.Contains("아샤", AutomationProperties.GetName(fox));
            Place(ara, new(110, 507 - 96 * .89)); Place(asha, new(520, 507 - 96 * .89));
            Render("ara-asha-pair.png");
            // One character leaving a chart cannot clear the other's visit state.
            var chart = new SpeedChartView(); chart.SetCompanionVisiting(true, ara); chart.SetCompanionVisiting(true, asha);
            chart.SetCompanionVisiting(false, ara); Assert.IsTrue(chart.CompanionVisiting);
            chart.SetCompanionVisiting(false, asha); Assert.IsFalse(chart.CompanionVisiting);
            if (SystemParameters.ClientAreaAnimation)
            {
                ara.Configure(true, false, 1); asha.Configure(true, false, 1);
                Assert.IsTrue(cat.React()); Assert.AreEqual(AshaAction.Glance, cat.Action);
                Assert.IsTrue(fox.React()); Assert.AreEqual(AshaAction.Sniff, fox.Action);
                var release = new Point(290, 300); Release(asha, fox, release);
                Assert.IsFalse(asha.IsPositionSafe, "Fixture starts overlapping the painted input.");
                Assert.IsTrue(asha.IsFalling); Assert.AreEqual(release, asha.Position);
                var catBefore = ara.Position;
                Pump(100); Assert.IsGreaterThan(release.Y, asha.Position.Y);
                var watch = Stopwatch.StartNew();
                while (asha.IsFalling && watch.ElapsedMilliseconds < 2400) Pump(20);
                Assert.IsFalse(asha.IsFalling); Assert.IsTrue(asha.IsStanding); Assert.AreEqual(release.X, asha.Position.X);
                Assert.AreEqual(catBefore, ara.Position, "Dropping the fox must not move the cat.");
                Render("fox-overlap-landing.png");
                Release(asha, fox, new(600, 140)); asha.Configure(false, false, 1);
                Assert.IsFalse(asha.IsRunning); Assert.IsFalse(asha.IsFalling); Assert.IsTrue(asha.IsStanding);
                catHost.Visibility = Visibility.Collapsed;
                Assert.IsFalse(ara.IsRunning); Assert.IsTrue(foxHost.IsVisible);
                asha.Configure(true, true, 2); Assert.IsTrue(asha.IsRunning);
                ara.Configure(false, false, 1); asha.Configure(false, true, 2);
                Assert.IsFalse(ara.IsRunning); Assert.IsFalse(asha.IsRunning);
            }
        }
        finally { ara.Dispose(); asha.Dispose(); window.Close(); cat.DisposeMotion(); fox.DisposeMotion(); }
    }
    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(3);
        }
    }
}
