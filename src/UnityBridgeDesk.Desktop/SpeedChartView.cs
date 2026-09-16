using System.Globalization;
using System.Windows;
using System.Windows.Media;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop;

public sealed class SpeedChartView : FrameworkElement
{
    private ReportChart? chart;
    private bool stability;
    public void Show(ReportChart? value, bool showStability)
    {
        chart = value; stability = showStability;
        Height = value is null ? 65 : value.Series.Length * 54 + 60;
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var ink = (Brush)FindResource("Ink"); var muted = (Brush)FindResource("Muted"); var line = (Brush)FindResource("Line");
        void Text(double x, double y, string value, double width, Brush? brush = null)
        {
            var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Malgun Gothic"), 12, brush ?? ink, VisualTreeHelper.GetDpi(this).PixelsPerDip)
                { MaxTextWidth = Math.Max(10, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(text, new Point(x, y));
        }
        if (chart is null) { Text(0, 15, "기록을 선택하면 그래프가 표시됩니다.", ActualWidth, muted); return; }
        const double left = 120, right = 160;
        double width = Math.Max(30, ActualWidth - left - right);
        double max = stability ? 100 : chart.Series.Max(s => s.Maximum ?? s.Mean ?? 0);
        Text(left, 0, "0", 35, muted);
        Text(left + width - 55, 0, stability ? "100%" : SpeedReport.Time(max), 80, muted);
        for (int i = 0; i < chart.Series.Length; i++)
        {
            var s = chart.Series[i]; double y = 27 + i * 54;
            Text(0, y + 3, s.Release, left - 8);
            dc.DrawRoundedRectangle((Brush)FindResource("Tint"), null, new Rect(left, y, width, 24), 5, 5);
            double? value = stability ? s.SuccessPercent : s.Mean;
            if (value is { } n && max > 0)
            {
                var fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(SpeedCharts.Colors[i % SpeedCharts.Colors.Length]));
                dc.DrawRoundedRectangle(fill, null, new Rect(left, y, Math.Clamp(n / max, 0, 1) * width, 24), 5, 5);
                if (!stability && s.Minimum is { } low && s.Maximum is { } high)
                {
                    var pen = new Pen(ink, 1.5); double a = left + low / max * width, b = left + high / max * width;
                    dc.DrawLine(pen, new Point(a, y + 12), new Point(b, y + 12));
                    dc.DrawLine(pen, new Point(a, y + 7), new Point(a, y + 17));
                    dc.DrawLine(pen, new Point(b, y + 7), new Point(b, y + 17));
                }
            }
            Text(left + width + 10, y + 2, value is null ? "측정 없음" : stability ? $"{value:F1}%" : SpeedReport.Time(value) + " ms", right - 10);
            Text(left, y + 29, stability ? $"유효 {s.Valid} / 종료 {s.Finished} · 계획 {s.Planned}" : $"유효 시행 {s.Valid} · {chart.Unit}", width + right, muted);
        }
        dc.DrawLine(new Pen(line, 1), new Point(left, 22), new Point(left, Height - 12));
    }
}
