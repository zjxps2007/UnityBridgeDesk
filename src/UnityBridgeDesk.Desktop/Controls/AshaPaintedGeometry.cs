using System.Windows;
using System.Windows.Media;

namespace UnityBridgeDesk.Desktop.Controls;

/// <summary>Painted template shapes, excluding transparent hit targets. Text has its own line reader.</summary>
internal readonly record struct AshaPaintedPart(Rect Bounds, bool Complete, Rect Top, bool TopVisible);
internal static class AshaPaintedGeometry
{
    public static IEnumerable<AshaPaintedPart> Read(Drawing drawing) => Read(drawing, Matrix.Identity, null);
    private static IEnumerable<AshaPaintedPart> Read(Drawing drawing, Matrix transform, Rect? clip)
    {
        if (drawing is DrawingGroup group)
        {
            if (group.Opacity <= 0) yield break;
            var combined = group.Transform?.Value ?? Matrix.Identity; combined.Append(transform);
            if (group.ClipGeometry is { } geometry)
            {
                var bounds = Rect.Transform(geometry.Bounds, combined);
                if (clip is { } parent) bounds.Intersect(parent);
                clip = bounds;
            }
            foreach (var child in group.Children)
                foreach (var rect in Read(child, combined, clip)) yield return rect;
        }
        else if (drawing is GeometryDrawing shape && (Visible(shape.Brush) || shape.Pen is { Thickness: > 0 } pen && Visible(pen.Brush)) ||
                 drawing is ImageDrawing { ImageSource: not null })
        {
            // Reject tilted shapes rather than inventing a horizontal ledge around their bounding box.
            if (Math.Abs(transform.M12) > .001 || Math.Abs(transform.M21) > .001) yield break;
            var rect = Rect.Transform(drawing.Bounds, transform); var original = rect;
            if (clip is { } window) rect.Intersect(window);
            Rect top = drawing is GeometryDrawing vector ? PaintedTop(vector, transform) : Rect.Empty;
            bool topVisible = !top.IsEmpty && (clip is not { } clipping || clipping.Top <= top.Top + .01 && clipping.Bottom > top.Top);
            if (clip is { } topClip && !top.IsEmpty) top.Intersect(topClip);
            if (!rect.IsEmpty) yield return new(rect, (rect.TopLeft - original.TopLeft).Length < .01 &&
                Math.Abs(rect.Width - original.Width) < .01 && Math.Abs(rect.Height - original.Height) < .01, top, topVisible && !top.IsEmpty);
        }
    }
    private static Rect PaintedTop(GeometryDrawing drawing, Matrix transform)
    {
        Geometry? paint = Visible(drawing.Brush) ? drawing.Geometry : null;
        if (drawing.Pen is { Thickness: > 0 } pen && Visible(pen.Brush))
        {
            var stroke = drawing.Geometry.GetWidenedPathGeometry(pen, .05, ToleranceType.Absolute);
            paint = paint is null ? stroke : Geometry.Combine(paint, stroke, GeometryCombineMode.Union, null, .05, ToleranceType.Absolute);
        }
        if (paint is null || paint.Bounds.IsEmpty) return Rect.Empty;
        double roof = Rect.Transform(paint.Bounds, transform).Top;
        var path = paint.GetFlattenedPathGeometry(.05, ToleranceType.Absolute);
        var mapping = path.Transform?.Value ?? Matrix.Identity; mapping.Append(transform);
        var spans = new List<(double Left, double Right)>();
        void Edge(Point start, Point end)
        {
            start = mapping.Transform(start); end = mapping.Transform(end);
            if (Math.Abs(start.Y - roof) < .08 && Math.Abs(end.Y - roof) < .08 && Math.Abs(start.Y - end.Y) < .01)
                spans.Add((Math.Min(start.X, end.X), Math.Max(start.X, end.X)));
        }
        foreach (var figure in path.Figures)
        {
            Point previous = figure.StartPoint;
            foreach (var segment in figure.Segments)
            {
                if (segment is LineSegment line) { Edge(previous, line.Point); previous = line.Point; }
                else if (segment is PolyLineSegment poly)
                    foreach (var point in poly.Points) { Edge(previous, point); previous = point; }
            }
            if (figure.IsClosed) Edge(previous, figure.StartPoint);
        }
        if (spans.Count == 0) return Rect.Empty;
        var widest = spans.MaxBy(s => s.Right - s.Left);
        return new(widest.Left, roof, widest.Right - widest.Left, .01);
    }
    private static bool Visible(Brush? brush) => brush is { Opacity: > 0 } && brush switch
    {
        SolidColorBrush solid => solid.Color.A > 0,
        GradientBrush gradient => gradient.GradientStops.Any(stop => stop.Color.A > 0),
        _ => true
    };
}
