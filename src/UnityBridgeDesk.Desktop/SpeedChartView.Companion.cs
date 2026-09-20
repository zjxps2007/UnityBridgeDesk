using System.Windows;
using System.Windows.Media;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop;

public readonly record struct ChartFoothold(string Id, Rect Bounds);
public sealed record ChartTerrain(Rect Floor, ChartFoothold[] Bars, Rect[] Labels, double Reach);

public sealed partial class SpeedChartView
{
    private bool companionVisiting;
    private readonly HashSet<object> companionVisitors = [];
    public bool CompanionVisiting => companionVisiting;
    public event EventHandler? CompanionGeometryChanged;
    public void SetCompanionVisiting(bool visiting, object? owner = null)
    {
        owner ??= this;
        if (visiting) companionVisitors.Add(owner); else companionVisitors.Remove(owner);
        visiting = companionVisitors.Count > 0;
        if (companionVisiting == visiting) return;
        companionVisiting = visiting; InvalidateVisual();
    }
    private Rect BarBounds(int index)
    {
        double step = Math.Max(30, ActualWidth - 76) / chart!.Series.Length;
        double bottom = Height - 78, max = stability ? 100 : Infrastructure.SpeedBench.SpeedCharts.AxisMaximum(chart);
        double value = (stability ? chart.Series[index].SuccessPercent : chart.Series[index].Mean) ?? 0;
        double y = bottom - value / max * (bottom - PlotTop), width = Math.Min(62, step * .55);
        return new(60 + step * (index + .5) - width / 2, y, width, Math.Max(0, bottom - y));
    }
    private Rect ValueLabel(int index, Rect bar)
    {
        double step = Math.Max(30, ActualWidth - 76) / chart!.Series.Length;
        double x = 60 + step * index;
        // Keep values readable below the cat's feet. Very short bars use the space beside the bar.
        if (companionVisiting && bar.Height >= 30) return new(x, bar.Top + 6, step, 24);
        if (companionVisiting && bar.Height > 0)
        {
            double room = step / 2 - bar.Width / 2 - 4;
            if (room >= 30) return new(x, bar.Top - 25, room, 24);
        }
        return new(x, bar.Top - 28, step, 24);
    }
    public ChartTerrain GetCompanionTerrain()
    {
        if (chart is null || chart.Series.Length == 0 || ActualWidth <= 76 || Height <= PlotTop + 78)
            return new(Rect.Empty, [], [], 250);
        double bottom = Height - 78, step = (ActualWidth - 76) / chart.Series.Length;
        var bars = new List<ChartFoothold>(); var labels = new List<Rect>();
        labels.Add(new(0, 0, 53, bottom + 12));
        labels.Add(new(60, bottom + 10, ActualWidth - 76, 57));
        for (int i = 0; i < chart.Series.Length; i++)
        {
            double? value = stability ? chart.Series[i].SuccessPercent : chart.Series[i].Mean;
            if (value is { } n && double.IsFinite(n) && n > 0)
            {
                var body = BarBounds(i);
                // Identity includes the condition/metric so a changed chart cannot inherit an old game.
                bars.Add(new(chart.Experiment + "/" + chart.Condition + "/" + stability + "/" + chart.Series[i].Release, body));
                var slot = ValueLabel(i, body);
                labels.Add(FormatText(stability ? $"{n:F1}%" : SpeedReport.Time(n), slot.Width, Brushes.Black, 16, true)
                    .BuildGeometry(new Point(slot.X, slot.Y)).Bounds);
            }
            else labels.Add(new(60 + step * i, bottom - (value is null ? 45 : 28), step, 24));
        }
        return new(new(60, bottom, ActualWidth - 76, 1), bars.ToArray(), labels.ToArray(), Math.Clamp(step + 96, 250, 720));
    }
}
