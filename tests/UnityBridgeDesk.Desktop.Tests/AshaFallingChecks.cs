using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop.Tests;

internal static class AshaFallingChecks
{
    public static void Verify(string directory)
    {
        var root = new Grid { Width = 820, Height = 680, Background = Brushes.White };
        var content = new Canvas(); root.Children.Add(content);
        void Put(FrameworkElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); content.Children.Add(element); }
        var label = new TextBlock { Text = "F01 작은 요청", FontSize = 16, TextWrapping = TextWrapping.Wrap };
        var choice = new CheckBox { Name = "ExperimentChoice", Width = 620, Content = label };
        Put(choice, 35, 230);
        var radio = new RadioButton { Name = "RadioChoice", Width = 620, Content = "선택 옵션" }; Put(radio, 35, 330);
        var flat = new Button { Name = "FlatAction", Width = 380, Content = "글자 버튼", Background = Brushes.Transparent, BorderBrush = Brushes.Transparent };
        Put(flat, 20, 440);
        var expander = new Expander { Name = "Details", Width = 380, Header = "더 보기" }; Put(expander, 20, 520);
        var shelf = new Border { Name = "Shelf", Width = 240, Height = 1, Background = Brushes.MediumPurple };
        AshaSurface.SetEdge(shelf, AshaEdge.Top); Put(shelf, 480, 430);
        var floor = new Border { Name = "Floor", Width = 780, Height = 1, Background = Brushes.Gray };
        AshaSurface.SetEdge(floor, AshaEdge.Top); Put(floor, 15, 610);
        var home = new Border { Width = 100, Height = 100 }; Put(home, 30, 480);
        var layer = new Canvas { ClipToBounds = true }; root.Children.Add(layer);
        var mascot = new DeskMascot { Width = 96, Height = 96 };
        var host = new Border { Width = 96, Height = 96, Child = mascot }; layer.Children.Add(host);
        var window = new Window { Content = root, SizeToContent = SizeToContent.WidthAndHeight, Left = -22000, Top = -22000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        using var asha = new AshaCompanion(root, layer, host, home, mascot, seed: 17);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        AshaMap Map() => (AshaMap)typeof(AshaCompanion).GetField("map", flags)!.GetValue(asha)!;
        AshaLedge[] Ledges() => (AshaLedge[])typeof(AshaCompanion).GetField("oldLedges", flags)!.GetValue(asha)!;
        void Render(string? file = null)
        {
            window.UpdateLayout(); var image = new RenderTargetBitmap(820, 680, 96, 96, PixelFormats.Pbgra32); image.Render(root);
            if (file is null) return;
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(directory, file)); encoder.Save(stream);
        }
        void Release(Point point, bool lost = false)
        {
            typeof(AshaCompanion).GetMethod("StopRoute", flags)!.Invoke(asha, null);
            typeof(AshaCompanion).GetMethod("Place", flags)!.Invoke(asha, [point]);
            typeof(AshaCompanion).GetField("dragging", flags)!.SetValue(asha, true);
            typeof(AshaCompanion).GetMethod(lost ? "LostCapture" : "Released", flags)!.Invoke(asha,
                [mascot, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent }]);
        }
        void WaitForLanding()
        {
            var watch = Stopwatch.StartNew();
            while (asha.IsFalling && watch.ElapsedMilliseconds < 2500) { Pump(20); Assert.IsTrue(asha.IsPositionSafe); }
            Assert.IsFalse(asha.IsFalling); Assert.IsTrue(asha.IsStanding);
        }
        try
        {
            window.Show(); Render(); Pump(40); asha.RefreshGeometry();
            Assert.IsFalse(Ledges().Any(l => l.Id == choice.Name || l.Id == radio.Name), "A stretched choice hit target is not painted ground.");
            Assert.IsFalse(Ledges().Any(l => l.Id == flat.Name || l.Id.StartsWith("Details") && l.Right - l.Left > 350), "Transparent buttons and expander headers must not add empty-row floors either.");
            Assert.IsFalse(Ledges().Any(l => l.Y > 200 && l.Y < 400 && l.Right > 450), "Empty space to the right of labels must remain air.");
            Assert.IsTrue(Ledges().Any(l => l.Kind == AshaLedgeKind.TextLine && l.Id.StartsWith(choice.Name)), string.Join(" | ", Ledges()));
            var hit = root.InputHitTest(choice.TranslatePoint(new Point(550, 10), root));
            Assert.IsTrue(ReferenceEquals(hit, choice) || hit is DependencyObject d && choice.IsAncestorOf(d), "The existing large click target must still work.");
            choice.Width = 175; label.Text = "F01 작은 요청과 줄바꿈되는 명령 평균을 표시한다."; Render(); Pump(30); asha.RefreshGeometry();
            Assert.IsGreaterThan(1, AshaTextLayout.Read(label).Count);
            Assert.IsFalse(Ledges().Any(l => l.Id.StartsWith(choice.Name) && l.Right > 212));
            choice.IsEnabled = false; Render(); asha.RefreshGeometry();
            Assert.IsFalse(Ledges().Any(l => l.Id.StartsWith(choice.Name)), "Disabled choices cannot become destinations through their child text.");
            choice.IsEnabled = true;

            if (SystemParameters.ClientAreaAnimation)
            {
                asha.Configure(true, false, 1);
                var release = new Point(550, 80); Release(release);
                Assert.IsTrue(asha.IsFalling); Assert.AreEqual(release, asha.Position, "Release must not snap to the nearest platform.");
                Assert.AreEqual(AshaAction.Drop, asha.Action); Pump(150);
                Assert.IsGreaterThan(release.Y, asha.Position.Y); Assert.AreEqual(release.X, asha.Position.X);
                Render("asha-free-fall.png"); WaitForLanding();
                Assert.AreEqual("Shelf", Map().Support(asha.Position)?.Id); Assert.AreEqual(AshaAction.Land, asha.Action);
                Render("asha-free-fall-landed.png");

                Release(release, lost: true); Assert.IsTrue(asha.IsFalling);
                shelf.Visibility = Visibility.Collapsed; Render(); asha.RefreshGeometry();
                root.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Assert.IsTrue(asha.IsFalling, "Scrolling delays exploration but must not strand a fall.");
                WaitForLanding(); Assert.AreEqual("Floor", Map().Support(asha.Position)?.Id);

                Release(release); Pump(80); asha.Configure(false, false, 1);
                Assert.IsFalse(asha.IsFalling); Assert.IsFalse(asha.IsRunning); Assert.IsTrue(asha.IsStanding);
                var paused = asha.Position; Pump(80); Assert.AreEqual(paused, asha.Position);
                asha.Configure(true, false, 1); Release(release);
                // No ledge under this x: reach the viewport boundary, then safely reappear on a real ledge.
                floor.Width = 230; Render(); asha.RefreshGeometry(); WaitForLanding();
                Assert.AreEqual("Floor", Map().Support(asha.Position)?.Id); Assert.IsLessThan(250d, asha.Position.X);
                floor.Visibility = Visibility.Collapsed; choice.Visibility = Visibility.Collapsed; radio.Visibility = Visibility.Collapsed;
                flat.Visibility = Visibility.Collapsed; expander.Visibility = Visibility.Collapsed;
                Render(); asha.RefreshGeometry(); Pump(1600);
                Assert.AreEqual(0d, host.Opacity); Assert.IsFalse(asha.IsStanding); Assert.IsFalse(host.IsHitTestVisible);
                Pump(200); Assert.AreEqual(0d, host.Opacity, "An empty layout cannot repeatedly restart a completed fall.");
                floor.Visibility = Visibility.Visible; Render(); Pump(200);
                Assert.AreEqual(1d, host.Opacity); Assert.IsTrue(asha.IsStanding, "New visible ground wakes a hidden companion without an explicit refresh.");
            }
        }
        finally { asha.Dispose(); window.Close(); mascot.DisposeMotion(); }
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
