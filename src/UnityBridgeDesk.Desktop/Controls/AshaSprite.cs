using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UnityBridgeDesk.Desktop.Controls;

public readonly record struct AshaExpression(double GazeX, double GazeY, double Ears, double Tail, double Breath = 0, double Paws = 0, double Stretch = 0, double Weight = 0)
{
    public static AshaExpression Blend(AshaExpression from, AshaExpression to, double amount)
    {
        double t = Math.Clamp(amount, 0, 1);
        return new(from.GazeX + (to.GazeX - from.GazeX) * t, from.GazeY + (to.GazeY - from.GazeY) * t,
            from.Ears + (to.Ears - from.Ears) * t, from.Tail + (to.Tail - from.Tail) * t, from.Breath + (to.Breath - from.Breath) * t,
            from.Paws + (to.Paws - from.Paws) * t, from.Stretch + (to.Stretch - from.Stretch) * t,
            from.Weight + (to.Weight - from.Weight) * t);
    }
}

/// <summary>Small, continuous deformations of the existing atlas; no extra timer or generated artwork.</summary>
public sealed class AshaSprite : FrameworkElement
{
    private BitmapSource? source;
    private int frame;
    private double standingHeight, ground, center;
    private readonly DrawingGroup mesh = new();
    private readonly ImageBrush brush = new() { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill };
    private readonly List<(Point A, Point B, Point C, Matrix Inverse, MatrixTransform Transform)> triangles = [];
    public AshaExpression Expression { get; private set; }
    public AshaSprite()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        void Add(Point a, Point b, Point c)
        {
            var shape = new StreamGeometry();
            using (var context = shape.Open()) { context.BeginFigure(a, true, true); context.LineTo(b, false, false); context.LineTo(c, false, false); }
            shape.Freeze();
            var transform = new MatrixTransform();
            var drawing = new DrawingGroup { Transform = transform }; drawing.Children.Add(new GeometryDrawing(brush, null, shape)); mesh.Children.Add(drawing);
            var inverse = new Matrix(b.X - a.X, b.Y - a.Y, c.X - a.X, c.Y - a.Y, a.X, a.Y); inverse.Invert();
            triangles.Add((a, b, c, inverse, transform));
        }
        for (int row = 0; row < 8; row++) for (int column = 0; column < 6; column++)
        {
            Point a = new(column / 6d, row / 8d), b = new((column + 1) / 6d, row / 8d);
            Point c = new(column / 6d, (row + 1) / 8d), d = new((column + 1) / 6d, (row + 1) / 8d);
            Add(a, b, d); Add(a, d, c);
        }
    }
    public void SetFrame(BitmapSource bitmap, int index, double referenceHeight, double groundPixel, double centerPixel = double.NaN)
    {
        source = bitmap; brush.ImageSource = bitmap; frame = index;
        standingHeight = referenceHeight; ground = groundPixel;
        center = double.IsNaN(centerPixel) ? bitmap.PixelWidth / 2d : centerPixel;
        UpdateMesh(); InvalidateVisual();
    }
    public void SetExpression(AshaExpression expression)
    {
        if (Expression == expression) return;
        bool staticChanged = (Expression == default) != (expression == default);
        Expression = expression; UpdateMesh();
        if (staticChanged) InvalidateVisual();
    }
    public static Point Deform(Point p, int frame, AshaExpression expression)
    {
        static double Bell(double value, double center, double radius) => Math.Max(0, 1 - Math.Pow((value - center) / radius, 2));
        double face = Bell(p.X, .51, .37) * Bell(p.Y, .48, .22);
        double ears = Math.Pow(Math.Max(0, 1 - p.Y / .29), 2) * Math.Max(Bell(p.X, .28, .21), Bell(p.X, .79, .22));
        double tailSide = frame < 4 ? Math.Clamp((.33 - p.X) / .25, 0, 1) : Math.Clamp((p.X - .7) / .23, 0, 1);
        double tail = tailSide * Bell(p.Y, .76, .17);
        double chest = Bell(p.X, .5, .28) * Bell(p.Y, .77, .14);
        double sleeves = Math.Max(Bell(p.X, .24, .2), Bell(p.X, .76, .2)) * Bell(p.Y, .75, .13);
        double shoes = Bell(p.Y, .9, .055) * Bell(p.X, .5, .25);
        return new(p.X + face * expression.GazeX * .009 + ears * expression.Ears * .014 + tail * expression.Tail * .009 + chest * (p.X - .5) * expression.Breath * .025
                + shoes * (.5 - p.X) * expression.Paws * .18 + sleeves * (p.X - .5) * expression.Stretch * .04
                + Math.Pow(Math.Max(0, 1 - p.Y / .88), 2) * expression.Weight * .018,
            p.Y + face * expression.GazeY * .014 - ears * Math.Abs(expression.Ears) * .01 + tail * expression.Tail * .025 - chest * expression.Breath * .006
                - sleeves * expression.Stretch * .032);
    }
    private Rect ImageBounds()
    {
        if (source is null || ActualWidth <= 0 || ActualHeight <= 0) return Rect.Empty;
        // DeskMascot has a 4 DIP inset on each edge. Match visible standing height
        // between species and place shoes at the physics anchor (89% of the control).
        // Do not normalize each pose independently: breathing/curled sleep stays smaller.
        const double inset = 4;
        double size = Math.Min(ActualWidth, ActualHeight) + inset * 2;
        double scale = size * .84 / standingHeight;
        double width = source.PixelWidth * scale, height = source.PixelHeight * scale;
        return new(ActualWidth / 2 - center * scale, (ActualHeight + inset * 2) * .89 - inset - ground * scale, width, height);
    }
    private void UpdateMesh()
    {
        Rect rect = ImageBounds(); if (rect.IsEmpty || Expression == default) return;
        Point Target(Point uv) { Point p = Deform(uv, frame, Expression); return new(rect.X + p.X * rect.Width, rect.Y + p.Y * rect.Height); }
        // Reuse geometry, brushes, and transforms throughout idle; only vertex mappings change.
        foreach (var triangle in triangles)
        {
            Point x = Target(triangle.A), y = Target(triangle.B), z = Target(triangle.C);
            var mapping = triangle.Inverse; mapping.Append(new Matrix(y.X - x.X, y.Y - x.Y, z.X - x.X, z.Y - x.Y, x.X, x.Y));
            triangle.Transform.Matrix = mapping;
        }
    }
    protected override void OnRender(DrawingContext dc)
    {
        Rect rect = ImageBounds(); if (rect.IsEmpty) return;
        if (Expression == default) dc.DrawImage(source, rect);
        else { UpdateMesh(); dc.DrawDrawing(mesh); }
    }
}
