using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UnityBridgeDesk.Desktop.Controls;

/// <summary>App-local platform companion. No global hooks, model calls or benchmark dependency.</summary>
public sealed partial class AshaCompanion : IDisposable
{
    private readonly FrameworkElement surface, home;
    private readonly Canvas layer;
    private readonly Border host;
    private readonly DeskMascot mascot;
    private readonly TranslateTransform placement = new();
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch clock = new();
    private readonly Random random;
    private readonly Queue<AshaStep> route = new();
    private AshaExplorer explorer;
    private AshaMap? map;
    private Rect[] oldObstacles = [];
    private AshaLedge[] oldLedges = [];
    private DispatcherOperation? pendingPlacement;
    private bool allowed, roaming = true, disposed, dirty = true, placed, dragging, stopAtLanding, landingLeft, continuingWalk;
    private int activity = 1;
    private double previousTick, nextDecision, lastInterest, interestSince, legElapsed, inspectUntil, landUntil, nextReach, groomingUntil, nextGroom;
    private Point? interestPoint, pressed, lastSupport, initialSupport;
    private Point grabOffset;
    public CompanionKind Character => mascot.Character;
    public AshaCompanion? OtherCompanion { get; set; }
    private bool Available(Point p) => OtherCompanion is not { } other || !other.host.IsVisible || other.host.Opacity <= 0 ||
        Math.Abs(p.X - other.Position.X) >= host.Width + 8 || Math.Abs(p.Y - other.Position.Y) >= host.Height;
    public Point Position { get; private set; }
    public bool IsRunning => timer.IsEnabled;
    public bool IsDragging => dragging;
    public bool IsFalling => falling;
    public bool IsPositionSafe => map?.IsClear(Position) == true;
    public bool IsStanding => map?.IsSafe(Position) == true;
    public IReadOnlyList<Point> StandingPositions => map?.Positions ?? [];
    public Point? Destination => route.Count > 0 ? route.Last().To : null;
    public AshaAction Action => mascot.Action;
    public AshaIntent Intent { get; private set; }

