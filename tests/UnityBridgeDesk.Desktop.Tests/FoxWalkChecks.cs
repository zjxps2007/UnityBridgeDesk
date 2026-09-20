using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop.Tests;

internal static class FoxWalkChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Verify(string directory)
    {
        var drawWalk = typeof(DeskMascot).GetMethod("SetFoxWalkFrame", Flags)!;
        var report = new System.Text.StringBuilder("size,dpi,direction,frame,height,feet\n");
        foreach (int size in new[] { 96, 112, 144 })
        foreach (double dpi in new[] { 1d, 1.5, 2d })
        foreach (bool left in new[] { false, true })
        {
            var actor = new DeskMascot { Character = CompanionKind.Fox, Width = size, Height = size, MotionEnabled = false };
            try
            {
                actor.RestoreFacing(left); actor.Measure(new(size, size)); actor.Arrange(new(0, 0, size, size));
                var heights = new List<double>();
                for (int frame = 0; frame < 8; frame++)
                {
                    drawWalk.Invoke(actor, [frame]); actor.UpdateLayout();
                    int pixels = (int)(size * dpi);
                    var image = new RenderTargetBitmap(pixels, pixels, dpi * 96, dpi * 96, PixelFormats.Pbgra32); image.Render(actor);
                    var data = new byte[pixels * pixels * 4]; image.CopyPixels(data, pixels * 4, 0);
                    int x0 = pixels, y0 = pixels, x1 = -1, y1 = -1;
                    for (int y = 0; y < pixels; y++) for (int x = 0; x < pixels; x++)
                    {
                        if (data[(y * pixels + x) * 4 + 3] <= 192) continue;
                        x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                    }
                    if (x0 <= 0 || x1 >= pixels - 1 || y0 <= 0 || y1 >= pixels - 1 || x1 < x0)
                        throw new InvalidOperationException($"Walk frame missing or clipped: {size}/{dpi}/{left}/{frame}");
                    double height = (y1 - y0 + 1) / dpi, feet = (y1 + 1) / dpi;
                    if (Math.Abs(feet - size * .89) > 1.5 || Math.Abs(height - size * .84) > 2)
                        throw new InvalidOperationException($"Walk scale/ground mismatch: {size}/{dpi}/{left}/{frame}: {height}/{feet}");
                    heights.Add(height); report.AppendLine(FormattableString.Invariant($"{size},{dpi},{left},{frame},{height:F2},{feet:F2}"));
                }
                if (heights.Max() - heights.Min() > 2) throw new InvalidOperationException("Walk cycle resizes the character.");
            }
            finally { actor.DisposeMotion(); }
        }
        File.WriteAllText(Path.Combine(directory, "fox-walk-scale.csv"), report.ToString());
        ContactSheet(directory, drawWalk);
        VerifyMotion();
    }
    private static void ContactSheet(string directory, MethodInfo drawWalk)
    {
        var canvas = new Canvas { Width = 800, Height = 172, Background = Brushes.White };
        canvas.Children.Add(new TextBlock { Text = "아샤 · 새 걷기 8프레임", FontSize = 14, Margin = new Thickness(12, 10, 0, 0) });
        var line = new Border { Background = new SolidColorBrush(Color.FromRgb(196, 190, 210)), Height = 1, Width = 776 };
        Canvas.SetLeft(line, 12); Canvas.SetTop(line, 48 + 96 * .89); canvas.Children.Add(line);
        var actors = new List<DeskMascot>();
        try
        {
            for (int frame = 0; frame < 8; frame++)
            {
                var actor = new DeskMascot { Character = CompanionKind.Fox, Width = 96, Height = 96, MotionEnabled = false };
                drawWalk.Invoke(actor, [frame]); actors.Add(actor); canvas.Children.Add(actor);
                Canvas.SetLeft(actor, 10 + 98 * frame); Canvas.SetTop(actor, 48);
            }
            canvas.Measure(new(800, 172)); canvas.Arrange(new(0, 0, 800, 172)); canvas.UpdateLayout();
            var image = new RenderTargetBitmap(1200, 258, 144, 144, PixelFormats.Pbgra32); image.Render(canvas);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(directory, "fox-walk-frames.png")); encoder.Save(output);
        }
        finally { foreach (var actor in actors) actor.DisposeMotion(); }
    }
    private static void VerifyMotion()
    {
        var actor = new DeskMascot { Character = CompanionKind.Fox, Width = 96, Height = 96, MotionEnabled = true };
        var host = new Window { Content = actor, Width = 150, Height = 150, Left = -30000, Top = -30000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show(); host.UpdateLayout();
            if (SystemParameters.ClientAreaAnimation)
            {
                actor.SetAction(AshaAction.Walk);
                for (int i = 0; i < 24; i++)
                {
                    actor.AdvanceStride(6);
                    if (actor.CurrentFrame != (i + 1) % 8) throw new InvalidOperationException("Walk pose lags actual distance.");
                }
                int stopped = actor.CurrentFrame;
                var pump = new DispatcherFrame(); var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
                wait.Tick += (_, _) => { wait.Stop(); pump.Continue = false; }; wait.Start(); Dispatcher.PushFrame(pump);
                if (actor.CurrentFrame != stopped) throw new InvalidOperationException("Walking feet move without travel.");
                actor.SetAction(AshaAction.Idle); actor.SetAction(AshaAction.Walk, true);
                if (!actor.FacesLeft || actor.CurrentFrame != 0) throw new InvalidOperationException("New walk starts with wrong facing/pose.");
            }
            actor.MotionEnabled = false;
            if (actor.IsMoving || actor.CurrentFrame != 4) throw new InvalidOperationException("Paused fox retains walking clocks/frame.");
            if ((bool)typeof(DeskMascot).GetField("walkingFrame", Flags)!.GetValue(actor)!)
                throw new InvalidOperationException("Idle frame is using the walking sheet.");
        }
        finally { actor.DisposeMotion(); host.Close(); }
    }
}
