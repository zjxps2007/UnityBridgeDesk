using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnityBridgeDesk.Desktop.Controls;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop.Tests;

internal static class AshaChartChecks
{
    public static void Verify(string directory)
    {
        var root = new Grid { Width = 1200, Height = 780, Background = Brushes.White };
        var canvas = new Canvas(); root.Children.Add(canvas);
        var chart = new SpeedChartView { Name = "TestChart", Width = 1120 };
        ReportChart Data(int count) => new("F01", "graph-play", "ms", Enumerable.Range(0, count).Select(i =>
            new ChartSeries("v" + i, 30 + i % 4 * 23, null, null, 5, 5, 5, 5, 5, 0, i)).ToArray());
        chart.Show(Data(5), false); chart.Height = 330; Canvas.SetLeft(chart, 35); Canvas.SetTop(chart, 310); canvas.Children.Add(chart);
        var home = new Border { Width = 104, Height = 104 }; Canvas.SetLeft(home, 10); Canvas.SetTop(home, 600); canvas.Children.Add(home);
        var layer = new Canvas { ClipToBounds = true }; root.Children.Add(layer);
        var mascot = new DeskMascot { Width = 96, Height = 96 }; var host = new Border { Width = 96, Height = 96, Child = mascot }; layer.Children.Add(host);
        var window = new Window { Content = root, SizeToContent = SizeToContent.WidthAndHeight, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, WindowStartupLocation = WindowStartupLocation.Manual };
        using var asha = new AshaCompanion(root, layer, host, home, mascot, seed: 7);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        AshaMap Map() => (AshaMap)typeof(AshaCompanion).GetField("map", flags)!.GetValue(asha)!;
        void Refresh() { window.UpdateLayout(); Pump(40); asha.RefreshGeometry(); }
        try
        {
            window.Show(); Refresh(); Refresh();
            Assert.HasCount(5, chart.GetCompanionTerrain().Bars);
            Assert.IsTrue(chart.CompanionVisiting); Assert.IsTrue(asha.IsStanding);
            Assert.AreEqual(AshaLedgeKind.ChartFloor, Map().Support(asha.Position)?.Kind);
            Assert.HasCount(5, Map().Positions.Select(p => Map().Support(p)).Where(l => l?.Kind == AshaLedgeKind.ChartBar).Select(l => l!.Value.Id).Distinct().ToArray());
            Assert.IsFalse(host.IsHitTestVisible, "The companion must not swallow chart clicks.");
            var terrain = chart.GetCompanionTerrain(); var firstBody = terrain.Bars[0].Bounds;
            Assert.IsFalse(terrain.Labels.Any(r => r.IntersectsWith(new Rect(firstBody.X - 17, firstBody.Y - 89, 96, 85))));

            asha.Configure(true, true, 1);
            if (SystemParameters.ClientAreaAnimation)
            {
                var visited = new HashSet<string>(); var actions = new HashSet<AshaAction>(); var watch = Stopwatch.StartNew();
                while (visited.Count < 3 && watch.Elapsed < TimeSpan.FromSeconds(32))
                {
                    Pump(35); actions.Add(asha.Action); Assert.IsTrue(asha.IsPositionSafe);
                    if (asha.IsStanding && Map().Support(asha.Position) is { Kind: AshaLedgeKind.ChartBar } bar) visited.Add(bar.Id);
                }
                Assert.IsGreaterThanOrEqualTo(3, visited.Count, "Real timers must start a multi-bar game without an injected destination.");
                Assert.Contains(AshaAction.Jump, actions); Assert.Contains(AshaAction.Crouch, actions); Assert.Contains(AshaAction.Land, actions);
                var image = new RenderTargetBitmap(1200, 780, 96, 96, PixelFormats.Pbgra32); image.Render(root);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using (var f = File.Create(Path.Combine(directory, "asha-chart-play.png"))) png.Save(f);
                root.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Assert.IsFalse(asha.IsPlayingOnChart); Pump(1800); Assert.IsTrue(asha.IsStanding); Assert.IsNull(asha.Destination);
            }
            asha.Configure(false, false, 1);
            var data = Data(5); var expected = data.Series.Select(s => s.Mean).ToArray();
            chart.Show(data, false); chart.Height = 330; Refresh();
            chart.SetCompanionVisiting(false, asha); var before = chart.GetCompanionTerrain(); chart.SetCompanionVisiting(true, asha); var after = chart.GetCompanionTerrain();
            CollectionAssert.AreEqual(before.Bars, after.Bars, "Label avoidance must not change data, bar order or height.");
            CollectionAssert.AreEqual(expected, data.Series.Select(s => s.Mean).ToArray());
            string? selected = null; chart.ReleaseSelected += release => selected = release;
            typeof(SpeedChartView).GetMethod("OnKeyDown", flags)!.Invoke(chart, [new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(chart)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent }]);
            Assert.AreEqual("v0", selected);
            chart.Show(data with { Series = [data.Series[0] with { Mean = null }, data.Series[1] with { Mean = 0 }] }, false); chart.Height = 330; Refresh();
            Assert.IsEmpty(chart.GetCompanionTerrain().Bars); Assert.IsFalse(asha.IsPlayingOnChart);
            chart.Show(Data(8), true); chart.Height = 330; Refresh(); Assert.HasCount(8, chart.GetCompanionTerrain().Bars);
            chart.Show(null, false); Refresh(); Assert.IsEmpty(chart.GetCompanionTerrain().Bars);
            Assert.IsFalse(asha.IsPlayingOnChart);
        }
        finally { asha.Dispose(); window.Close(); mascot.DisposeMotion(); }
        Assert.IsFalse(chart.CompanionVisiting);
    }
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
