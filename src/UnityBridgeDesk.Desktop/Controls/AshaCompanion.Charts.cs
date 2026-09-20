using System.Windows;

namespace UnityBridgeDesk.Desktop.Controls;

public sealed partial class AshaCompanion
{
    private readonly Dictionary<SpeedChartView, Rect> charts = [];
    private readonly AshaChartPlay chartPlay;
    public bool IsPlayingOnChart => chartPlay.Active;

    private void CollectChart(SpeedChartView chart, Rect visible, List<Rect> obstacles, List<AshaLedge> ledges)
    {
        if (!charts.ContainsKey(chart)) chart.CompanionGeometryChanged += ChartChanged;
        charts[chart] = visible;
        string group = PerchId(chart);
        var vicinity = visible; vicinity.Inflate(110, 180);
        bool crossing = route.Any(s => map?.Support(s.From)?.Group == group || map?.Support(s.To)?.Group == group);
        bool visiting = placed && host.IsVisible && (crossing || chartPlay.Active || vicinity.Contains(new Point(Position.X + host.Width / 2, Position.Y + host.Height * .89)));
        chart.SetCompanionVisiting(visiting, this);
        var terrain = chart.GetCompanionTerrain();
        if (terrain.Floor.IsEmpty) { AddObstacle(visible, obstacles); return; }
        foreach (var label in terrain.Labels) AddObstacle(VisibleBounds(chart, label, out _), obstacles);
        foreach (var bar in terrain.Bars)
        {
            AddObstacle(VisibleBounds(chart, bar.Bounds, out _), obstacles);
            var top = VisibleEdge(chart, bar.Bounds);
            if (!top.IsEmpty && top.Width >= host.Width * .3)
                ledges.Add(new(top.Left, top.Right, top.Top, group + "/bar:" + bar.Id, AshaLedgeKind.ChartBar, group, terrain.Reach));
        }
        var floor = VisibleEdge(chart, terrain.Floor);
        if (!floor.IsEmpty && terrain.Bars.Length > 0)
            ledges.Add(new(floor.Left, floor.Right, floor.Top, group + "/floor", AshaLedgeKind.ChartFloor, group, terrain.Reach));
    }
    private void ChartChanged(object? sender, EventArgs e)
    {
        chartPlay.Cancel(clock.Elapsed.TotalSeconds); dirty = true; RefreshAfterLayout();
    }
    private void ResetCharts()
    {
        foreach (var chart in charts.Keys) { chart.CompanionGeometryChanged -= ChartChanged; chart.SetCompanionVisiting(false, this); }
        charts.Clear(); chartPlay.Reset(0);
    }
    private void UpdateChartInput()
    {
        var body = new Rect(Position, new Size(host.Width, host.Height));
        // While crossing a chart, the chart's tooltip/click/keyboard behaviour keeps priority.
        host.IsHitTestVisible = host.Opacity > 0 && !charts.Any(pair => pair.Key.IsVisible && pair.Value.IntersectsWith(body));
    }
}
