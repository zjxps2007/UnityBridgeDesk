using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace UnityBridgeDesk.Desktop.Controls;

public sealed partial class AshaCompanion
{
    private readonly HashSet<FrameworkElement> inputFrames = [];
    private void ObserveInputFrame(FrameworkElement element)
    {
        if (!inputFrames.Add(element)) return;
        // Enabled state need not trigger a WPF layout pass. A restored field must regain
        // its roof immediately instead of waiting for a resize or unrelated scroll.
        element.IsEnabledChanged += InputFrameChanged;
        element.IsVisibleChanged += InputFrameChanged;
    }
    private void InputFrameChanged(object sender, DependencyPropertyChangedEventArgs e)
    { dirty = true; RefreshAfterLayout(); }
    private void PruneInputFrames(bool all = false)
    {
        foreach (var element in inputFrames.Where(e => all || !ReferenceEquals(e, surface) && !surface.IsAncestorOf(e)).ToArray())
        {
            element.IsEnabledChanged -= InputFrameChanged;
            element.IsVisibleChanged -= InputFrameChanged;
            inputFrames.Remove(element);
        }
    }
    private void Collect(DependencyObject node, List<Rect> obstacles, List<AshaLedge> ledges)
    {
        if (ReferenceEquals(node, layer) || node is UIElement { IsVisible: false } or UIElement { Opacity: <= 0 }) return;
        if (node is FrameworkElement element)
        {
            Rect rect = VisibleBounds(element, new Rect(element.RenderSize), out bool complete);
            // Prune clipped branches before asking WPF for any text drawing.
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return;
            if (ReferenceEquals(element, home)) return;
            if (element is SpeedChartView chart) { CollectChart(chart, rect, obstacles, ledges); return; }
            if (element is TextBlock text)
            {
                var lines = AshaTextLayout.Read(text);
                string id = PerchId(text);
                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i]; Rect ink = VisibleBounds(text, line.Bounds, out bool visible);
                    AddObstacle(ink, obstacles);
                    if (line.TopVisible && text.IsEnabled)
                        AddVisibleLedge(text, line.Bounds, id + "/line:" + i, AshaLedgeKind.TextLine, ledges);
                }
                // Until WPF has drawn a newly changed TextBlock, avoid crossing its pending content.
                if (lines.Count == 0 && !string.IsNullOrWhiteSpace(text.Text)) AddObstacle(rect, obstacles);
                return;
            }
            if (element is ButtonBase button)
            {
                // Native hit targets often stretch beyond their paint (checkboxes, expander
                // headers and text-only buttons). A full frame must actually be painted.
                if (HasPaintedFrame(button, rect, out var paintedTop))
                {
                    AddObstacle(rect, obstacles);
                    AddLedge(paintedTop, button.IsEnabled, PerchId(button), AshaLedgeKind.Button, ledges);
                }
                else CollectButtonContent(button, obstacles, ledges);
                return;
            }
            if (element is ComboBox or TextBoxBase or PasswordBox)
            {
                ObserveInputFrame(element);
                // Fields were protected as obstacles only, so one-way gravity passed through
                // them. Their painted outer roof supports feet; values, carets, arrows and
                // popup/list internals remain protected and do not become extra platforms.
                AddObstacle(rect, obstacles);
                if (element.IsEnabled && HasPaintedFrame(element, rect, out var fieldTop))
                    AddLedge(fieldTop, true, PerchId(element), AshaLedgeKind.InputField, ledges);
                return;
            }
            if (node is ScrollBar or ProgressBar ||
                node is Selector && node is not TabControl)
            {
                AddObstacle(rect, obstacles); return;
            }
            // Explicit structural lines remain connectors/fallbacks, not the primary destinations.
            var edge = AshaSurface.GetEdge(element);
            if (edge != AshaEdge.None)
                AddVisibleLedge(element, new Rect(element.RenderSize), PerchId(element), AshaLedgeKind.Edge, ledges, edge == AshaEdge.Bottom);
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Collect(VisualTreeHelper.GetChild(node, i), obstacles, ledges);
    }

    private bool HasPaintedFrame(DependencyObject node, Rect target, out Rect top)
    {
        top = Rect.Empty;
        if (node is UIElement { IsVisible: false } or UIElement { Opacity: <= 0 } || node is TextBlock) return false;
        if (node is FrameworkElement element && VisualTreeHelper.GetDrawing(element) is { } drawing)
            foreach (var painted in AshaPaintedGeometry.Read(drawing))
            {
                var rect = VisibleBounds(element, painted.Bounds, out _);
                if (!rect.IsEmpty && Math.Abs(rect.Left - target.Left) < .5 && Math.Abs(rect.Top - target.Top) < .5 &&
                    Math.Abs(rect.Width - target.Width) < .5 && Math.Abs(rect.Height - target.Height) < .5)
                {
                    if (painted.TopVisible) top = VisibleEdge(element, painted.Top);
                    return true;
                }
            }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (HasPaintedFrame(VisualTreeHelper.GetChild(node, i), target, out top)) return true;
        return false;
    }

    private void CollectButtonContent(DependencyObject node, List<Rect> obstacles, List<AshaLedge> ledges)
    {
        if (node is UIElement { IsVisible: false } or UIElement { Opacity: <= 0 }) return;
        if (node is TextBlock) { Collect(node, obstacles, ledges); return; }
        if (node is FrameworkElement element && VisualTreeHelper.GetDrawing(element) is { } drawing)
        {
            int part = 0;
            foreach (var painted in AshaPaintedGeometry.Read(drawing))
            {
                var rect = VisibleBounds(element, painted.Bounds, out bool complete);
                AddObstacle(rect, obstacles);
                if (element.IsEnabled && painted.TopVisible)
                    AddVisibleLedge(element, painted.Top, PerchId(element) + "/paint:" + part++, AshaLedgeKind.Button, ledges);
            }
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) CollectButtonContent(VisualTreeHelper.GetChild(node, i), obstacles, ledges);
    }

    private static void AddObstacle(Rect rect, List<Rect> obstacles)
    {
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return;
        // Horizontal clearance does not move the physical top of the supporting surface.
        obstacles.Add(new Rect(rect.Left - 2, rect.Top, rect.Width + 4, rect.Height + 2));
    }
    private void AddLedge(Rect rect, bool complete, string id, AshaLedgeKind kind, List<AshaLedge> ledges, bool bottom = false)
    {
        if (complete && !rect.IsEmpty && rect.Width >= host.Width * .3)
            ledges.Add(new(rect.Left, rect.Right, bottom ? rect.Bottom : rect.Top, id, kind));
    }
    private void AddVisibleLedge(FrameworkElement element, Rect local, string id, AshaLedgeKind kind, List<AshaLedge> ledges, bool bottom = false)
    {
        if (VisibleEdge(element, local, bottom) is { IsEmpty: false } top) AddLedge(top, true, id, kind, ledges);
    }
    private Rect VisibleEdge(FrameworkElement element, Rect local, bool bottom = false)
    {
        if (local.IsEmpty) return Rect.Empty;
        var transform = element.TransformToVisual(layer);
        Point a = transform.Transform(local.TopLeft), b = transform.Transform(local.TopRight), c = transform.Transform(local.BottomLeft);
        if (Math.Abs(a.Y - b.Y) > .001 || Math.Abs(a.X - c.X) > .001 || b.X < a.X || c.Y < a.Y) return Rect.Empty;
        Rect original = transform.TransformBounds(local), visible = VisibleBounds(element, local, out _);
        double y = bottom ? original.Bottom : original.Top;
        if (visible.IsEmpty || visible.Width <= 0 || y < visible.Top - .01 || y > visible.Bottom + .01) return Rect.Empty;
        return new(visible.Left, y, visible.Width, .01);
    }
    private Rect VisibleBounds(FrameworkElement element, Rect local, out bool complete)
    {
        complete = false;
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0 || local.IsEmpty) return Rect.Empty;
        var transform = element.TransformToVisual(layer);
        Rect rect = transform.TransformBounds(local), unclipped = rect;
        // A sloped/rotated control must not create an imaginary horizontal top.
        Point origin = transform.Transform(new Point()), x = transform.Transform(new Point(1, 0)), y = transform.Transform(new Point(0, 1));
        bool horizontal = Math.Abs(x.Y - origin.Y) < .001 && Math.Abs(y.X - origin.X) < .001;
        for (DependencyObject? parent = element; parent is Visual; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is UIElement visual && (visual.ClipToBounds || visual is ScrollContentPresenter))
                rect.Intersect(visual.TransformToVisual(layer).TransformBounds(new Rect(visual.RenderSize)));
            if (parent is Visual clipped && VisualTreeHelper.GetClip(clipped) is { } clip)
                rect.Intersect(clipped.TransformToVisual(layer).TransformBounds(clip.Bounds));
            if (rect.IsEmpty || ReferenceEquals(parent, surface)) break;
        }
        complete = horizontal && !rect.IsEmpty && (rect.TopLeft - unclipped.TopLeft).Length < .01 &&
            Math.Abs(rect.Width - unclipped.Width) < .01 && Math.Abs(rect.Height - unclipped.Height) < .01;
        return rect;
    }
}
