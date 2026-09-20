using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnityBridgeDesk.Desktop.Controls;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop.Tests;

internal static class AshaContactChecks
{
    public static void Verify(string directory)
    {
        var root = new Grid { Width = 900, Height = 720, Background = Brushes.White };
        var content = new Canvas(); root.Children.Add(content);
        void Put(FrameworkElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); content.Children.Add(element); }
        var text = new TextBlock { Name = "ClippedText", Width = 340, FontSize = 18,
            Text = "옆으로 잘려도 보이는 글줄에는 착지한다", Clip = new RectangleGeometry(new Rect(60, 0, 180, 60)) };
        Put(text, 40, 180);
        var round = new Button { Name = "Rounded", Width = 180, Height = 48, Content = "둥근 버튼", Background = Brushes.Lavender,
            Template = (ControlTemplate)XamlReader.Parse("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
                  <Border Background="{TemplateBinding Background}" CornerRadius="24">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                  </Border>
                </ControlTemplate>
                """) };
        Put(round, 360, 240);
        var clippedButton = new Button { Name = "SideButton", Width = 200, Height = 40, Content = "옆으로 잘린 버튼",
            Clip = new RectangleGeometry(new Rect(60, 0, 140, 40)) };
        Put(clippedButton, 60, 360);
        var chart = new SpeedChartView { Name = "ContactChart", Width = 430 };
        chart.Show(new ReportChart("F01", "contact", "ms", [
            new ChartSeries("vA", 30, null, null, 5, 5, 5, 5, 5, 0, 0),
            new ChartSeries("vB", 80, null, null, 5, 5, 5, 5, 5, 0, 1)]), false);
        chart.Height = 240; Put(chart, 430, 400);
        var floor = new Border { Name = "ContactFloor", Width = 850, Height = 1, Background = Brushes.Gray };
        AshaSurface.SetEdge(floor, AshaEdge.Top); Put(floor, 20, 660);
        var home = new Border { Width = 100, Height = 100 }; Put(home, 20, 540);
        var layer = new Canvas { ClipToBounds = true }; root.Children.Add(layer);
        var mascot = new DeskMascot { Width = 96, Height = 96 };
        var host = new Border { Width = 96, Height = 96, Child = mascot }; layer.Children.Add(host);
        var window = new Window { Content = root, SizeToContent = SizeToContent.WidthAndHeight, Left = -22000, Top = -22000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        using var companion = new AshaCompanion(root, layer, host, home, mascot, seed: 43);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        AshaMap Map() => (AshaMap)typeof(AshaCompanion).GetField("map", flags)!.GetValue(companion)!;
        AshaLedge[] Ledges() => (AshaLedge[])typeof(AshaCompanion).GetField("oldLedges", flags)!.GetValue(companion)!;
        void Draw(string? file = null)
        {
            window.UpdateLayout(); var image = new RenderTargetBitmap(900, 720, 96, 96, PixelFormats.Pbgra32); image.Render(root);
            if (file is null) return;
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(directory, file)); encoder.Save(stream);
        }
        void Place(Point p)
        {
            typeof(AshaCompanion).GetMethod("StopRoute", flags)!.Invoke(companion, null);
            typeof(AshaCompanion).GetMethod("Place", flags)!.Invoke(companion, [p]);
        }
        void WaitForLanding()
        {
            var watch = Stopwatch.StartNew();
            while (companion.IsFalling && watch.ElapsedMilliseconds < 2500) Pump(20);
            Assert.IsTrue(companion.IsStanding); Assert.IsFalse(companion.IsFalling);
        }
        try
        {
            window.Show(); Draw(); Pump(60);
            var line = Ledges().Single(l => l.Id == "ClippedText/line:0");
            Assert.AreEqual(100d, line.Left, .01); Assert.AreEqual(280d, line.Right, .01);
            Assert.IsTrue(Map().StandingSpans.Any(s => s.Ledge.Id == line.Id));
            var button = Ledges().Single(l => l.Id == "SideButton");
            Assert.AreEqual(120d, button.Left, .01); Assert.IsTrue(button.Right <= 260);
            var roundTop = Ledges().Single(l => l.Id == "Rounded");
            Assert.IsTrue(roundTop.Left >= 383 && roundTop.Right <= 517, "Rounded corners cannot become flat ground.");
            Assert.AreEqual(240d, roundTop.Y, .01);
            Place(Map().StandingSpans.First(s => s.Ledge.Id == "Rounded").Closest(new(403.271, 0)));
            Draw("contact-rounded-button.png");

            // A visible fragment is usable only when its original top survives the clip.
            text.Clip = new RectangleGeometry(new Rect(60, 12, 180, 40)); Draw(); Pump(60);
            Assert.IsFalse(Ledges().Any(l => l.Id == line.Id));
            text.Clip = null; Draw(); Pump(40);
            var bar = chart.GetCompanionTerrain().Bars[0];
            Assert.IsGreaterThan(60d, bar.Bounds.Width);
            double cut = bar.Bounds.Left + 8;
            chart.Clip = new RectangleGeometry(new Rect(cut, 0, chart.Width - cut, chart.Height)); Draw(); Pump(60);
            var clippedBar = Ledges().Single(l => l.Id == "ContactChart/bar:" + bar.Id);
            Assert.AreEqual(430 + cut, clippedBar.Left, .01);
            Assert.AreEqual(400 + bar.Bounds.Top, clippedBar.Y, .01);
            Assert.IsTrue(Map().StandingSpans.Any(s => s.Ledge.Id == clippedBar.Id));

            text.Visibility = clippedButton.Visibility = chart.Visibility = Visibility.Collapsed;
            if (SystemParameters.ClientAreaAnimation)
            {
                companion.Configure(true, false, 1);
                foreach (var kind in Enum.GetValues<CompanionKind>())
                {
                    mascot.Character = kind; Canvas.SetTop(round, 240); Draw(); Pump(50);
                    var span = Map().StandingSpans.First(s => s.Ledge.Id == "Rounded");
                    var start = span.Closest(new(403.271, 0)); Place(start);
                    Canvas.SetTop(round, 430); Draw(); Pump(90);
                    Assert.IsTrue(companion.IsFalling, "A moved button must invalidate support before the old 700 ms refresh interval.");
                    Assert.IsGreaterThan(start.Y, companion.Position.Y);
                    WaitForLanding();
                    Assert.AreEqual("Rounded", Map().Support(companion.Position)?.Id);
                    Assert.AreEqual(430d, companion.Position.Y + Map().Feet, .001);
                    Assert.AreEqual(start.X, companion.Position.X);
                    Draw("contact-landed-" + kind + ".png");
                }
                // Programmatic scrolling (no mouse wheel event) must also invalidate the old top.
                round.Visibility = Visibility.Collapsed;
                var scrolled = new TextBlock { Name = "MovingLine", Text = "스크롤 뒤에는 새 위치로 내려온다", FontSize = 18 };
                var stack = new StackPanel(); stack.Children.Add(new Border { Height = 40 }); stack.Children.Add(scrolled); stack.Children.Add(new Border { Height = 450 });
                var scroll = new ScrollViewer { Width = 330, Height = 180, Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
                Put(scroll, 310, 300); Draw(); Pump(60);
                var perch = Map().StandingSpans.First(s => s.Ledge.Id == "MovingLine/line:0");
                Place(perch.Closest(new(365.731, 0)));
                scroll.ScrollToVerticalOffset(120); Draw(); Pump(90);
                Assert.IsFalse(Ledges().Any(l => l.Id == "MovingLine/line:0"));
                Assert.IsTrue(companion.IsFalling);
                WaitForLanding(); Assert.AreEqual("ContactFloor", Map().Support(companion.Position)?.Id);
                Draw("contact-after-scroll.png");
            }
            host.Width = host.Height = mascot.Width = mascot.Height = 144; Draw(); Pump(60);
            Assert.AreEqual(new Size(144, 144), Map().SpriteSize);
        }
        finally { companion.Dispose(); window.Close(); mascot.DisposeMotion(); }
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
