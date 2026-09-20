using System.Globalization;
using System.Windows;
using System.Windows.Media;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop;

/// <summary>Order is evidence: no smoothing, sorting by duration, or outlier removal.</summary>
public sealed class SpeedSequenceView : FrameworkElement
{
    private ReportTrial? trial;
    private ReportTrial[]? trials;
    public void SetTrial(ReportTrial? value) { trial = value; trials = null; InvalidateVisual(); }
    public void SetTrials(ReportTrial[] values) { trial = null; trials = values; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        Brush muted = TryFindResource("Muted") as Brush ?? Brushes.DimGray;
        Brush accent = TryFindResource("AccentInk") as Brush ?? Brushes.SlateBlue;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Text(string value, double x, double y) => dc.DrawText(new FormattedText(value, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), 11, muted, dpi), new(x, y));
        var guest = trial?.Result?.Guest;
        var warmups = guest?.WarmupMilliseconds ?? [];
        var samples = trials is null ? guest?.Samples.OrderBy(s => s.Index).ToArray() ?? [] :
            trials.OrderBy(t => t.Order).Select((t, i) => new SpeedSample(i, t.WorkMs ?? double.NaN, 0, t.Included ? "success" : "failed")).ToArray();
        var values = warmups.Concat(samples.Select(s => s.Milliseconds)).ToArray();
        if (values.Length == 0 || ActualWidth < 130) { Text("호출 시간 기록 없음", 0, 16); return; }
        double max = values.Where(double.IsFinite).Where(v => v >= 0).DefaultIfEmpty(1).Max();
        max = Math.Max(1, max) * 1.12;
        double left = 64, right = Math.Max(left + 1, ActualWidth - 12), top = 14, bottom = Math.Max(40, ActualHeight - 32);
        var axis = new Pen(muted, .5);
        dc.DrawLine(axis, new(left, top), new(left, bottom)); dc.DrawLine(axis, new(left, bottom), new(right, bottom));
        Text(SpeedReport.Time(max) + " ms", 0, top - 8); Text("0", 36, bottom - 10);
        Text("1", left, bottom + 5); Text(values.Length.ToString(CultureInfo.InvariantCulture) + "회", right - 32, bottom + 5);
        double X(int i) => left + (right - left) * (values.Length == 1 ? .5 : (double)i / (values.Length - 1));
        if (warmups.Length > 0 && samples.Length > 0)
        {
            double boundary = (X(warmups.Length - 1) + X(warmups.Length)) / 2;
            dc.DrawLine(new Pen(muted, 1) { DashStyle = DashStyles.Dash }, new(boundary, top), new(boundary, bottom));
        }
        Point? previous = null;
        for (int i = 0; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i]) || values[i] < 0) { previous = null; continue; }
            Brush color = i < warmups.Length ? muted : samples[i - warmups.Length].Outcome == "failed" ? Brushes.IndianRed : accent;
            var point = new Point(X(i), bottom - values[i] / max * (bottom - top));
            if (previous is { } p) dc.DrawLine(new Pen(color, 1.3), p, point);
            if (values.Length <= 150 || samples.ElementAtOrDefault(i - warmups.Length)?.Outcome == "failed") dc.DrawEllipse(color, null, point, 2.5, 2.5);
            previous = point;
        }
    }
}
