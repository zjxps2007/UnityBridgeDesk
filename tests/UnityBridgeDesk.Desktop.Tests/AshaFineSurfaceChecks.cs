using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop.Tests;

internal static class AshaFineSurfaceChecks
{
    public static void Verify(string directory)
    {
        var surface = new Grid { Width = 900, Height = 680, Background = Brushes.White };
        var canvas = new Canvas(); surface.Children.Add(canvas);
        void Put(FrameworkElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); canvas.Children.Add(element); }
        var text = new TextBlock { Name = "Paragraph", Width = 300, FontSize = 18, FontFamily = new FontFamily("Malgun Gothic"),
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, LineHeight = 112, Padding = new Thickness(8),
            Text = "화면의 첫 번째 줄\n둘째 줄\n세 번째 줄 위에도 올라간다." };
        Put(text, 370, 140);
        var button = new Button { Name = "ActionButton", Content = "실행 내용 보기", Width = 125, Height = 34 };
        Put(button, 185, 425);
        var disabled = new Button { Name = "DisabledButton", Content = "사용 불가", Width = 100, Height = 32, IsEnabled = false };
        Put(disabled, 35, 260);
        var tight = new TextBlock { Name = "TightParagraph", Width = 240, Text = "첫 번째 문단 줄입니다.\n두 번째 문단 줄입니다.", FontSize = 16 };
        Put(tight, 35, 550);
        var home = new Border { Width = 104, Height = 104 }; Put(home, 15, 560);
        var layer = new Canvas { ClipToBounds = true }; surface.Children.Add(layer);
        var mascot = new DeskMascot { Width = 96, Height = 96 };
        var host = new Border { Width = 96, Height = 96, Child = mascot }; layer.Children.Add(host);
        var window = new Window { Content = surface, SizeToContent = SizeToContent.WidthAndHeight, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        using var asha = new AshaCompanion(surface, layer, host, home, mascot, seed: 31);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        AshaMap Map() => (AshaMap)typeof(AshaCompanion).GetField("map", flags)!.GetValue(asha)!;
        AshaLedge[] Ledges() => (AshaLedge[])typeof(AshaCompanion).GetField("oldLedges", flags)!.GetValue(asha)!;
        void Refresh()
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap(900, 680, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
            asha.RefreshGeometry();
        }
        try
        {
            window.Show(); Refresh();
            var lines = AshaTextLayout.Read(text);
            Assert.HasCount(3, lines, "Each rendered line is a separate surface, not the whole paragraph.");
            Assert.IsTrue(lines[1].Bounds.Width < lines[0].Bounds.Width);
            Assert.IsTrue(lines[1].Bounds.Left > lines[0].Bounds.Left, "Short centered lines retain their actual horizontal position.");
            for (int i = 0; i < 3; i++)
            {
                var ledge = Ledges().Single(l => l.Id == "Paragraph/line:" + i);
                var expected = text.TransformToVisual(layer).TransformBounds(lines[i].Bounds);
                Assert.AreEqual(expected.Top, ledge.Y, .01); Assert.AreEqual(expected.Width, ledge.Right - ledge.Left, .01);
                Assert.IsTrue(Map().Positions.Any(p => Map().Support(p)?.Id == ledge.Id), "Separated lines allow real landing on the second/third line too.");
            }
            var top = button.TranslatePoint(new Point(), layer);
            Assert.AreEqual(top.Y, Ledges().Single(l => l.Id == "ActionButton").Y, .51);
            Assert.HasCount(1, Ledges().Where(l => l.Id.StartsWith("ActionButton")).ToArray(), "A button's text does not duplicate the button platform.");
            Assert.IsFalse(Ledges().Any(l => l.Id == "DisabledButton"));
            Assert.IsTrue(Map().Positions.Any(p => Map().Support(p)?.Id == "ActionButton"));
            Assert.IsFalse(Map().Positions.Any(p => Map().Support(p)?.Id == "TightParagraph/line:1"), "Do not cover the preceding line in tightly set paragraphs.");
            var hit = surface.InputHitTest(button.TranslatePoint(new Point(60, 16), surface));
            Assert.IsTrue(ReferenceEquals(hit, button) || hit is DependencyObject dependency && button.IsAncestorOf(dependency), "Buttons keep their native hit targets.");

            // Actual drawing data handles wrapping, mixed fonts, trimming and empty text.
            text.LineHeight = double.NaN; text.Text = "화면에 보이는 각 줄을 사용한다. 줄바꿈 뒤에도 글자의 위치와 너비가 일치한다.";
            text.Inlines.Add(new Bold(new Run(" 크게 보기")) { FontSize = 24 }); text.Width = 300; Refresh();
            int wideCount = AshaTextLayout.Read(text).Count; text.Width = 150; Refresh();
            Assert.IsGreaterThan(wideCount, AshaTextLayout.Read(text).Count);
            Assert.HasCount(AshaTextLayout.Read(text).Count(l => l.Complete && l.Bounds.Width >= 96 * .3),
                Ledges().Where(l => l.Id.StartsWith("Paragraph/line:")).ToArray(), "A line shorter than both feet is recognized but cannot support a landing.");
            text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis; Refresh();
            Assert.HasCount(1, AshaTextLayout.Read(text)); Assert.IsLessThanOrEqualTo(text.ActualWidth, AshaTextLayout.Read(text)[0].Bounds.Right + 1);
            text.Text = ""; Refresh(); Assert.IsFalse(Ledges().Any(l => l.Id.StartsWith("Paragraph/")));

            // A scroll viewport may reveal, hide or cut through a line. Never keep a stale ledge.
            var scrolledText = new TextBlock { Name = "ScrolledText", FontSize = 20, Text = "스크롤 첫 줄\n스크롤 둘째 줄\n스크롤 셋째 줄", LineHeight = 42 };
            var stack = new StackPanel(); stack.Children.Add(new Border { Height = 180 }); stack.Children.Add(scrolledText); stack.Children.Add(new Border { Height = 220 });
            var scroll = new ScrollViewer { Width = 280, Height = 160, Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
            Put(scroll, 420, 165); Refresh(); Assert.IsFalse(Ledges().Any(l => l.Id.StartsWith("ScrolledText/")));
            scroll.ScrollToVerticalOffset(180); Refresh();
            if (!Ledges().Any(l => l.Id == "ScrolledText/line:0"))
                for (DependencyObject? parent = scrolledText; parent is FrameworkElement f; parent = VisualTreeHelper.GetParent(parent))
                {
                    var arguments = new object[] { f, new Rect(f.RenderSize), false };
                    var bounds = typeof(AshaCompanion).GetMethod("VisibleBounds", flags)!.Invoke(asha, arguments);
                    Console.WriteLine($"{f.GetType().Name} {f.Name}: {f.RenderSize}; visible {bounds}; complete {arguments[2]}; clip {VisualTreeHelper.GetClip(f)}; opacity {f.Opacity}");
                    if (ReferenceEquals(f, surface)) break;
                }
            Assert.IsTrue(Ledges().Any(l => l.Id == "ScrolledText/line:0"), $"Scroll {scroll.VerticalOffset}; text {scrolledText.TranslatePoint(new Point(), layer)}; lines {string.Join(" | ", AshaTextLayout.Read(scrolledText))}; ledges {string.Join(" | ", Ledges().Where(l => l.Id.Contains("Scrolled")))}");
            var first = Ledges().Single(l => l.Id == "ScrolledText/line:0");
            var localFirst = AshaTextLayout.Read(scrolledText)[0].Bounds;
            scroll.ScrollToVerticalOffset(180 + localFirst.Top + 2); Refresh();
            Assert.IsFalse(Ledges().Any(l => l.Id == first.Id), "Partially clipped glyphs cannot support a landing.");
            Assert.IsTrue(Ledges().Any(l => l.Id == "ScrolledText/line:1"));
            scroll.ScrollToVerticalOffset(0); Refresh(); Assert.IsFalse(Ledges().Any(l => l.Id.StartsWith("ScrolledText/")));
            canvas.Children.Remove(scroll);

            text.TextWrapping = TextWrapping.Wrap; text.TextTrimming = TextTrimming.None; text.Text = "첫 줄 위에 선다.\n다음 줄의 발판이다."; text.LineHeight = 112; text.Width = 280;
            foreach (double scale in new[] { 1d, 1.5, 2d })
            {
                text.LayoutTransform = new ScaleTransform(scale, scale); Refresh();
                var line = AshaTextLayout.Read(text)[0]; var expected = text.TransformToVisual(layer).TransformBounds(line.Bounds);
                Assert.AreEqual(expected.Top, Ledges().Single(l => l.Id == "Paragraph/line:0").Y, .01);
                Assert.AreEqual(expected.Width, Ledges().Single(l => l.Id == "Paragraph/line:0") is { } l ? l.Right - l.Left : 0, .01);
            }
            text.LayoutTransform = Transform.Identity; Refresh();
            var destination = Map().Positions.First(p => Map().Support(p)?.Id == "ActionButton");
            typeof(AshaCompanion).GetMethod("Place", flags)!.Invoke(asha, [destination]); asha.RefreshGeometry();
            Assert.IsTrue(asha.IsStanding);
            var image = new RenderTargetBitmap(900, 680, 96, 96, PixelFormats.Pbgra32); image.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(directory, "asha-fine-surfaces.png")); encoder.Save(file);
        }
        finally { asha.Dispose(); window.Close(); mascot.DisposeMotion(); }
    }
}
