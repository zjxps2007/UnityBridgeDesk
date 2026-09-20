using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop.Tests;

internal static class MascotMotionChecks
{
    public static void Verify(string directory)
    {
        CharacterScaleChecks.Verify(directory);
        FoxWalkChecks.Verify(directory);
        SmoothMotionChecks.Verify(directory, true);
        var mascot = new DeskMascot { Width = 140, Height = 136 };
        var button = new Button { Content = "벤치 시작", Width = 130, Height = 38 };
        var panel = new StackPanel(); panel.Children.Add(mascot); panel.Children.Add(button);
        var host = new Window { Content = panel, Width = 220, Height = 240, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show(); host.UpdateLayout();
            Assert.IsFalse(mascot.React());
            mascot.MotionEnabled = true;
            Assert.AreEqual(SystemParameters.ClientAreaAnimation, mascot.IsMoving);
            Assert.AreEqual(SystemParameters.ClientAreaAnimation, mascot.IsBlinkTimerRunning);
            Assert.AreEqual(SystemParameters.ClientAreaAnimation, mascot.React());
            Pump(140); Capture(mascot, Path.Combine(directory, "mascot-reaction.png"));
            if (SystemParameters.ClientAreaAnimation)
            {
                mascot.SetAction(AshaAction.Peek, false, .8); Pump(150);
                var sprite = (AshaSprite)mascot.FindName("Sprite");
                Assert.IsGreaterThan(0d, sprite.Expression.GazeY); Capture(mascot, Path.Combine(directory, "asha-peek-detail.png"));
                mascot.SetAction(AshaAction.Land, false, .15);
                Pump(70); double shallow = ((ScaleTransform)mascot.FindName("BodyScale")).ScaleY;
                mascot.SetAction(AshaAction.Idle); Pump(280); mascot.SetAction(AshaAction.Land, true, .9); Pump(70);
                Assert.IsLessThan(shallow, ((ScaleTransform)mascot.FindName("BodyScale")).ScaleY);
                Assert.AreEqual(-1d, ((ScaleTransform)mascot.FindName("Facing")).ScaleX);
                Pump(350); mascot.SetAction(AshaAction.Peek, true, .7); Pump(250);
                var beforeIdle = sprite.Expression; double beforeTurn = ((RotateTransform)mascot.FindName("BodyTurn")).Angle;
                mascot.SetAction(AshaAction.Idle);
                Assert.AreEqual(beforeIdle.GazeY, sprite.Expression.GazeY, .001, "The idle transition begins at the displayed pose.");
                Assert.AreEqual(beforeIdle.Tail, sprite.Expression.Tail, .001);
                Assert.AreEqual(beforeTurn, ((RotateTransform)mascot.FindName("BodyTurn")).Angle, .01);
                Pump(290); string firstIdle = Fingerprint(mascot);
                for (int i = 0; i < 8; i++)
                {
                    Pump(100);
                    Assert.AreEqual(AshaAction.Idle, mascot.Action);
                    Assert.AreEqual(1d, ((ScaleTransform)mascot.FindName("BodyScale")).ScaleY);
                    Assert.AreEqual(1d, ((ScaleTransform)mascot.FindName("BodyScale")).ScaleX);
                    Assert.AreEqual(0d, ((TranslateTransform)mascot.FindName("BodyLift")).Y);
                }
                Assert.AreNotEqual(firstIdle, Fingerprint(mascot), "Idle must visibly update while its position and body scale remain fixed.");
                Capture(mascot, Path.Combine(directory, "asha-idle-detail.png"));
                Assert.IsTrue(mascot.FacesLeft);
                mascot.TurnToward(false); Assert.AreEqual(AshaAction.Turn, mascot.Action);
                Assert.IsTrue(mascot.FacesLeft, "Eyes lead before the body changes orientation.");
                Pump(120); Assert.IsTrue(mascot.FacesLeft); Assert.IsLessThan(0d, sprite.Expression.GazeX);
                Capture(mascot, Path.Combine(directory, "asha-turn-eyes.png"));
                Pump(260); Assert.IsFalse(mascot.FacesLeft); Assert.AreEqual(AshaAction.Idle, mascot.Action);
                mascot.LookToward(-1, -.6); mascot.SetAction(AshaAction.Look); Pump(200);
                Assert.IsLessThan(0d, sprite.Expression.GazeY); Assert.IsFalse(mascot.FacesLeft, "Looking does not reset orientation.");
                mascot.MotionEnabled = false; mascot.MotionEnabled = true;
                Assert.IsTrue(mascot.React()); Assert.AreEqual(AshaAction.Glance, mascot.Action); Pump(280);
                Assert.IsTrue(mascot.React()); Assert.AreEqual(AshaAction.Play, mascot.Action); Pump(280);
                Assert.IsTrue(mascot.React()); Assert.AreEqual(AshaAction.Dismiss, mascot.Action); Assert.IsFalse(mascot.React());
                Capture(mascot, Path.Combine(directory, "asha-dismiss.png"));
            }
            mascot.MotionEnabled = false;
            Assert.IsFalse(mascot.IsMoving); Assert.IsFalse(mascot.IsBlinkTimerRunning); Assert.IsFalse(mascot.React());
            Assert.AreEqual(4, mascot.CurrentFrame, "Paused Asha must use the still frame.");
            Assert.AreEqual(default(AshaExpression), ((AshaSprite)mascot.FindName("Sprite")).Expression);
            foreach (string name in new[] { "BodyScale", "BodyLift", "BodyTurn", "Facing" })
                Assert.IsFalse(((Animatable)mascot.FindName(name)).HasAnimatedProperties, name + " must release its clocks when paused.");
            Capture(mascot, Path.Combine(directory, "mascot-rest.png"));
            mascot.MotionEnabled = true; mascot.Visibility = Visibility.Collapsed;
            Assert.IsFalse(mascot.IsMoving); Assert.IsFalse(mascot.IsBlinkTimerRunning);
            mascot.Visibility = Visibility.Visible;
            Assert.AreEqual(SystemParameters.ClientAreaAnimation, mascot.IsMoving);

            DeskMotion.SetAllowed(host, true);
            var originalSize = button.RenderSize; var originalPosition = button.TranslatePoint(new(), panel);
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            Pump(150);
            Assert.AreEqual(originalSize, button.RenderSize);
            Assert.AreEqual(originalPosition, button.TranslatePoint(new(), panel));
            button.IsEnabled = false; Assert.AreEqual((double)button.GetAnimationBaseValue(UIElement.OpacityProperty), button.Opacity);
            button.IsEnabled = true;
            DeskMotion.Reveal(button); DeskMotion.SetAllowed(host, false);
            Assert.AreEqual((double)button.GetAnimationBaseValue(UIElement.OpacityProperty), button.Opacity);
            Assert.AreEqual(originalPosition, button.TranslatePoint(new(), panel), "Pausing must immediately remove the transition offset.");
            DeskMotion.SetAllowed(host, true);
            for (int i = 0; i < 10; i++) DeskMotion.Reveal(button);
            PumpUntil(() => button.TranslatePoint(new(), panel) == originalPosition && button.Opacity == (double)button.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.AreEqual(originalPosition, button.TranslatePoint(new(), panel), "Rapid navigation must settle without queued movement.");
            Assert.AreEqual((double)button.GetAnimationBaseValue(UIElement.OpacityProperty), button.Opacity);
            DeskMotion.Reveal(button);
            for (int i = 0; i < 16; i++)
            {
                Pump(10);
                Assert.AreEqual(originalPosition, button.TranslatePoint(new(), panel), "Page entry must never move reading or hit positions.");
                Assert.IsTrue(button.Opacity >= .96 && button.Opacity <= 1, "Only a restrained page fade is allowed.");
            }
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Pump(110);
            Assert.AreEqual(originalPosition, button.TranslatePoint(new(), panel), "Press feedback must not shift or shrink buttons.");
            Assert.AreEqual(new Point(button.ActualWidth, button.ActualHeight) + (Vector)originalPosition,
                button.TranslatePoint(new(button.ActualWidth, button.ActualHeight), panel));
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
            DeskMotion.SetAllowed(host, false); DeskMotion.SetAllowed(host, true);
            VerifyControlMotion(host, panel, directory);
        }
        finally { host.Close(); mascot.DisposeMotion(); }
        Assert.IsFalse(mascot.IsBlinkTimerRunning); Assert.IsFalse(mascot.IsMoving);
    }
    private static void VerifyControlMotion(Window host, StackPanel panel, string directory)
    {
        host.Width = 480; host.Height = 430;
        var hover = new Border { Width = 60, Height = 20, Background = Brushes.Plum };
        var arrow = new Border { Width = 10, Height = 10, Background = Brushes.Purple, RenderTransformOrigin = new(.5, .5) };
        var tabs = new TabControl { Width = 420, Height = 100 };
        tabs.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/UnityBridgeDesk;component/Themes/BenchControls.xaml", UriKind.Relative) });
        tabs.Style = (Style)tabs.FindResource("BenchTabs"); tabs.ItemContainerStyle = (Style)tabs.FindResource("BenchTab");
        foreach (string label in new[] { "준비", "진행", "결과" }) tabs.Items.Add(new TabItem { Header = label });
        panel.Children.Add(hover); panel.Children.Add(arrow); panel.Children.Add(tabs);
        hover.Opacity = 0; DeskMotion.SetAngle(arrow, 0);
        host.UpdateLayout(); Pump(30);
        var marker = (FrameworkElement)tabs.Template.FindName("PART_SelectionIndicator", tabs);
        var parent = (FrameworkElement)marker.Parent;
        double Position() => marker.TranslatePoint(new(), parent).X;
        double Target(int index)
        {
            var tab = (TabItem)tabs.Items[index];
            return tab.TranslatePoint(new(tab.ActualWidth / 2 - marker.Width / 2, 0), parent).X;
        }
        Assert.AreEqual(Visibility.Visible, marker.Visibility);
        Assert.AreEqual(Target(0), Position(), .1);
        double first = Position(); tabs.SelectedIndex = 2;
        if (SystemParameters.ClientAreaAnimation)
        {
            var positions = new List<double>();
            for (int i = 0; i < 12; i++) { Pump(10); positions.Add(Position()); if (Position() > first && Position() < Target(2)) break; }
            Assert.IsTrue(Position() > first && Position() < Target(2), $"The tab marker must pass through an intermediate position. Start {first}, target {Target(2)}, positions {string.Join(", ", positions)}, allowed {DeskMotion.GetAllowed(marker)}, loaded {marker.IsLoaded}, visible {marker.IsVisible}.");
            double interrupted = Position(); tabs.SelectedIndex = 1;
            Assert.AreEqual(interrupted, Position(), 2, "A new selection must continue from the current marker position.");
        }
        hover.Opacity = 1; DeskMotion.SetAngle(arrow, 90);
        Assert.IsFalse(arrow.RenderTransform.HasAnimatedProperties, "Disclosure arrows update immediately without a second transition.");
        DeskMotion.SetAllowed(host, false);
        Assert.AreEqual(Target(tabs.SelectedIndex), Position(), .1, "Pausing settles on the selected tab immediately.");
        Assert.AreEqual(1d, hover.Opacity);
        Assert.AreEqual(90d, ((TransformGroup)arrow.RenderTransform).Children.OfType<RotateTransform>().Single().Angle);
        foreach (var element in new FrameworkElement[] { marker, hover, arrow })
        {
            Assert.IsFalse(element.HasAnimatedProperties);
            Assert.IsFalse(element.RenderTransform.HasAnimatedProperties);
            if (element.RenderTransform is TransformGroup group)
                foreach (var transform in group.Children) Assert.IsFalse(transform.HasAnimatedProperties);
        }
        tabs.SelectedIndex = 0; Assert.AreEqual(Target(0), Position(), .1, "Reduced motion keeps selection feedback immediate.");
        DeskMotion.SetAllowed(host, true);
        for (int i = 0; i < 12; i++) { tabs.SelectedIndex = i % 3; hover.Opacity = i % 2; DeskMotion.SetAngle(arrow, i % 2 * 90); }
        PumpUntil(() => Math.Abs(Target(2) - Position()) < .001 && hover.Opacity == 1);
        Assert.AreEqual(Target(2), Position(), .1); Assert.AreEqual(1d, hover.Opacity);
        Capture(tabs, Path.Combine(directory, "motion-tabs-settled.png"));
        hover.Opacity = 0; panel.Children.Remove(hover); Pump(30);
        Assert.IsFalse(hover.HasAnimatedProperties, "Removed controls must release animation clocks.");
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Func<bool> complete)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Pump(20);
        while (!complete() && elapsed.Elapsed < TimeSpan.FromSeconds(2)) Pump(20);
        Assert.IsTrue(complete(), "The finite transition must settle without queued work.");
    }
    private static void Capture(FrameworkElement control, string path)
    {
        var image = new RenderTargetBitmap((int)control.ActualWidth * 3, (int)control.ActualHeight * 3, 288, 288, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(control) { Stretch = Stretch.Fill }, null, new Rect(0, 0, control.ActualWidth, control.ActualHeight));
        image.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static string Fingerprint(FrameworkElement control)
    {
        var bitmap = new RenderTargetBitmap((int)control.ActualWidth * 2, (int)control.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(control); var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
    }
}