    public AshaCompanion(FrameworkElement surface, Canvas layer, Border host, FrameworkElement home, DeskMascot mascot, int? seed = null)
    {
        this.surface = surface; this.layer = layer; this.host = host; this.home = home; this.mascot = mascot;
        random = seed is { } value ? new Random(value) : new Random(); explorer = new(random.Next(), Character); host.RenderTransform = placement;
        chartPlay = new(seed is { } chartSeed ? chartSeed ^ 0xA51 : Random.Shared.Next());
        pages[page] = new(explorer);
        layer.Loaded += Loaded; layer.Unloaded += Unloaded; layer.IsVisibleChanged += VisibleChanged;
        host.IsVisibleChanged += VisibleChanged;
        surface.LayoutUpdated += LayoutUpdated; surface.SizeChanged += SizeChanged;
        surface.PreviewMouseMove += PointerMoved; surface.PreviewKeyDown += KeyPressed;
        surface.PreviewMouseWheel += UserWorking; surface.PreviewMouseDown += UserWorking;
        surface.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
        mascot.PreviewMouseLeftButtonDown += Pressed; mascot.PreviewMouseLeftButtonUp += Released;
        mascot.LostMouseCapture += LostCapture; mascot.Reacted += Reacted;
        timer.Tick += Tick;
    }
    public void Configure(bool motionAllowed, bool canRoam, int activityLevel)
    {
        bool changed = roaming != canRoam || activity != Math.Clamp(activityLevel, 0, 2);
        allowed = motionAllowed; roaming = canRoam; activity = Math.Clamp(activityLevel, 0, 2);
        if (changed) RestAfterInput();
        RefreshRunning();
    }
    private bool CanRun => !disposed && allowed && layer.IsLoaded && layer.IsVisible && host.IsVisible && SystemParameters.ClientAreaAnimation;
    private void RefreshRunning()
    {
        if (!CanRun) { Pause(); return; }
        if (timer.IsEnabled) return;
        clock.Restart(); previousTick = 0; nextDecision = .8 + random.NextDouble() * .6;
        lastInterest = -15; dirty = true; interestPoint = null;
        nextReach = nextGroom = 0;
        chartPlay.Reset(0);
        mascot.MotionEnabled = host.Opacity > 0; timer.Start();
    }
    public void Pause()
    {
        bool wasDragging = dragging;
        timer.Stop(); clock.Reset(); StopRoute(); pressed = null; dragging = false;
        chartPlay.Cancel(0);
        if (!host.IsVisible || disposed) ResetCharts();
        host.BeginAnimation(UIElement.OpacityProperty, null);
        if (mascot.IsMouseCaptured) mascot.ReleaseMouseCapture();
        Settle(); mascot.MotionEnabled = false;
        if (wasDragging && !disposed) RefreshGeometry();
    }
    private void StopRoute() { route.Clear(); legElapsed = 0; inspectUntil = landUntil = groomingUntil = 0; stopAtLanding = continuingWalk = false; falling = false; fallSpeed = fallDistance = 0; }
    private void Settle()
    {
        if (map is null || IsStanding) return;
        Point? safe = map.LandingBelow(Position) ?? (lastSupport is { } p && map.IsSafe(p) ? p : map.Nearest(Position));
        if (safe is { } destination) { Place(destination); lastSupport = destination; }
        else { host.Opacity = 0; host.IsHitTestVisible = false; }
    }
    private void Loaded(object sender, RoutedEventArgs e) { RefreshGeometry(); RefreshRunning(); }
    private void Unloaded(object sender, RoutedEventArgs e) => Pause();
    private void VisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => RefreshRunning();
    private void LayoutUpdated(object? sender, EventArgs e)
    {
        dirty = true;
        RefreshAfterLayout();
    }
    private void Scrolled(object sender, ScrollChangedEventArgs e) { dirty = true; RefreshAfterLayout(); }
    public void RefreshAfterLayout()
    {
        if (disposed || !host.IsVisible || pendingPlacement is not null) return;
        // Coalesce layout/scroll events and read geometry after WPF has redrawn its glyphs.
        pendingPlacement = surface.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        { pendingPlacement = null; if (!disposed && host.IsVisible && dirty) RefreshGeometry(); }));
    }
    private void SizeChanged(object sender, SizeChangedEventArgs e) { dirty = true; RefreshAfterLayout(); }
    private void KeyPressed(object sender, KeyEventArgs e) => UserWorking(sender, e);
    private void UserWorking(object sender, RoutedEventArgs e)
    {
        if (!CanRun || e.OriginalSource is DependencyObject source && (ReferenceEquals(source, mascot) || mascot.IsAncestorOf(source))) return;
        RestAfterInput();
        RefreshAfterLayout();
    }
    private void RestAfterInput()
    {
        chartPlay.Cancel(clock.Elapsed.TotalSeconds);
        interestPoint = null; nextDecision = clock.Elapsed.TotalSeconds + 3;
        if (falling) return; // Input pauses exploration, not gravity in the middle of a fall.
        // Finish an already airborne hop on its verified ledge rather than freezing in mid-air.
        if (route.TryPeek(out var step) && step.IsAirborne && legElapsed > Anticipation(step)) stopAtLanding = true;
        else { StopRoute(); mascot.SetAction(AshaAction.Idle); }
    }
    public void RefreshGeometry()
    {
        if (disposed || layer.ActualWidth <= 0 || layer.ActualHeight <= 0) return;
        var obstacles = new List<Rect>(); var ledges = new List<AshaLedge>(); Collect(surface, obstacles, ledges);
        PruneInputFrames();
        var bounds = new Rect(4, 4, Math.Max(0, layer.ActualWidth - 8), Math.Max(0, layer.ActualHeight - 8));
        bool changed = map is null || map.Bounds != bounds || map.SpriteSize != new Size(host.Width, host.Height) ||
            !oldObstacles.SequenceEqual(obstacles) || !oldLedges.SequenceEqual(ledges);
        if (changed)
        {
            oldObstacles = obstacles.ToArray(); oldLedges = ledges.ToArray();
            map = new AshaMap(bounds, new Size(host.Width, host.Height), obstacles, ledges);
        }
        dirty = false;
        if (dragging || map is null) return;
        if (awaitingGround)
        {
            // A completed fall in an empty viewport must not restart on every layout event.
            if (map.Nearest(Position) is null) return;
            awaitingGround = false; placed = false;
        }
        if (restorePage)
        {
            restorePage = false;
            var saved = pages[page];
            if (saved.Perch is { } bookmark && map.Restore(bookmark) is { } restored)
            {
                Place(restored); lastSupport = restored; placed = true; mascot.RestoreFacing(bookmark.FaceLeft);
                initialSupport = saved.Home is { } first ? map.Restore(first) : restored;
                RevealPlacement();
            }
        }
        bool travelling = route.Count > 0 && map.IsClear(Position) && (!changed || route.All(map.CanFollow));
        if (!placed)
        {
            StopRoute();
            Point wanted = Position;
            if (!placed && home.IsVisible && home.ActualWidth >= host.Width)
                wanted = home.TranslatePoint(new Point((home.ActualWidth - host.Width) / 2, home.ActualHeight - map.Feet), layer);
            // Start on an accessible lower perch when possible, so the first autonomous
            // choice can lead upward instead of trapping the cat on an isolated footer.
            Point? safe = !placed ? map.Positions.OrderBy(p => (p - wanted).LengthSquared)
                .Cast<Point?>().FirstOrDefault(p => map.Positions.Any(q => q.Y < p!.Value.Y - 20 &&
                    q.Y >= p.Value.Y - 175 && Math.Abs(q.X - p.Value.X) <= 250 && map.Connection(p.Value, q) is not null)) : null;
            if (safe is { } initial && !Available(initial)) safe = null;
            safe ??= map.Positions.Where(Available).OrderBy(p => (p - wanted).LengthSquared).Cast<Point?>().FirstOrDefault() ?? map.Nearest(wanted);
            host.Opacity = safe is null ? 0 : 1; host.IsHitTestVisible = safe is not null;
            if (safe is { } p) { Place(p); lastSupport = p; initialSupport ??= p; explorer.Arrived(p, p, VisitId(p)); placed = true; RevealPlacement(); }
            mascot.SetAction(AshaAction.Idle);
        }
        else if (!travelling && !map.IsSafe(Position))
        {
            if (CanRun) BeginFall();
            else { StopRoute(); Settle(); }
        }
        else
        {
            host.Opacity = 1; host.IsHitTestVisible = true;
            if (falling && map.IsSafe(Position)) FinishFall();
            if (changed && !travelling && route.Count > 0) { StopRoute(); mascot.SetAction(AshaAction.Idle); }
        }
        mascot.MotionEnabled = CanRun && host.Opacity > 0;
        UpdateChartInput();
        if (IsStanding && map.Support(Position) is { } support) mascot.SetPerch(support.Right - support.Left < host.Width * 1.6);
    }
    private string? VisitId(Point p) => map?.Support(p) is { Kind: not AshaLedgeKind.Edge } ledge ? ledge.Id : null;
    private void Tick(object? sender, EventArgs e)
    {
        if (!CanRun) { Pause(); return; }
        double now = clock.Elapsed.TotalSeconds, delta = Math.Clamp(now - previousTick, 0, .08); previousTick = now;
        timer.Interval = TimeSpan.FromMilliseconds(falling || route.Count > 0 ? 16 : 40);
        if (dirty && !dragging) RefreshGeometry();
        if (map is null || host.Opacity == 0 || mascot.ContextMenu?.IsOpen == true || pressed is not null) return;
        if (falling) { TickFall(delta); return; }
        if (route.Count == 0 && !map.IsSafe(Position)) { BeginFall(); TickFall(delta); return; }
        if (now < landUntil) { mascot.SetAction(AshaAction.Land, landingLeft); return; }
        if (now < groomingUntil) { mascot.SetAction(Character == CompanionKind.Fox ? AshaAction.Sniff : AshaAction.Groom); return; }
        if (route.TryPeek(out var step))
        {
            if (now < inspectUntil)
            {
                var aim = route.Last().To;
                mascot.LookToward(Math.Sign(aim.X - Position.X), Intent == AshaIntent.Climb ? -.65 : Intent == AshaIntent.Descend ? .6 : 0);
                mascot.SetAction(Character == CompanionKind.Fox ? AshaAction.Sniff : AshaAction.Look); return;
            }
            bool left = step.To.X < step.From.X;
            if (mascot.IsTurning) return;
            if (Math.Abs(step.To.X - step.From.X) > 1 && mascot.FacesLeft != left)
            { mascot.TurnToward(left); return; }
            if (Math.Abs(step.To.X - step.From.X) <= 1) left = mascot.FacesLeft;
            legElapsed += delta;
            if (step.IsDrop && legElapsed < step.PeekDuration)
            { mascot.LookToward(Math.Sign(step.To.X - Position.X)); mascot.SetAction(AshaAction.Peek, left, step.Impact); return; }
            if (step.IsAirborne && legElapsed < Anticipation(step)) { mascot.SetAction(AshaAction.Crouch, left, step.Impact); return; }
            bool sameWalkNext = !step.IsAirborne && route.Skip(1).FirstOrDefault() is { } followingWalk &&
                !followingWalk.IsAirborne && (followingWalk.To - followingWalk.From).Length > 0 &&
                Math.Sign(followingWalk.To.X - followingWalk.From.X) == Math.Sign(step.To.X - step.From.X);
            var walk = new AshaWalk((step.To - step.From).Length, CompanionCharacter.Speed(Character, activity), !continuingWalk, !sameWalkNext || stopAtLanding);
            double duration = step.IsDrop ? Math.Clamp(.38 + Math.Sqrt((step.To.Y - step.From.Y) / 500) * .5 + Math.Abs(step.To.X - step.From.X) * .0006, .42, .95) :
                step.IsJump ? Math.Clamp(.5 + (step.To - step.From).Length * .002 + step.Arc * .0015, .55, chartPlay.Active ? 1.9 : 1.25) :
                walk.Duration;
            double progress = step.IsAirborne ? (legElapsed - Anticipation(step)) / Math.Max(.01, duration) : walk.Distance(legElapsed) / Math.Max(.01, walk.Length);
            Point next = step.At(progress);
            if (step.IsAirborne && map.SweepLanding(Position, next, includeStart: false) is { } contact && (contact - step.To).Length > .5)
            {
                double drop = Math.Max(0, contact.Y - step.From.Y);
                Place(contact); StopRoute(); fallDistance = drop; FinishFall(); return;
            }
            if (!map.IsClear(next) || !step.IsAirborne && !map.CanWalk(Position, next) || progress >= 1 && !map.IsSafe(next))
            {
                StopRoute();
                if (map.IsSafe(Position)) mascot.SetAction(AshaAction.Idle); else BeginFall();
                return;
            }
            double travelled = (next - Position).Length;
            Place(next);
            mascot.SetAction(step.IsDrop ? AshaAction.Drop : step.IsJump ? AshaAction.Jump : AshaAction.Walk, left);
            if (!step.IsAirborne) { mascot.AdvanceStride(travelled); lastSupport = Position; }
            if (progress >= 1)
            {
                Place(step.To); lastSupport = Position; explorer.Arrived(step.From, step.To, VisitId(step.To)); route.Dequeue();
                bool keepWalking = !stopAtLanding && sameWalkNext;
                continuingWalk = keepWalking;
                legElapsed = keepWalking ? Math.Max(0, legElapsed - duration) : 0;
                if (stopAtLanding) { route.Clear(); stopAtLanding = false; }
                if (step.IsAirborne)
                {
                    landUntil = now + step.LandingDuration; landingLeft = left; mascot.SetAction(AshaAction.Land, left, step.Impact);
                    if (route.Count == 0 && step.Impact > .5 && now >= nextGroom)
                    { groomingUntil = landUntil + .55; nextGroom = now + 18; }
                }
                else if (!keepWalking) mascot.SetAction(AshaAction.Idle);
                if (map.Support(Position) is { } support) mascot.SetPerch(support.Right - support.Left < host.Width * 1.6);
                if (route.Count == 0)
                {
                    chartPlay.Arrived(map.Support(Position), now);
                    // Do not shorten the user's input cooldown when an airborne step finishes.
                    nextDecision = Math.Max(nextDecision, Math.Max(now, Math.Max(landUntil, groomingUntil)) + TravelPause());
                }
            }
            return;
        }
        if (mascot.Action is AshaAction.Land or AshaAction.Crouch or AshaAction.Jump or AshaAction.Drop or AshaAction.Groom or AshaAction.Sniff) mascot.SetAction(AshaAction.Idle);
        if (interestPoint is { } pointer && new Rect(Position, new Size(host.Width, host.Height)).Contains(pointer) &&
            now - interestSince >= .65 && now - lastInterest >= 15 && mascot.Action == AshaAction.Idle)
        {
            lastInterest = now; interestPoint = null;
            mascot.LookToward((pointer.X - Position.X - host.Width / 2) / (host.Width / 2));
            mascot.SetAction(AshaAction.Look); nextDecision = Math.Max(nextDecision, now + 1.2);
        }
        if (now < nextDecision) return;
        if (roaming) ChooseDestination(); else mascot.SetAction(AshaAction.Idle);
        // Back off when no route exists; roaming otherwise continues after the next landing.
        nextDecision = now + (route.Count > 0 ? TravelPause() : 2.5);
    }
    private static double Anticipation(AshaStep step) => step.IsDrop ? step.PeekDuration + .12 : step.IsJump ? .24 : 0;
    private double TravelPause() => Character == CompanionKind.Fox
        ? (activity switch { 0 => 1.2, 2 => .35, _ => .65 }) + random.NextDouble() * .7
        : chartPlay.Active ? chartPlay.Pause(activity) : activity switch { 0 => .9 + random.NextDouble() * .7, 2 => .25 + random.NextDouble() * .2, _ => .4 + random.NextDouble() * .4 };
    private void ChooseDestination()
    {
        if (map is null || !IsStanding) return;
        double now = clock.Elapsed.TotalSeconds;
        var path = Character == CompanionKind.Cat ? chartPlay.Choose(map, Position, now) : [];
        if (path.Length > 0 && !Available(path[^1].To)) path = [];
        if (path.Length == 0) path = explorer.Choose(map, Position, Available);
        if (path.Length > 0) { StartRoute(path, inspect: true); return; }
        if (now >= nextReach && map.Positions.Where(p => p.Y < Position.Y - 20).OrderBy(p => (p - Position).Length).Cast<Point?>().FirstOrDefault() is { } higher)
        {
            mascot.LookToward(Math.Sign(higher.X - Position.X)); mascot.SetAction(AshaAction.Reach); nextReach = now + 45;
        }
        else mascot.SetAction(AshaAction.Idle);
    }
    private void StartRoute(IEnumerable<AshaStep> steps, bool inspect)
    {
        StopRoute(); foreach (var step in steps) route.Enqueue(step);
        double height = route.Count > 0 ? route.Last().To.Y - Position.Y : 0;
        Intent = height < -20 ? AshaIntent.Climb : height > 20 ? AshaIntent.Descend : AshaIntent.Explore;
        interestPoint = null; inspectUntil = clock.Elapsed.TotalSeconds + (inspect ? Character == CompanionKind.Fox ? .6 + random.NextDouble() * .35 : .25 + random.NextDouble() * .2 : 0);
    }
    /// <summary>Moves to a supported position, using the same verified walk/jump graph as autonomous exploration.</summary>
    public bool WalkTo(Point destination)
    {
        if (!CanRun) return false;
        RefreshGeometry(); if (map is null || !IsStanding) return false;
        var path = map.Route(Position, destination); if (path.Length == 0) return false;
        StartRoute(path, inspect: false); return true;
    }
    public void ReturnHome()
    {
        if (disposed || !placed || map is null || initialSupport is not { } initial) return;
        if (map.Nearest(initial) is not { } target) return;
        if (CanRun && WalkTo(target)) return;
        else
        {
            // Settings can request a return while the companion is paused behind its modal.
            StopRoute(); Place(target); lastSupport = target; mascot.SetAction(AshaAction.Idle);
        }
    }
    private void Place(Point p) { Position = p; placement.X = p.X; placement.Y = p.Y; UpdateChartInput(); }
    private void PointerMoved(object sender, MouseEventArgs e)
    {
        if (!CanRun) return;
        Point p = e.GetPosition(layer);
        if (pressed is { } first && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!dragging && (p - first).Length < 6) return;
            dragging = true; StopRoute(); mascot.SetAction(AshaAction.Carried);
            Place(new Point(Math.Clamp(p.X - grabOffset.X, 4, Math.Max(4, layer.ActualWidth - host.Width - 4)),
                Math.Clamp(p.Y - grabOffset.Y, 4, Math.Max(4, layer.ActualHeight - host.Height - 4)))); return;
        }
        double now = clock.Elapsed.TotalSeconds;
        if (new Rect(Position, new Size(host.Width, host.Height)).Contains(p))
        {
            if (interestPoint is not { } previous || (previous - p).Length > 6) { interestSince = now; interestPoint = p; }
        }
        else interestPoint = null;
    }
    private void Pressed(object sender, MouseButtonEventArgs e)
    {
        if (!CanRun) return;
        Point p = e.GetPosition(layer); pressed = p; grabOffset = new Point(p.X - Position.X, p.Y - Position.Y);
        StopRoute(); mascot.CaptureMouse();
    }
    private void Released(object sender, MouseButtonEventArgs e)
    {
        bool wasDragging = dragging; pressed = null; dragging = false;
        if (mascot.IsMouseCaptured) mascot.ReleaseMouseCapture();
        if (wasDragging)
        {
            SetDown();
            e.Handled = true;
        }
        else { RefreshGeometry(); }
    }
    private void LostCapture(object sender, MouseEventArgs e)
    {
        bool wasDragging = dragging; pressed = null; dragging = false;
        if (wasDragging) SetDown();
    }
    private void Reacted(object? sender, EventArgs e)
    {
        chartPlay.Cancel(clock.Elapsed.TotalSeconds); lastInterest = clock.Elapsed.TotalSeconds; nextDecision = lastInterest + 1.2;
        if (falling) return;
        StopRoute(); Settle();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; Pause(); pendingPlacement?.Abort(); pendingPlacement = null;
        PruneInputFrames(all: true);
        timer.Tick -= Tick; layer.Loaded -= Loaded; layer.Unloaded -= Unloaded; layer.IsVisibleChanged -= VisibleChanged;
        host.IsVisibleChanged -= VisibleChanged;
        surface.LayoutUpdated -= LayoutUpdated; surface.SizeChanged -= SizeChanged;
        surface.PreviewMouseMove -= PointerMoved; surface.PreviewKeyDown -= KeyPressed;
        surface.PreviewMouseWheel -= UserWorking; surface.PreviewMouseDown -= UserWorking;
        surface.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
        mascot.PreviewMouseLeftButtonDown -= Pressed; mascot.PreviewMouseLeftButtonUp -= Released;
        mascot.LostMouseCapture -= LostCapture; mascot.Reacted -= Reacted;
    }
}
