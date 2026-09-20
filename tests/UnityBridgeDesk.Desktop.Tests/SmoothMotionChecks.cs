using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop.Tests;

internal static class SmoothMotionChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo Draw = typeof(DeskMascot).GetMethod("DrawAt", Flags)!;
    private static void Sample(DeskMascot actor, double phase)
    {
        double start = (double)typeof(DeskMascot).GetField("actionStarted", Flags)!.GetValue(actor)!;
        Draw.Invoke(actor, [start + phase]);
    }
    private static void Begin(DeskMascot actor, AshaAction action, bool left = false, double impact = .75)
    {
        if (action == AshaAction.Turn) actor.TurnToward(left);
        else actor.SetAction(action, left, impact);
        typeof(DeskMascot).GetField("actionStarted", Flags)!.SetValue(actor, 0d);
        typeof(DeskMascot).GetField("lastDraw", Flags)!.SetValue(actor, 0d);
    }
    public static void Verify(string directory, bool preview)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        foreach (var kind in new[] { CompanionKind.Cat, CompanionKind.Fox })
        {
            var actor = new DeskMascot { Character = kind, Width = 112, Height = 112, MotionEnabled = true };
            var host = new Window { Content = actor, Width = 150, Height = 150, Left = -30000, Top = -30000,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                host.Show(); host.UpdateLayout();
                ((DispatcherTimer)typeof(DeskMascot).GetField("timer", Flags)!.GetValue(actor)!).Stop();
                var scale = (ScaleTransform)actor.FindName("BodyScale");
                var turn = (RotateTransform)actor.FindName("BodyTurn");
                foreach (var action in new[] { AshaAction.Crouch, AshaAction.Jump, AshaAction.Drop, AshaAction.Land, AshaAction.Carried, AshaAction.Idle })
                {
                    double x = scale.ScaleX, y = scale.ScaleY, angle = turn.Angle;
                    Begin(actor, action); Sample(actor, 0);
                    if (Math.Abs(scale.ScaleX - x) > .0001 || Math.Abs(scale.ScaleY - y) > .0001 || Math.Abs(turn.Angle - angle) > .0001)
                        throw new InvalidOperationException("A new action snapped the displayed pose: " + kind + "/" + action);
                    for (int i = 1; i <= 30; i++) Sample(actor, i / 60d);
                }
                Begin(actor, AshaAction.Land); Sample(actor, 0);
                double start = scale.ScaleY;
                Sample(actor, .07);
                if (scale.ScaleY >= start - .03) throw new InvalidOperationException("Landing must absorb impact after contact.");
                Sample(actor, .4);
                if (Math.Abs(scale.ScaleY - 1) > .0001) throw new InvalidOperationException("Landing must settle without endless bouncing.");
                actor.MotionEnabled = false;
                if (actor.IsMoving || scale.ScaleX != 1 || scale.ScaleY != 1 || turn.Angle != 0)
                    throw new InvalidOperationException("Reduced motion did not release the moving pose.");
            }
            finally { actor.DisposeMotion(); host.Close(); }
        }
        if (preview) Preview(directory);
    }
    private static void Preview(string directory)
    {
        const int width = 560, height = 278, count = 260;
        var canvas = new Canvas { Width = width, Height = height, Background = new SolidColorBrush(Color.FromRgb(248, 247, 251)) };
        var actors = new[] { new DeskMascot { Character = CompanionKind.Cat, Width = 96, Height = 96, MotionEnabled = true },
            new DeskMascot { Character = CompanionKind.Fox, Width = 96, Height = 96, MotionEnabled = true } };
        canvas.Children.Add(new TextBlock { Text = "아라 · 아샤  /  움직임 연결 미리보기", FontSize = 15, Margin = new Thickness(16, 10, 0, 0) });
        for (int i = 0; i < 2; i++)
        {
            double y = 37 + i * 119;
            var line = new Border { Width = 528, Height = 1, Background = new SolidColorBrush(Color.FromRgb(207, 202, 217)) };
            Canvas.SetLeft(line, 16); Canvas.SetTop(line, y + 96 * .89); canvas.Children.Add(line);
            canvas.Children.Add(actors[i]); Canvas.SetTop(actors[i], y); Canvas.SetLeft(actors[i], 50);
        }
        var host = new Window { Content = canvas, Width = width, Height = height, SizeToContent = SizeToContent.WidthAndHeight,
            Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show(); host.UpdateLayout();
            foreach (var actor in actors) ((DispatcherTimer)typeof(DeskMascot).GetField("timer", Flags)!.GetValue(actor)!).Stop();
            var encoder = new GifBitmapEncoder();
            var sheet = new DrawingVisual(); using var contact = sheet.RenderOpen();
            int previousStage = -1, shot = 0;
            double previousX = 50;
            for (int frame = 0; frame < count; frame++)
            {
                double time = frame / 25d;
                int stage = time < 2.6 ? 0 : time < 2.9 ? 1 : time < 3.12 ? 2 : time < 3.96 ? 3 :
                    time < 4.36 ? 4 : time < 5.3 ? 5 : time < 6.5 ? 6 : time < 9 ? 7 : time < 9.36 ? 8 : 9;
                double start = new[] { 0d, 2.6, 2.9, 3.12, 3.96, 4.36, 5.3, 6.5, 9, 9.36 }[stage];
                double t = time - start;
                var walk = new AshaWalk(160, 70, true, true);
                double x = stage == 0 ? 50 + walk.Distance(t) : stage == 3 ? 210 + 85 * t / .84 :
                    stage < 3 ? 210 : stage == 9 ? 295 - new AshaWalk(85, 80, true, true).Distance(t) : 295;
                for (int i = 0; i < 2; i++)
                {
                    var actor = actors[i];
                    var action = stage switch { 0 or 9 => AshaAction.Walk, 2 => AshaAction.Crouch, 3 => AshaAction.Jump,
                        4 => AshaAction.Land, 5 => AshaAction.Look, 6 => i == 0 ? AshaAction.Groom : AshaAction.Sniff,
                        8 => AshaAction.Turn, _ => AshaAction.Idle };
                    if (stage != previousStage) { actor.LookToward(-.7, -.35); Begin(actor, action, stage >= 8); }
                    if (action == AshaAction.Walk) actor.AdvanceStride(Math.Abs(x - previousX));
                    Sample(actor, t);
                    Canvas.SetLeft(actor, x);
                    Canvas.SetTop(actor, 37 + i * 119 - (stage == 3 ? 4 * 30 * (t / .84) * (1 - t / .84) : 0));
                }
                canvas.UpdateLayout();
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(canvas);
                var metadata = new BitmapMetadata("gif"); metadata.SetQuery("/grctlext/Delay", (ushort)4);
                if (frame == 0)
                {
                    metadata.SetQuery("/appext/application", System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
                    metadata.SetQuery("/appext/data", new byte[] { 3, 1, 0, 0, 0 });
                }
                encoder.Frames.Add(BitmapFrame.Create(image, null, metadata, null));
                if (new[] { 25, 70, 86, 101, 143, 183 }.Contains(frame))
                { contact.DrawImage(image, new Rect(shot % 2 * width, shot / 2 * height, width, height)); shot++; }
                previousStage = stage; previousX = x;
            }
            contact.Close();
            var contactImage = new RenderTargetBitmap(width * 2, height * 3, 96, 96, PixelFormats.Pbgra32); contactImage.Render(sheet);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(contactImage));
            using (var output = File.Create(Path.Combine(directory, "character-motion-contact.png"))) png.Save(output);
            using (var output = File.Create(Path.Combine(directory, "character-motion.gif"))) encoder.Save(output);
        }
        finally { foreach (var actor in actors) actor.DisposeMotion(); host.Close(); }
    }
}
