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

internal static class AshaCompanionChecks
{
    public static void Verify(string directory)
    {
        var surface = new Grid { Width = 800, Height = 600, Background = Brushes.WhiteSmoke };
        var content = new Canvas(); surface.Children.Add(content);
        var obstacle = new Button { Content = "조작 영역", Width = 90, Height = 90 };
        Canvas.SetLeft(obstacle, 230); Canvas.SetTop(obstacle, 120); content.Children.Add(obstacle);
        var floor = new Border { Width = 780, Height = 1, Background = Brushes.SlateGray };
        Canvas.SetLeft(floor, 10); Canvas.SetTop(floor, 500); content.Children.Add(floor); AshaSurface.SetEdge(floor, AshaEdge.Top);
        var middle = new Border { Width = 220, Height = 1, Background = Brushes.SlateGray };
        Canvas.SetLeft(middle, 230); Canvas.SetTop(middle, 355); content.Children.Add(middle); AshaSurface.SetEdge(middle, AshaEdge.Top);
        var title = new TextBlock { Text = "아샤의 높은 자리", FontSize = 24, Foreground = Brushes.DarkSlateBlue };
        Canvas.SetLeft(title, 435); Canvas.SetTop(title, 205); content.Children.Add(title); AshaSurface.SetEdge(title, AshaEdge.Top);
        var home = new Border { Width = 104, Height = 104 };
        Canvas.SetLeft(home, 20); Canvas.SetTop(home, 396); content.Children.Add(home); AshaSurface.SetEdge(home, AshaEdge.Bottom);
        var layer = new Canvas { ClipToBounds = true }; surface.Children.Add(layer);
        var mascot = new DeskMascot { Width = 96, Height = 96 };
        var host = new Border { Width = 96, Height = 96, Child = mascot }; layer.Children.Add(host);
        var window = new Window { Content = surface, SizeToContent = SizeToContent.WidthAndHeight,
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        using var asha = new AshaCompanion(surface, layer, host, home, mascot, seed: 7);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void DecideNow() => typeof(AshaCompanion).GetField("nextDecision", flags)!.SetValue(asha, -1d);
        try
        {
            window.Show(); window.UpdateLayout(); asha.RefreshGeometry();
            Assert.IsTrue(asha.IsPositionSafe); Assert.IsTrue(asha.IsStanding);
            Assert.IsNull(layer.Background, "The companion layer passes input through empty areas.");
            asha.Configure(true, true, 1);
            Assert.AreEqual(SystemParameters.ClientAreaAnimation, asha.IsRunning);
            if (SystemParameters.ClientAreaAnimation)
            {
                Point before = asha.Position; Pump(150); Assert.AreEqual(before, asha.Position, "Startup gives the reader a moment before exploration.");
                Assert.IsTrue(asha.WalkTo(new Point(180, before.Y)));
                var frames = new HashSet<int>();
                for (int i = 0; i < 24; i++) { Pump(55); frames.Add(mascot.CurrentFrame); Assert.IsTrue(asha.IsPositionSafe); }
                Assert.IsGreaterThan(20d, (asha.Position - before).Length);
                Assert.IsTrue(new[] { 0, 1, 2, 3 }.All(frames.Contains));
                surface.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Point readingPosition = asha.Position; Pump(100); Assert.AreEqual(readingPosition, asha.Position);
                Assert.IsTrue(asha.IsStanding); Assert.AreEqual(AshaAction.Idle, asha.Action);
                Assert.AreEqual(1d, ((ScaleTransform)mascot.FindName("BodyScale")).ScaleY);

                DecideNow(); Pump(80);
                Assert.IsNotNull(asha.Destination, "Autonomy must choose its own destination without a move command.");
                Assert.IsGreaterThan(100d, (asha.Destination!.Value - asha.Position).Length, "Explore a different neighbourhood instead of choosing an adjacent sample.");
                var actions = new HashSet<AshaAction>(); var watch = Stopwatch.StartNew(); bool captured = false;
                while (asha.Destination is not null && watch.Elapsed < TimeSpan.FromSeconds(15))
                {
                    Pump(35); actions.Add(asha.Action); Assert.IsTrue(asha.IsPositionSafe);
                    if (asha.Action == AshaAction.Jump && !captured) { Capture(surface, Path.Combine(directory, "asha-jump.png")); captured = true; }
                }
                Assert.IsNull(asha.Destination); Pump(260);
                Assert.IsTrue(asha.IsStanding);
                Assert.Contains(AshaAction.Look, actions);
                Capture(surface, Path.Combine(directory, "asha-perched.png"));

                // No injected destination or decision time: it must descend and set off again on its own.
                watch.Restart(); var continuedActions = new HashSet<AshaAction>(); int arrivals = 0; bool travelling = false;
                while ((arrivals < 3 || !continuedActions.Contains(AshaAction.Drop) || !continuedActions.Contains(AshaAction.Jump)) && watch.Elapsed < TimeSpan.FromSeconds(35))
                {
                    Pump(30); continuedActions.Add(asha.Action); Assert.IsTrue(asha.IsPositionSafe);
                    if (asha.Destination is not null) travelling = true;
                    else if (travelling) { travelling = false; arrivals++; Assert.IsTrue(asha.IsStanding); }
                    Assert.AreNotEqual(AshaAction.Sleep, asha.Action, "Autonomous exploration must not enter a long sleep.");
                }
                Assert.IsGreaterThanOrEqualTo(3, arrivals, "Landing must lead to another journey without manual commands.");
                Assert.Contains(AshaAction.Drop, continuedActions); Assert.Contains(AshaAction.Jump, continuedActions); Assert.Contains(AshaAction.Peek, continuedActions);
                Assert.Contains(AshaAction.Crouch, continuedActions); Assert.Contains(AshaAction.Land, continuedActions);
                // Autonomy was verified above. Use a known drop for the separate input-interruption check.
                asha.Configure(false, false, 1);
                var map = (AshaMap)typeof(AshaCompanion).GetField("map", flags)!.GetValue(asha)!;
                var dropStart = map.Positions.First(p => map.Positions.Any(q => q.Y > p.Y + 30 && map.Route(p, q).Any(s => s.IsDrop)));
                var dropEnd = map.Positions.First(q => q.Y > dropStart.Y + 30 && map.Route(dropStart, q).Any(s => s.IsDrop));
                typeof(AshaCompanion).GetMethod("Place", flags)!.Invoke(asha, [dropStart]); asha.RefreshGeometry();
                asha.Configure(true, false, 1); Assert.IsTrue(asha.WalkTo(dropEnd));
                watch.Restart();
                while (asha.Action != AshaAction.Drop && watch.Elapsed < TimeSpan.FromSeconds(8)) Pump(25);
                Assert.AreEqual(AshaAction.Drop, asha.Action);
                Capture(surface, Path.Combine(directory, "asha-drop.png"));
                surface.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Pump(1500); Assert.IsTrue(asha.IsStanding, "Input during a drop must finish on a supported ledge."); Assert.IsNull(asha.Destination);
                Assert.IsTrue(mascot.React()); Assert.AreEqual(AshaAction.Glance, mascot.Action);
                asha.Configure(false, true, 1); var paused = asha.Position; Pump(100);
                Assert.AreEqual(paused, asha.Position); Assert.IsFalse(asha.IsRunning); Assert.IsFalse(mascot.IsMoving);
                asha.ReturnHome(); Assert.IsTrue(asha.IsStanding, "Returning home from a paused settings popup must use a supported position.");
                Assert.IsFalse(asha.IsRunning); Assert.IsNull(asha.Destination);
                var remembered = asha.Position; mascot.RestoreFacing(true);
                var originalExplorer = typeof(AshaCompanion).GetField("explorer", flags)!.GetValue(asha);
                asha.SetPage("history"); window.UpdateLayout(); asha.RefreshGeometry();
                Assert.IsTrue(asha.IsStanding); Assert.AreNotSame(originalExplorer, typeof(AshaCompanion).GetField("explorer", flags)!.GetValue(asha));
                var elsewhere = asha.StandingPositions.MaxBy(p => (p - remembered).LengthSquared);
                typeof(AshaCompanion).GetMethod("Place", flags)!.Invoke(asha, [elsewhere]); asha.RefreshGeometry();
                asha.SetPage("work:0"); window.UpdateLayout(); asha.RefreshGeometry();
                Assert.AreEqual(remembered, asha.Position); Assert.IsTrue(mascot.FacesLeft);
                Assert.AreSame(originalExplorer, typeof(AshaCompanion).GetField("explorer", flags)!.GetValue(asha));
                asha.SetPage("history"); window.UpdateLayout(); asha.RefreshGeometry(); Assert.AreEqual(elsewhere, asha.Position);
                asha.SetPage("work:0"); window.UpdateLayout(); asha.RefreshGeometry();
                asha.Configure(true, false, 1);
                typeof(AshaCompanion).GetField("dragging", flags)!.SetValue(asha, true);
                typeof(AshaCompanion).GetMethod("Released", flags)!.Invoke(asha, [mascot, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent }]);
                Assert.AreEqual(AshaAction.Land, mascot.Action); Pump(300); Assert.AreEqual(AshaAction.Groom, mascot.Action);
                Assert.IsTrue(asha.IsStanding); Pump(600); Assert.AreEqual(AshaAction.Idle, mascot.Action);
                Assert.IsTrue(mascot.React()); Assert.AreEqual(AshaAction.Glance, mascot.Action, "Setting down must not count as a playful tap.");
                asha.Configure(true, true, 1); layer.Visibility = Visibility.Collapsed; Pump(50); Assert.IsFalse(asha.IsRunning);
                layer.Visibility = Visibility.Visible; Pump(50); Assert.IsTrue(asha.IsRunning);
                asha.Configure(true, false, 0); DecideNow(); var resting = asha.Position; Pump(100); Assert.AreEqual(resting, asha.Position);
                middle.Visibility = Visibility.Collapsed; window.UpdateLayout(); asha.RefreshGeometry();
                Pump(1400); Assert.IsTrue(asha.IsStanding, "A removed ledge requires falling or recovery to a supported position, even while resting.");
                surface.Width = 520; window.UpdateLayout(); asha.RefreshGeometry(); Pump(1400); Assert.IsTrue(asha.IsStanding);
                // An unsupported placement must fall or recover to a real surface.
                typeof(AshaCompanion).GetMethod("Place", flags)!.Invoke(asha, [new Point(340, 250)]);
                asha.RefreshGeometry(); Pump(1400); Assert.IsTrue(asha.IsStanding); Assert.AreNotEqual(new Point(340, 250), asha.Position);
                Canvas.SetLeft(obstacle, 0); Canvas.SetTop(obstacle, 0); obstacle.Width = 800; obstacle.Height = 600;
                window.UpdateLayout(); asha.RefreshGeometry(); Pump(1400);
                Assert.AreEqual(0d, host.Opacity); Assert.IsFalse(host.IsHitTestVisible); Assert.IsFalse(mascot.IsMoving);
                obstacle.Width = 90; obstacle.Height = 90; Canvas.SetLeft(obstacle, 230); Canvas.SetTop(obstacle, 120);
                window.UpdateLayout(); asha.RefreshGeometry();
                // Recovery starts the fade on a dispatcher tick. Wait for its actual
                // completion rather than assuming that tick ran at the start of 180ms.
                var recovered = Stopwatch.StartNew();
                while ((!asha.IsStanding || host.Opacity != 1) && recovered.Elapsed < TimeSpan.FromSeconds(2)) Pump(20);
                Assert.IsTrue(asha.IsStanding); Assert.AreEqual(1d, host.Opacity);
            }
        }
        finally { asha.Dispose(); window.Close(); mascot.DisposeMotion(); }
        Assert.IsFalse(asha.IsRunning); Assert.IsFalse(mascot.IsMoving);
    }
    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(4);
        }
    }
    private static void Capture(FrameworkElement target, string file)
    {
        var bitmap = new RenderTargetBitmap((int)target.ActualWidth, (int)target.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(target);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(file); encoder.Save(stream);
    }
}
