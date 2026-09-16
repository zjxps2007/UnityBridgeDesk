using System.Globalization;
using System.Xml.Linq;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record ChartSeries(string Release, double? Mean, double? Minimum, double? Maximum, int Valid, int Finished,
    int Planned, int CallsPassed, int CallsObserved, int CallsUnknown)
{
    public double? SuccessPercent => Finished == 0 ? null : 100d * Valid / Finished;
}
public sealed record ReportChart(string Experiment, string Condition, string Unit, ChartSeries[] Series)
{
    public string Label => Experiment + " · " + Condition;
}

public static class SpeedCharts
{
    public static readonly string[] Colors = ["#B7A0D4", "#8DBBB0", "#D5A0B7", "#D4B58B", "#91AEC8", "#B5BA8C", "#BFA1A1", "#AAA9D2"];
    public static ReportChart[] Build(SpeedReport report) => report.Summaries.GroupBy(s => (s.Experiment, s.Condition, s.Unit)).Select(g =>
        new ReportChart(g.Key.Experiment, g.Key.Condition, g.Key.Unit, g.Select(s =>
        {
            var stability = report.Stability.Single(x => x.Experiment == s.Experiment && x.Condition == s.Condition && x.Release == s.Release);
            return new ChartSeries(s.Release, s.Mean, stability.MinimumMs, stability.MaximumMs, stability.Valid,
                stability.Finished, stability.Planned, stability.CallsVerified, stability.CallsObserved, stability.CallsUnknown);
        }).ToArray())).ToArray();
    public static void WriteSvg(string path, SpeedReport report)
    {
        XNamespace ns = "http://www.w3.org/2000/svg";
        var groups = Build(report); double height = 125 + groups.Sum(g => 92 + g.Series.Length * 48);
        var svg = new XElement(ns + "svg", new XAttribute("viewBox", $"0 0 1000 {height}"),
            new XAttribute("width", 1000), new XAttribute("height", height), new XAttribute("role", "img"),
            new XElement(ns + "title", "UnityBridge 속도와 안정성 결과"),
            new XElement(ns + "rect", new XAttribute("width", "100%"), new XAttribute("height", "100%"), new XAttribute("fill", "#FFFEFC")));
        void Text(double x, double y, string text, int size = 14) => svg.Add(new XElement(ns + "text", new XAttribute("x", x),
            new XAttribute("y", y), new XAttribute("font-family", "Malgun Gothic, sans-serif"), new XAttribute("font-size", size),
            new XAttribute("fill", "#40394F"), text));
        Text(28, 34, "UnityBridge · 조건별 속도와 안정성", 23);
        Text(28, 62, "막대: 유효 시행 평균 · 선: 최소–최대(신뢰구간 아님) · 완료율: 유효 완료 / 종료 시행(사용자 중단 제외)");
        Text(28, 84, "실패·미수행은 0ms가 아니다. 서로 다른 조건의 막대 길이를 직접 비교하지 않는다. 기준값은 같은 시행을 공유할 수 있다.");
        double y = 125;
        foreach (var group in groups)
        {
            Text(28, y, group.Label + " [" + group.Unit + "]", 18); y += 22;
            double max = group.Series.Max(s => s.Maximum ?? s.Mean ?? 0);
            Text(175, y, "0"); Text(590, y, max.ToString("N2", CultureInfo.InvariantCulture));
            Text(730, y, "유효 완료 / 종료 · 계획"); y += 23;
            foreach (var (s, i) in group.Series.Select((s, i) => (s, i)))
            {
                Text(28, y + 16, s.Release);
                if (s.Mean is { } mean && max > 0)
                {
                    svg.Add(new XElement(ns + "rect", new XAttribute("x", 175), new XAttribute("y", y), new XAttribute("width", mean / max * 430),
                        new XAttribute("height", 22), new XAttribute("rx", 5), new XAttribute("fill", Colors[i % Colors.Length])));
                    if (s.Minimum is { } low && s.Maximum is { } high)
                        svg.Add(new XElement(ns + "line", new XAttribute("x1", 175 + low / max * 430), new XAttribute("x2", 175 + high / max * 430),
                            new XAttribute("y1", y + 11), new XAttribute("y2", y + 11), new XAttribute("stroke", "#584278"), new XAttribute("stroke-width", 2)));
                }
                Text(618, y + 16, SpeedReport.Time(s.Mean));
                Text(730, y + 16, $"{s.Valid}/{s.Finished} · 계획 {s.Planned}" + (s.SuccessPercent is { } rate ? $" ({rate:F1}%)" : " · 측정 없음"));
                y += 48;
            }
            y += 47;
        }
        new XDocument(svg).Save(path);
    }
}
