using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Desktop;

public sealed partial class SpeedChartView : FrameworkElement
{
    public const double PlotTop = 104;
    public const double MinimumPlotHeight = 254;
    private ReportChart? chart;
    private bool stability;
    private int selected = -1;
    private int hovered = -1;
    public event Action<string>? ReleaseSelected;
    public SpeedChartView() { Focusable = true; Cursor = Cursors.Hand; }
    public void Show(ReportChart? value, bool showStability)
    {
        string? previous = chart is not null && selected >= 0 && selected < chart.Series.Length ? chart.Series[selected].Release : null;
        chart = value; stability = showStability; selected = value is null ? -1 : Array.FindIndex(value.Series, s => s.Release == previous); hovered = -1;
        Height = value is null ? 65 : 270;
        MinWidth = value is null ? 0 : 70 + value.Series.Length * 96;
        InvalidateVisual();
        CompanionGeometryChanged?.Invoke(this, EventArgs.Empty);
    }
    private int HitSeries(Point point) => chart is null || chart.Series.Length == 0 || ActualWidth <= 76 || point.X < 60 || point.X > ActualWidth - 16
        ? -1 : Math.Clamp((int)((point.X - 60) / ((ActualWidth - 76) / chart.Series.Length)), 0, chart.Series.Length - 1);
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitSeries(e.GetPosition(this)); if (hovered == hit) return;
        hovered = hit;
        ToolTip = hit < 0 || chart is null ? null : DescribeSeries(hit);
        InvalidateVisual();
    }
    protected override void OnMouseLeave(MouseEventArgs e)
    { base.OnMouseLeave(e); hovered = -1; ToolTip = null; InvalidateVisual(); }
    private string DescribeSeries(int index)
    {
        var series = chart!.Series[index];
        string value = stability ? series.SuccessPercent is { } p ? $"{p:F1}%" : "유효 측정 없음" : SpeedReport.Time(series.Mean) + " " + SpeedReport.Unit(chart.Experiment);
        return $"{series.Release}\n{value}\n유효 {series.Valid}/{(stability ? series.Finished : series.Planned)}회\n선택하면 이 버전의 시행을 확인합니다.";
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        int hit = HitSeries(e.GetPosition(this)); if (hit < 0) return;
        selected = hit;
        Focus(); Activate(); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (chart is null || chart.Series.Length == 0) return;
        if (e.Key is Key.Left or Key.Right)
        { selected = Math.Clamp(selected + (e.Key == Key.Left ? -1 : 1), 0, chart.Series.Length - 1); InvalidateVisual(); e.Handled = true; }
        if (e.Key is Key.Enter or Key.Space) { if (selected < 0) selected = 0; Activate(); e.Handled = true; }
    }
    private void Activate() { InvalidateVisual(); if (chart is not null) ReleaseSelected?.Invoke(chart.Series[selected].Release); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var ink = (Brush)FindResource("Ink"); var muted = (Brush)FindResource("Muted"); var line = (Brush)FindResource("Line");
        void Text(double x, double y, string value, double width, Brush? brush = null, double size = 12, bool bold = false)
        {
            var text = FormatText(value, width, brush ?? ink, size, bold);
            dc.DrawText(text, new Point(x, y));
        }
        if (chart is null || chart.Series.Length == 0) { Text(0, 15, "기록을 선택하면 그래프가 표시됩니다.", ActualWidth, muted); return; }
        const double left = 60, top = PlotTop;
        double bottom = Height - 78;
        double width = Math.Max(30, ActualWidth - left - 16), step = width / chart.Series.Length;
        double max = stability ? 100 : SpeedCharts.AxisMaximum(chart);
        for (int i = 0; i <= 4; i++)
        {
            double y = bottom - (bottom - top) * i / 4;
            dc.DrawLine(new Pen(line, 1), new(left, y), new(left + width, y));
            Text(0, y - 8, (max * i / 4).ToString("0.##", CultureInfo.InvariantCulture), left - 7, muted, 10);
        }
        Text(0, 5, stability ? "%" : "ms", left - 7, muted);
        for (int i = 0; i < chart.Series.Length; i++)
        {
            var s = chart.Series[i]; double x = left + step * i;
            if (selected == i || hovered == i) dc.DrawRectangle((Brush)FindResource("Tint"), selected == i ? new Pen((Brush)FindResource("AccentInk"), 1) : null, new Rect(x + 2, top - 30, step - 4, Height - top + 22));
            double? value = stability ? s.SuccessPercent : s.Mean;
            if (value is { } n)
            {
                var body = BarBounds(i); var label = ValueLabel(i, body);
                dc.DrawRectangle(new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.Color)), null, body);
                Text(label.X, label.Y, stability ? $"{n:F1}%" : SpeedReport.Time(n), label.Width, size: 16, bold: true);
            }
            else Text(x, bottom - 45, "유효 측정 없음", step, muted, 11);
            Text(x, bottom + 12, s.Release, step, bold: true);
            Text(x, bottom + 37, stability ? $"유효 {s.Valid}/{s.Finished}" : $"유효 {s.Valid}/{s.Planned}회", step, muted, 11);
        }
    }
    private FormattedText FormatText(string value, double width, Brush brush, double size = 12, bool bold = false) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Malgun Gothic"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { MaxTextWidth = Math.Max(10, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
}
