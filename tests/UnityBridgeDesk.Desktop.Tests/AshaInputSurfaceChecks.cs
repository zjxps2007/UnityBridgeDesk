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

internal static class AshaInputSurfaceChecks
{
    public static void Verify(string directory)
    {
        var root = new Grid { Width = 800, Height = 680, Background = Brushes.White };
        root.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/UnityBridgeDesk;component/Themes/BenchControls.xaml", UriKind.Relative) });
        var content = new Canvas(); root.Children.Add(content);
        void Put(FrameworkElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); content.Children.Add(element); }
        // Same styles, sizes, wording and neighbouring controls as the reported Editor section.
        var section = new StackPanel { Width = 553, Name = "EditorSection" };
        var label = new TextBlock { Name = "EditorStatus", Text = "Unity 5개 발견 · 선택한 Editor로 새 실험 프로젝트를 만듭니다.",
            FontSize = 13, Margin = new(0, 6, 0, 10), LineHeight = 19 };
        section.Children.Add(label);
        var combo = new ComboBox { Name = "EditorList", FontSize = 13 };
        combo.Items.Add("Unity 6000.3.23f1"); combo.Items.Add("Unity 6000.3.24f1"); combo.SelectedIndex = 0; section.Children.Add(combo);
        var actions = new WrapPanel { Margin = new(0, 10, 0, 0) };
        var find = new Button { Name = "FindEditors", Content = "다시 찾기", Style = (Style)root.FindResource("QuietButton"), Margin = new(0, 0, 8, 0) };
        actions.Children.Add(find); actions.Children.Add(new Button { Content = "직접 선택…", Style = (Style)root.FindResource("QuietButton") }); section.Children.Add(actions);
        var details = new Expander { Header = "실험 환경과 분리 범위", Content = new TextBlock { Text = "임시 프로젝트에서 측정합니다." } }; section.Children.Add(details);
        Put(section, 90, 220);
        var input = new TextBox { Name = "NumericInput", Width = 220, Text = "30" }; Put(input, 90, 510);
        var home = new Border { Width = 100, Height = 100 }; Put(home, 540, 530);
        var floor = new Border { Name = "InputFloor", Width = 720, Height = 1, Background = Brushes.Gray };
        AshaSurface.SetEdge(floor, AshaEdge.Top); Put(floor, 40, 630);
        var layer = new Canvas { ClipToBounds = true }; root.Children.Add(layer);
        var mascot = new DeskMascot { Width = 96, Height = 96 }; var host = new Border { Width = 96, Height = 96, Child = mascot }; layer.Children.Add(host);
        var window = new Window { Content = root, SizeToContent = SizeToContent.WidthAndHeight, Left = -24000, Top = -24000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        using var companion = new AshaCompanion(root, layer, host, home, mascot, 81);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        AshaMap Map() => (AshaMap)typeof(AshaCompanion).GetField("map", flags)!.GetValue(companion)!;
        AshaLedge[] Ledges() => (AshaLedge[])typeof(AshaCompanion).GetField("oldLedges", flags)!.GetValue(companion)!;
        void Draw(string? name = null, double scale = 1)
        {
            window.UpdateLayout(); var image = new RenderTargetBitmap((int)(800 * scale), (int)(680 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); image.Render(root);
            if (name is null) return;
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(directory, name)); png.Save(file);
        }
        void Refresh() { Draw(); Pump(60); }
        void Release(Point p)
        {
            typeof(AshaCompanion).GetMethod("StopRoute", flags)!.Invoke(companion, null);
            typeof(AshaCompanion).GetMethod("Place", flags)!.Invoke(companion, [p]);
            typeof(AshaCompanion).GetField("dragging", flags)!.SetValue(companion, true);
            typeof(AshaCompanion).GetMethod("Released", flags)!.Invoke(companion,
                [mascot, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent }]);
        }
        void LandOn(string id, Point release)
        {
            Release(release); Assert.IsTrue(companion.IsFalling);
            var watch = Stopwatch.StartNew();
            while (companion.IsFalling && watch.ElapsedMilliseconds < 2400) Pump(20);
            Assert.IsFalse(companion.IsFalling); Assert.IsTrue(companion.IsStanding);
            Assert.AreEqual(id, Map().Support(companion.Position)?.Id);
            Assert.AreEqual(release.X, companion.Position.X, .001);
        }
        try
        {
            window.Show(); Refresh();
            Assert.IsTrue(Ledges().Any(l => l.Id == "EditorList"), "The actual Editor ComboBox must supply a painted landing surface, not only an obstacle.");
            Assert.IsTrue(Ledges().Any(l => l.Id == "NumericInput"));
            Assert.IsFalse(Ledges().Any(l => l.Id.StartsWith("EditorList/") || l.Id.StartsWith("NumericInput/")), "Field contents and arrow cannot duplicate their outer frame.");
            Assert.IsTrue(Ledges().Any(l => l.Id.StartsWith("FindEditors/") && l.Kind == AshaLedgeKind.TextLine));
            var roof = Ledges().Single(l => l.Id == "EditorList");
            Assert.AreEqual(combo.TranslatePoint(new Point(), layer).Y, roof.Y, .01);
            var hit = root.InputHitTest(combo.TranslatePoint(new Point(combo.ActualWidth - 20, 18), root));
            Assert.IsTrue(hit is DependencyObject d && combo.IsAncestorOf(d), "The drop-down arrow keeps its original click target.");
            int selected = combo.SelectedIndex;
            combo.IsDropDownOpen = true; Refresh(); Assert.IsTrue(combo.IsDropDownOpen);
            Assert.HasCount(1, Ledges().Where(l => l.Id == "EditorList").ToArray());
            combo.SelectedIndex = 1; combo.IsDropDownOpen = false; Refresh(); Assert.AreEqual(1, combo.SelectedIndex);
            input.Select(0, 1); Assert.AreEqual(1, input.SelectionLength);
            if (SystemParameters.ClientAreaAnimation)
            {
                companion.Configure(true, false, 1);
                foreach (var kind in Enum.GetValues<CompanionKind>())
                {
                    mascot.Character = kind;
                    var span = Map().StandingSpans.Last(s => s.Ledge.Id == "EditorList");
                    var target = span.Closest(new(565.371, 0));
                    LandOn("EditorList", new(target.X, target.Y - 70));
                    Assert.AreEqual(roof.Y, companion.Position.Y + Map().Feet, .001);
                    foreach (double scale in new[] { 1d, 1.5, 2d }) Draw($"input-landing-{kind}-{scale * 100}.png", scale);
                    var field = Map().StandingSpans.First(s => s.Ledge.Id == "NumericInput").Closest(new(175.371, 0));
                    LandOn("NumericInput", new(field.X, field.Y - 60));
                }
                // A fully clipped/hidden field must not leave its old roof in the collision map.
                combo.Visibility = Visibility.Collapsed; Refresh();
                Assert.IsFalse(Ledges().Any(l => l.Id == "EditorList"));
                combo.Visibility = Visibility.Visible; Refresh();
            }
            companion.Configure(false, false, 1);
            combo.Clip = new RectangleGeometry(new Rect(80, 0, combo.ActualWidth - 80, combo.ActualHeight)); Refresh();
            Assert.AreEqual(170d, Ledges().Single(l => l.Id == "EditorList").Left, .01);
            combo.Clip = new RectangleGeometry(new Rect(0, 6, combo.ActualWidth, combo.ActualHeight - 6)); Refresh();
            Assert.IsFalse(Ledges().Any(l => l.Id == "EditorList"), "A clipped top is not a new floor at the clip edge.");
            combo.Clip = null; combo.IsEnabled = false; Refresh();
            Assert.IsFalse(Ledges().Any(l => l.Id == "EditorList"));
            combo.IsEnabled = true; Refresh(); Assert.IsTrue(Ledges().Any(l => l.Id == "EditorList"));
        }
        finally { companion.Dispose(); window.Close(); mascot.DisposeMotion(); }
    }
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
