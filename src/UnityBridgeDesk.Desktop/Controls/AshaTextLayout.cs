using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UnityBridgeDesk.Desktop.Controls;

public readonly record struct AshaTextLine(Rect Bounds, bool Complete, bool TopVisible = true);

/// <summary>Reads rendered glyphs, so wrapping, padding, alignment and inline fonts match the screen.</summary>
public static class AshaTextLayout
{
    public static IReadOnlyList<AshaTextLine> Read(TextBlock text)
    {
        var runs = new List<(Rect Ink, double Baseline, bool Complete, bool TopVisible)>();
        if (VisualTreeHelper.GetDrawing(text) is { } drawing) Collect(drawing, Matrix.Identity, null, runs);
        var lines = new List<(Rect Ink, double Baseline, bool Complete, bool TopVisible)>();
        foreach (var run in runs.OrderBy(r => r.Baseline).ThenBy(r => r.Ink.Left))
        {
            int index = lines.FindLastIndex(l => Math.Abs(l.Baseline - run.Baseline) < .75);
            if (index < 0) lines.Add(run);
            else
            {
                var line = lines[index]; line.Ink.Union(run.Ink);
                lines[index] = (line.Ink, line.Baseline, line.Complete && run.Complete, line.TopVisible && run.TopVisible);
            }
        }
        return lines.Where(l => !l.Ink.IsEmpty).Select(l => new AshaTextLine(l.Ink, l.Complete, l.TopVisible)).ToArray();
    }

    private static void Collect(Drawing drawing, Matrix transform, Rect? clip,
        List<(Rect Ink, double Baseline, bool Complete, bool TopVisible)> runs)
    {
        if (drawing is DrawingGroup group)
        {
            if (group.Opacity <= 0) return;
            var combined = group.Transform?.Value ?? Matrix.Identity; combined.Append(transform);
            if (group.ClipGeometry is { } geometry)
            {
                var bounds = Rect.Transform(geometry.Bounds, combined);
                if (clip is { } parent) bounds.Intersect(parent);
                clip = bounds;
            }
            foreach (var child in group.Children) Collect(child, combined, clip, runs);
        }
        else if (drawing is GlyphRunDrawing { GlyphRun: { } glyph, ForegroundBrush: { Opacity: > 0 } } ink && !ink.Bounds.IsEmpty)
        {
            // Rotated text is an obstacle, not a horizontal platform.
            bool horizontal = Math.Abs(transform.M12) < .001 && Math.Abs(transform.M21) < .001;
            var bounds = Rect.Transform(ink.Bounds, transform); var visible = bounds;
            if (clip is { } window) visible.Intersect(window);
            bool complete = !visible.IsEmpty && (visible.TopLeft - bounds.TopLeft).Length < .01 &&
                Math.Abs(visible.Width - bounds.Width) < .01 && Math.Abs(visible.Height - bounds.Height) < .01;
            // Side clipping shortens a line; clipping its original top removes the landing edge.
            if (!visible.IsEmpty) runs.Add((visible, transform.Transform(glyph.BaselineOrigin).Y, horizontal && complete,
                horizontal && Math.Abs(visible.Top - bounds.Top) < .01));
        }
    }
}
