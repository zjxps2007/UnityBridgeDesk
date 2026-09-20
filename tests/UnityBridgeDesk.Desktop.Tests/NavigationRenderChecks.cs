using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop.Tests;

internal static class NavigationRenderChecks
{
    public static void Verify(SpeedBenchWindow window, FrameworkElement content, SpeedRun sample, string directory)
    {
        void Invoke(string name, object value) => typeof(SpeedBenchWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [value]);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Layout(double width, double height)
        { content.Measure(new(width, height)); content.Arrange(new(0, 0, width, height)); content.UpdateLayout(); }
        void Capture(string name)
        {
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
        }
        var options = new SpeedOptions(Repeats: 20, Experiments: ["F01", "F02", "F03", "F04", "S01"]);
        var plan = SpeedProtocol.Schedule(options, sample.Releases.Select(r => r.Tag).ToArray());
        Assert.AreEqual(600, plan.Length);
        var results = plan.Select((trial, i) => sample.Results[i % sample.Results.Length] with { Trial = trial }).ToArray();
        var run = sample with { Id = Guid.NewGuid(), Options = options, Plan = plan, Results = results };
        Control<TabControl>("Tabs").SelectedIndex = 0; Layout(1440, 850);
        Assert.AreEqual(1, Grid.GetColumn(Control<StackPanel>("PreparationOptions")));
        Capture("workspace-preparation-wide");
        Layout(960, 600);
        Assert.AreEqual(0, Grid.GetColumn(Control<StackPanel>("PreparationOptions")));
        Assert.AreEqual(1, Grid.GetRow(Control<StackPanel>("PreparationOptions")));
        Invoke("ShowRun", run); Control<TabControl>("Tabs").SelectedIndex = 2;
        var experiments = Control<ListBox>("ChartExperiment");
        var conditions = Control<ListBox>("ChartCondition");
        var trials = Control<ListBox>("TrialIndex");
        Assert.AreEqual(5, experiments.Items.Count);
        Assert.AreEqual(Visibility.Visible, Control<StackPanel>("OverviewView").Visibility);
        Assert.AreEqual(new SpeedReport(run).Comparisons.Length, Control<DataGrid>("OverviewTable").Items.Count);
        Capture("workspace-overview-small");
        Invoke("SelectResultView", 0);
        foreach (string code in options.Selected)
        {
            experiments.SelectedItem = code;
            foreach (var condition in conditions.Items.Cast<ReportChart>())
            {
                conditions.SelectedItem = condition;
                Assert.IsTrue(trials.Items.Cast<ReportTrial>().All(t => t.Experiment == code && t.Condition == condition.Condition));
                Assert.AreEqual(40, trials.Items.Count);
                Assert.Contains(condition.Condition, Control<TextBlock>("ResultHeading").Text);
            }
        }

        Control<Expander>("TrialFiltersExpander").IsExpanded = true;
        Layout(960, 600);
        if (Control<Grid>("SidebarBody").Visibility != Visibility.Visible)
            Control<Button>("SidebarToggle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(960, 600);
        Capture("navigation-expanded-small");
        Assert.IsGreaterThanOrEqualTo(100d, trials.ActualHeight, "Expanded filters must leave a scrollable trial list at the minimum size.");
        Assert.IsLessThanOrEqualTo(600d, trials.TranslatePoint(new(), content).Y + trials.ActualHeight);
        Assert.AreEqual(30d, Control<TextBlock>("Clock").FontSize);
        Assert.IsGreaterThanOrEqualTo(14d, Control<TextBlock>("Date").ActualHeight, "The compact header must not clip its date line.");
        Assert.IsNull(Control<UnityBridgeDesk.Desktop.Controls.DeskMascot>("Mascot").Effect);
        Control<ScrollViewer>("ResultIndexTools").ScrollToBottom(); content.UpdateLayout();
        Capture("navigation-filters-small");

        Control<Expander>("TrialFiltersExpander").IsExpanded = false;
        Layout(1440, 850); Control<ScrollViewer>("ResultIndexTools").ScrollToTop(); content.UpdateLayout();
        Capture("navigation-five-experiments");

        // Returning from another condition or record must not discard the selected evidence.
        experiments.SelectedItem = "F01"; conditions.SelectedIndex = 0;
        var selectedChart = (ReportChart)conditions.SelectedItem;
        var targetTrial = (ReportTrial)trials.Items[4];
        Control<TextBox>("TrialSearch").Text = "#" + targetTrial.Order;
        trials.SelectedIndex = 0;
        Control<Expander>("TrialDiagnosticsExpander").IsExpanded = true;
        Invoke("SelectResultView", 2); Layout(960, 600);
        var detailsScroll = Control<ScrollViewer>("ResultDetailsScroll");
        detailsScroll.ScrollToVerticalOffset(Math.Min(60, detailsScroll.ScrollableHeight)); content.UpdateLayout();
        double rememberedOffset = detailsScroll.VerticalOffset;
        Assert.IsGreaterThan(0d, rememberedOffset);
        Invoke("SelectResultView", 0); Layout(960, 600);
        Invoke("SelectResultView", 2); Layout(960, 600);
        Assert.AreEqual(rememberedOffset, detailsScroll.VerticalOffset, 1d);
        experiments.SelectedItem = "F02"; Layout(960, 600);
        Assert.AreEqual("", Control<TextBox>("TrialSearch").Text);
        experiments.SelectedItem = "F01"; Layout(960, 600);
        Assert.AreEqual(selectedChart.Condition, ((ReportChart)conditions.SelectedItem).Condition);
        Assert.AreEqual("#" + targetTrial.Order, Control<TextBox>("TrialSearch").Text);
        Assert.AreEqual(targetTrial.Trial.Id, ((ReportTrial)trials.SelectedItem).Trial.Id);
        Assert.AreEqual(Visibility.Visible, Control<Border>("TrialView").Visibility);
        Assert.AreEqual(rememberedOffset, detailsScroll.VerticalOffset, 1d);
        Invoke("ShowRun", sample); Invoke("ShowRun", run); Layout(960, 600);
        Assert.AreEqual("#" + targetTrial.Order, Control<TextBox>("TrialSearch").Text);
        Assert.AreEqual(targetTrial.Trial.Id, ((ReportTrial)trials.SelectedItem).Trial.Id);
        Control<TextBox>("TrialSearch").Text = "";
        Control<Expander>("TrialDiagnosticsExpander").IsExpanded = false;
        Control<Button>("OverviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(1440, 850);
        Capture("workspace-overview-wide");

        Invoke("BeginRun", options);
        var mascot = Control<UnityBridgeDesk.Desktop.Controls.DeskMascot>("Mascot");
        Assert.IsFalse(mascot.MotionEnabled, "The companion must stay still during a benchmark.");
        Assert.IsFalse(mascot.React()); Assert.IsFalse(mascot.IsBlinkTimerRunning);
        var asha = (UnityBridgeDesk.Desktop.Controls.AshaCompanion)typeof(SpeedBenchWindow)
            .GetField("asha", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        Assert.IsFalse(asha.IsRunning, "Benchmark preparation must also stop route planning and autonomous decisions.");
        Assert.AreEqual(Visibility.Collapsed, Control<Canvas>("CompanionLayer").Visibility,
            "Hide the companion during benchmark progress to avoid stale placement over changing content.");
        Assert.IsFalse(UnityBridgeDesk.Desktop.Controls.DeskMotion.GetAllowed(window));
        Invoke("ShowProgress", new SpeedLiveProgress(SpeedLiveStage.Preparation, 0, plan.Length, null, Plan: plan));
        Layout(1440, 850);
        var live = Control<ListBox>("LiveTrialIndex");
        Assert.AreEqual(600, live.Items.Count);
        Assert.IsNull(live.ItemContainerGenerator.ContainerFromIndex(599), "Offscreen rows must remain virtualized.");
        live.SelectedIndex = 0; var inspected = live.SelectedItem;
        Invoke("ShowProgress", new SpeedLiveProgress(SpeedLiveStage.Measurement, 599, 600, plan[^1]));
        Assert.AreSame(inspected, live.SelectedItem);
        Control<Button>("FollowTrialButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(599, live.SelectedIndex);
        Control<TextBox>("LiveSearch").Text = "#600";
        Assert.AreEqual(1, live.Items.Count); Assert.AreSame(live.Items[0], live.SelectedItem);
        Control<TextBox>("LiveSearch").Text = "";
        Invoke("ShowProgress", new SpeedLiveProgress(SpeedLiveStage.Cleanup, 1, 600, results[3].Trial, results[3]));
        Control<ComboBox>("LiveFilter").SelectedIndex = 2;
        Assert.AreEqual(1, live.Items.Count);
        live.SelectedIndex = 0;
        Assert.Contains("실패", Control<TextBox>("LiveInspection").Text);
        Assert.IsTrue(Control<Button>("LiveFailuresButton").IsEnabled);
        Control<ComboBox>("LiveFilter").SelectedIndex = 0;
        Capture("navigation-live-600");
        Invoke("CompleteRun", run);
        Assert.AreEqual(1, Control<TabControl>("Tabs").SelectedIndex);
        Invoke("ShowRun", sample); Control<TabControl>("Tabs").SelectedIndex = 2;
    }
}
