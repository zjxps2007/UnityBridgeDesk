using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnityBridgeDesk.Desktop.Controls;

namespace UnityBridgeDesk.Desktop.Tests;

/// <summary>Checks rendered opaque silhouettes against each other and the physics foot anchor.</summary>
internal static class CharacterScaleChecks
{
    public static void Verify(string directory)
    {
        var report = new System.Text.StringBuilder("size,dpi,frame,cat_height,fox_height,cat_feet,fox_feet\n");
        var setFrame = typeof(DeskMascot).GetMethod("SetFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (int size in new[] { 96, 112, 144 })
        foreach (double dpi in new[] { 1d, 1.5, 2d })
        {
            var actors = new[] { CompanionKind.Cat, CompanionKind.Fox }
                .Select(kind => new DeskMascot { Character = kind, Width = size, Height = size, MotionEnabled = false }).ToArray();
            try
            {
                foreach (var actor in actors) { actor.Measure(new(size, size)); actor.Arrange(new(0, 0, size, size)); actor.UpdateLayout(); }
                for (int frame = 0; frame < 8; frame++)
                {
                    var bounds = new Rect[2];
                    for (int species = 0; species < 2; species++)
                    {
                        setFrame.Invoke(actors[species], [frame]);
                        actors[species].UpdateLayout();
                        int pixels = (int)(size * dpi);
                        var bitmap = new RenderTargetBitmap(pixels, pixels, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                        bitmap.Render(actors[species]); var data = new byte[pixels * pixels * 4]; bitmap.CopyPixels(data, pixels * 4, 0);
                        int left = pixels, top = pixels, right = -1, bottom = -1;
                        for (int y = 0; y < pixels; y++) for (int x = 0; x < pixels; x++)
                        {
                            if (data[(y * pixels + x) * 4 + 3] <= 192) continue;
                            left = Math.Min(left, x); right = Math.Max(right, x);
                            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                        }
                        if (right < left || left <= 0 || top <= 0 || right >= pixels - 1 || bottom >= pixels - 1)
                            throw new InvalidOperationException($"Missing/clipped silhouette: {size}, {dpi}, {species}, {frame}");
                        bounds[species] = new(left / dpi, top / dpi, (right - left + 1) / dpi, (bottom - top + 1) / dpi);
                        if (Math.Abs(bounds[species].Bottom - size * .89) > 1.5)
                            throw new InvalidOperationException($"Visible shoes miss landing anchor: {size}, {dpi}, {species}, {frame}, {bounds[species]}");
                    }
                    if (frame != 6 && Math.Abs(bounds[0].Height - bounds[1].Height) > 2)
                        throw new InvalidOperationException($"Character height mismatch: {size}, {dpi}, {frame}, {bounds[0].Height} / {bounds[1].Height}");
                    if (frame == 6 && bounds.Any(b => b.Height >= size * .82))
                        throw new InvalidOperationException($"Sleeping pose was enlarged to standing height: {size}, {dpi}, {bounds[0].Height} / {bounds[1].Height}");
                    report.AppendLine(FormattableString.Invariant($"{size},{dpi},{frame},{bounds[0].Height:F2},{bounds[1].Height:F2},{bounds[0].Bottom:F2},{bounds[1].Bottom:F2}"));
                }
            }
            finally { foreach (var actor in actors) actor.DisposeMotion(); }
        }
        File.WriteAllText(Path.Combine(directory, "character-scale.csv"), report.ToString());
    }
}
