using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnityBridgeDesk.Infrastructure.SpeedBench;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Desktop.Tests;

internal static class MethodologyRenderChecks
{
    public static void Verify(SpeedBenchWindow window, FrameworkElement content, SpeedRun previous, string directory)
    {
        void Invoke(string name, object value) => typeof(SpeedBenchWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [value]);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Layout(Size size) { content.Measure(size); content.Arrange(new Rect(new Point(), size)); content.UpdateLayout(); }
        void Capture(string name, Size size, double scale)
        {
            Layout(size);
            var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, $"methodology-{name}-{size.Width}-{scale * 100}.png")); encoder.Save(output);
        }
        var run = SpeedReportSample.Methodology();
        Invoke("ShowRun", run); Control<TabControl>("Tabs").SelectedIndex = 2;
        Invoke("SelectResultView", 1); Control<Expander>("StatisticsExpander").IsExpanded = true;
        Assert.Contains("95%", Control<TextBlock>("ConditionMemo").Text);
        Assert.IsTrue(Control<Button>("FollowupPlanButton").IsEnabled);
        Assert.Contains("권장", Control<TextBlock>("ReplicationSummary").Text);
        foreach (var size in new[] { new Size(960, 600), new Size(1440, 850) })
        foreach (double scale in new[] { 1d, 1.5d, 2d })
        {
            Layout(size); Control<ScrollViewer>("ResultDetailsScroll").ScrollToTop();
            Capture("comparison", size, scale);
            Assert.IsGreaterThan(150d, Control<TextBlock>("ReplicationSummary").ActualWidth);
            Assert.IsLessThanOrEqualTo(size.Width, Control<Button>("FollowupPlanButton").TranslatePoint(new Point(), content).X + Control<Button>("FollowupPlanButton").ActualWidth);
        }
        Invoke("SelectResultView", 2); Control<Expander>("SequenceExpander").IsExpanded = true;
        Control<DataGrid>("Trials").SelectedIndex = 0;
        Layout(new(1440, 850)); Control<ScrollViewer>("ResultDetailsScroll").ScrollToBottom();
        Capture("sequence", new(1440, 850), 1);
        Assert.Contains("사전 실행 3회", Control<TextBlock>("SequenceDiagnosis").Text);
        Assert.IsGreaterThan(130d, Control<SpeedSequenceView>("CallSequence").ActualWidth);
        Invoke("ShowRun", run with { Status = "cancelled", Results = run.Results.Take(3).ToArray() });
        Assert.IsFalse(Control<Button>("FollowupPlanButton").IsEnabled);
        Control<Expander>("StatisticsExpander").IsExpanded = false; Control<Expander>("SequenceExpander").IsExpanded = false;
        Invoke("ShowRun", previous);
    }
}
