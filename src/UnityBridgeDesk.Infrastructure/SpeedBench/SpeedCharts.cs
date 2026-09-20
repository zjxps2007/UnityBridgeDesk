using System.Globalization;
using System.Xml.Linq;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record ChartSeries(string Release, double? Mean, double? Minimum, double? Maximum, int Valid, int Finished,
    int Planned, int CallsPassed, int CallsObserved, int CallsUnknown, int PaletteIndex = 0)
{
    public double? SuccessPercent => Finished == 0 ? null : 100d * Valid / Finished;
    public string Color => SpeedCharts.Colors[PaletteIndex % SpeedCharts.Colors.Length];
}
public sealed record ReportChart(string Experiment, string Condition, string Unit, ChartSeries[] Series)
{
    public string Label => Experiment + " · " + Condition;
}

public static class SpeedCharts
{
    public static readonly string[] Colors = ["#B7A0D4", "#8DBBB0", "#D5A0B7", "#D4B58B", "#91AEC8", "#B5BA8C", "#BFA1A1", "#AAA9D2"];
    // Measurement order rotates by block; presentation always follows the run's saved release order.
    public static ReportChart[] Build(SpeedReport report) => report.Summaries.GroupBy(s => (s.Experiment, s.Condition, s.Unit)).Select(g =>
        new ReportChart(g.Key.Experiment, g.Key.Condition, g.Key.Unit, report.Run.Releases.Select((release, index) =>
        {
            var summary = g.SingleOrDefault(s => s.Release == release.Tag);
            var stability = report.Stability.SingleOrDefault(x => x.Experiment == g.Key.Experiment &&
                x.Condition == g.Key.Condition && x.Release == release.Tag);
            return new ChartSeries(release.Tag, summary?.Mean, stability?.MinimumMs, stability?.MaximumMs, stability?.Valid ?? 0,
                stability?.Evaluated ?? 0, stability?.Planned ?? 0, stability?.CallsVerified ?? 0,
                stability?.CallsObserved ?? 0, stability?.CallsUnknown ?? 0, index);
        }).ToArray())).ToArray();
    public static double AxisMaximum(ReportChart chart)
    {
        double max = chart.Series.Max(s => s.Mean ?? 0);
        if (max <= 0) return 1;
        double step = Math.Pow(10, Math.Floor(Math.Log10(max))) / 2;
        return Math.Ceiling(max * 1.12 / step) * step;
    }
    public static void WriteSvg(string path, SpeedReport report) => WriteSvg(path, report, Build(report), false);
    public static void WriteSvg(string path, SpeedReport report, ReportChart[] groups, bool stability)
    {
        XNamespace ns = "http://www.w3.org/2000/svg";
        var svg = new XElement(ns + "svg", new XAttribute("viewBox", $"0 0 1100 {110 + groups.Length * 480}"),
            new XAttribute("width", 1100), new XAttribute("height", 110 + groups.Length * 480), new XAttribute("role", "img"),
            new XElement(ns + "title", "UnityBridge 조건별 측정 결과"),
            new XElement(ns + "rect", new XAttribute("width", "100%"), new XAttribute("height", "100%"), new XAttribute("fill", "#FFFEFC")));
        void Text(double x, double y, string text, int size = 14, string anchor = "start") => svg.Add(new XElement(ns + "text", new XAttribute("x", x),
            new XAttribute("y", y), new XAttribute("font-family", "Malgun Gothic, sans-serif"), new XAttribute("font-size", size),
            new XAttribute("text-anchor", anchor), new XAttribute("fill", "#40394F"), text));
        Text(28, 36, "UnityBridge · " + (stability ? "유효 완료율" : "조건별 평균 완료 시간"), 24);
        Text(28, 65, $"{report.Run.StartedAt:yyyy-MM-dd HH:mm} · 기준 {report.Baseline} · Unity 준비 완료 후 측정 · Unity 시작 시간 제외");
        Text(28, 90, "버전 순서·색상 고정 · 실패는 0ms로 표시하지 않음 · 조건별 축 범위 다름 · 관찰값이며 우열 확정 아님");
        double top = 135;
        foreach (var group in groups)
        {
            Text(28, top, group.Label + " · " + (stability ? "유효 완료율 (%)" : SpeedReport.UnitLabel(group.Experiment)), 19);
            Text(28, top + 26, report.ProtocolDescription(group.Experiment, group.Condition).Split('\n')[1], 13);
            double max = stability ? 100 : AxisMaximum(group), bottom = top + 300, step = 950d / Math.Max(1, group.Series.Length);
            for (int i = 0; i <= 4; i++)
            {
                double y = bottom - i * 55;
                svg.Add(new XElement(ns + "line", new XAttribute("x1", 90), new XAttribute("x2", 1040),
                    new XAttribute("y1", y), new XAttribute("y2", y), new XAttribute("stroke", "#E4DFEC")));
                Text(78, y + 4, (max * i / 4).ToString("0.##", CultureInfo.InvariantCulture), 12, "end");
            }
            for (int i = 0; i < group.Series.Length; i++)
            {
                var series = group.Series[i]; double x = 90 + step * (i + .5), bar = Math.Min(66, step * .55);
                double? value = stability ? series.SuccessPercent : series.Mean;
                if (value is { } n)
                {
                    double height = n / max * 220;
                    svg.Add(new XElement(ns + "rect", new XAttribute("x", x - bar / 2), new XAttribute("y", bottom - height),
                        new XAttribute("width", bar), new XAttribute("height", height), new XAttribute("fill", series.Color)));
                    Text(x, bottom - height - 12, stability ? $"{n:F1}%" : SpeedReport.Time(n), 17, "middle");
                }
                else Text(x, bottom - 18, "유효 측정 없음", 12, "middle");
                Text(x, bottom + 25, series.Release, 14, "middle");
                Text(x, bottom + 48, $"유효 {series.Valid}/{(stability ? series.Finished : series.Planned)}회", 12, "middle");
            }
            Text(28, bottom + 82, stability ? "분모: 종료 시행 중 환경 준비·호환성 오류 제외. 사용자 중단·미수행 제외. 정리 실패는 포함." :
                "평균: 버전별 유효 실험 전체. 기준 대비 비교는 두 버전 모두 유효한 실험만 사용하므로 평균이 다를 수 있음.", 13);
            top += 480;
        }
        new XDocument(svg).Save(path);
    }
}
