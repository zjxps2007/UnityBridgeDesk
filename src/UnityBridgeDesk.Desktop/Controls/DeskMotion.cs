using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace UnityBridgeDesk.Desktop.Controls;

/// <summary>Only page entry and the selected tab marker animate. Content and hit targets stay still.</summary>
public static class DeskMotion
{
    private static readonly ConditionalWeakTable<FrameworkElement, State> states = new();
    public static readonly DependencyProperty AllowedProperty = DependencyProperty.RegisterAttached("Allowed", typeof(bool), typeof(DeskMotion),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, (d, e) =>
        { if (!(bool)e.NewValue && d is FrameworkElement element) Reset(element); }));
    public static bool GetAllowed(DependencyObject element) => (bool)element.GetValue(AllowedProperty);
    public static void SetAllowed(DependencyObject element, bool value) => element.SetValue(AllowedProperty, value);
    public static readonly DependencyProperty AngleProperty = DependencyProperty.RegisterAttached("Angle", typeof(double), typeof(DeskMotion),
        new PropertyMetadata(double.NaN, (d, e) =>
        {
            if (d is FrameworkElement element && e.NewValue is double value && double.IsFinite(value))
                states.GetValue(element, x => new(x)).Turn.Angle = value;
        }));
    public static double GetAngle(DependencyObject element) => (double)element.GetValue(AngleProperty);
    public static void SetAngle(DependencyObject element, double value) => element.SetValue(AngleProperty, value);
    private sealed class State
    {
        public TranslateTransform Offset { get; } = new();
        public RotateTransform Turn { get; } = new();
        public State(FrameworkElement element)
        {
            var group = new TransformGroup(); group.Children.Add(element.RenderTransform); group.Children.Add(Turn); group.Children.Add(Offset);
            element.RenderTransform = group;
            element.Unloaded += (_, _) => Reset(element);
        }
    }
    private static bool CanAnimate(FrameworkElement element) => GetAllowed(element) && element.IsLoaded && element.IsVisible && element.IsEnabled && SystemParameters.ClientAreaAnimation;
    public static void Reveal(FrameworkElement? element)
    {
        if (element is null) return;
        if (!CanAnimate(element)) { Reset(element); return; }
        states.GetValue(element, e => new(e));
        double baseOpacity = (double)element.GetAnimationBaseValue(UIElement.OpacityProperty);
        // Fade only the work surface; never move text or restart a running fade from the beginning.
        double from = Math.Abs(element.Opacity - baseOpacity) > .0001 ? element.Opacity : baseOpacity * .96;
        element.BeginAnimation(UIElement.OpacityProperty, Animate(from, baseOpacity, 120));
    }
    public static void MoveTo(FrameworkElement element, double x, bool animate)
    {
        var state = states.GetValue(element, e => new(e));
        double from = state.Offset.X;
        state.Offset.X = x;
        if (animate && CanAnimate(element)) state.Offset.BeginAnimation(TranslateTransform.XProperty, Animate(from, x, 160));
        else state.Offset.BeginAnimation(TranslateTransform.XProperty, null);
    }
    private static DoubleAnimation Animate(double from, double to, int ms)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Timeline.SetDesiredFrameRate(animation, 60); return animation;
    }
    public static void Reset(FrameworkElement element)
    {
        if (!states.TryGetValue(element, out var state)) return;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        state.Offset.BeginAnimation(TranslateTransform.XProperty, null);
    }
}
