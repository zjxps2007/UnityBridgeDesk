using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UnityBridgeDesk.Desktop.Controls;

public enum AshaAction { Idle, Look, Walk, Sleep, Play, Carried, Crouch, Jump, Land, Reach, Drop, Peek, Turn, Glance, Dismiss, Groom, Sniff }

public partial class DeskMascot : UserControl
{
    public static readonly DependencyProperty MotionEnabledProperty = DependencyProperty.Register(nameof(MotionEnabled), typeof(bool), typeof(DeskMascot),
        new PropertyMetadata(false, (d, _) => ((DeskMascot)d).RefreshMotion()));
    public bool MotionEnabled { get => (bool)GetValue(MotionEnabledProperty); set => SetValue(MotionEnabledProperty, value); }
    public static readonly DependencyProperty CharacterProperty = DependencyProperty.Register(nameof(Character), typeof(CompanionKind), typeof(DeskMascot),
        new PropertyMetadata(CompanionKind.Cat, (d, _) => ((DeskMascot)d).CharacterChanged()));
    public CompanionKind Character { get => (CompanionKind)GetValue(CharacterProperty); set => SetValue(CharacterProperty, value); }
    private sealed record SpriteFrame(BitmapSource Image, double StandingHeight, double Ground, double Center = double.NaN);
    private static readonly Lazy<SpriteFrame[]> CatFrames = new(() => LoadFrames(CompanionKind.Cat));
    private static readonly Lazy<SpriteFrame[]> FoxFrames = new(() => LoadFrames(CompanionKind.Fox));
    private void CharacterChanged()
    {
        if (Sprite is null) return;
        idle = new(Random.Shared.Next(), Character);
        frame = -1; SetFrame(4);
        string name = CompanionCharacter.Name(Character);
        System.Windows.Automation.AutomationProperties.SetName(this, name + " · 클릭 또는 Enter로 반응 보기, 끌어서 자리 옮기기");
        ToolTip = name + " · 클릭하면 반응해요. 끌어서 자리를 옮길 수 있어요.";
    }
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch elapsed = new();
    private AshaIdleMotion idle = new(Random.Shared.Next());
    private readonly AshaInteraction interaction = new();
    private bool disposed, walkingFrame;
    private double actionStarted, nextBlink = 3.7, blinkEnds, look;
    private double effort = .5;
    private double strideDistance, lookY, lastDraw, followedLook, followedLookY, followedEars, followedTail;
    private bool narrowPerch, turnLeft;
    private double transitionDuration, fromScaleX = 1, fromScaleY = 1, fromTurn, fromLift;
    private AshaExpression fromExpression;
    private int frame = -1;
    public bool IsMoving => timer.IsEnabled;
    public bool IsBlinkTimerRunning => timer.IsEnabled;
    public AshaAction Action { get; private set; }
    public int CurrentFrame => frame;
    public bool FacesLeft => Facing.ScaleX < 0;
    public bool IsTurning => Action == AshaAction.Turn;
    public event EventHandler? Reacted;

    public DeskMascot()
    {
        InitializeComponent(); SetFrame(4);
        timer.Tick += Tick;
        Loaded += (_, _) => RefreshMotion();
        Unloaded += (_, _) => StopMotion();
        IsVisibleChanged += (_, _) => RefreshMotion();
        MouseLeftButtonUp += (_, e) => { if (React()) { Focus(); e.Handled = true; } };
        KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space && !e.IsRepeat) { React(); e.Handled = true; } };
        IsKeyboardFocusWithinChanged += (_, _) => FocusRing.BorderBrush = IsKeyboardFocusWithin
            ? new SolidColorBrush(Color.FromRgb(133, 105, 167)) : Brushes.Transparent;
    }
    private static SpriteFrame[] LoadFrames(CompanionKind kind)
    {
        var atlas = new BitmapImage(); atlas.BeginInit(); atlas.CacheOption = BitmapCacheOption.OnLoad;
        string assembly = typeof(DeskMascot).Assembly.GetName().Name!;
        atlas.UriSource = new Uri($"pack://application:,,,/{assembly};component/Assets/{CompanionCharacter.Atlas(kind)}"); atlas.EndInit(); atlas.Freeze();
        var frames = new SpriteFrame[8];
        // Calibrated from opaque artwork, not transparent cell dimensions. Sleep keeps
        // the standing scale so a curled pose does not grow to fill the upright height.
        int[] catGround = [436, 436, 435, 436, 433, 432, 419, 431];
        int[] foxGround = [401, 401, 401, 401, 363, 363, 361, 364];
        int width = atlas.PixelWidth / 4, height = atlas.PixelHeight / 2;
        for (int i = 0; i < frames.Length; i++)
        {
            var area = new Int32Rect(i % 4 * width, i / 4 * height, width, height);
            if (kind == CompanionKind.Fox)
            {
                // Keep the existing UV layout for facial/tail animation. Display scale
                // and foot anchoring are handled separately by AshaSprite.
                area = i < 4 ? new Int32Rect(i % 4 * width + 32, 77, 350, 350)
                    : new Int32Rect(i % 4 * width + 70, height + 58, 330, 330);
            }
            var crop = new CroppedBitmap(atlas, area); crop.Freeze();
            frames[i] = kind == CompanionKind.Cat ? new(crop, 426, catGround[i])
                : new(crop, i < 4 ? 304 : 285, foxGround[i] - (i < 4 ? 77 : 58));
        }
        return frames;
    }
    private bool CanMove => !disposed && MotionEnabled && IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation;
    private void RefreshMotion()
    {
        if (!CanMove) { StopMotion(); return; }
        if (timer.IsEnabled) return;
        elapsed.Restart(); actionStarted = lastDraw = 0; nextBlink = 2.8 + Random.Shared.NextDouble() * 2; blinkEnds = 0; idle.Reset(0); interaction.Reset();
        timer.Start();
    }
    public void SetAction(AshaAction action, bool faceLeft = false, double? intensity = null)
    {
        if (!CanMove) return;
        ChangeAction(action, elapsed.Elapsed.TotalSeconds);
        if (intensity is { } value) effort = Math.Clamp(value, .1, 1);
        // Keep the last orientation while resting instead of flipping at every landing.
        if (action is AshaAction.Walk or AshaAction.Peek or AshaAction.Crouch or AshaAction.Jump or AshaAction.Drop or AshaAction.Land)
            Facing.ScaleX = faceLeft ? -1 : 1;
        Draw();
    }
    public void AdvanceStride(double distance)
    {
        if (!double.IsFinite(distance) || distance <= 0) return;
        if (Character == CompanionKind.Fox)
            strideDistance = FoxGait.Advance(strideDistance, distance, ActualHeight > 0 ? ActualHeight : 96);
        else strideDistance = (strideDistance + distance * 96 / Math.Max(1, ActualHeight > 0 ? ActualHeight : 96)) % 3900;
        // Keep drawing in the same dispatcher turn as movement; don't lag by another timer.
        if (CanMove && Action == AshaAction.Walk) Draw();
    }
    public void SetPerch(bool narrow) => narrowPerch = narrow;
    public void RestoreFacing(bool left) => Facing.ScaleX = left ? -1 : 1;
    public void TurnToward(bool left)
    {
        if (!CanMove || FacesLeft == left) return;
        turnLeft = left; ChangeAction(AshaAction.Turn, elapsed.Elapsed.TotalSeconds); Draw();
    }
    private void ChangeAction(AshaAction action, double now)
    {
        if (Action == action) return;
        if (action == AshaAction.Walk) strideDistance = 0;
        fromScaleX = BodyScale.ScaleX; fromScaleY = BodyScale.ScaleY; fromTurn = BodyTurn.Angle; fromLift = BodyLift.Y;
        fromExpression = Sprite.Expression;
        transitionDuration = action switch
        {
            AshaAction.Idle => .24, AshaAction.Sleep => .35,
            AshaAction.Land => .04, AshaAction.Jump or AshaAction.Drop => .10,
            AshaAction.Walk or AshaAction.Crouch => .12, _ => .18
        };
        Action = action; actionStarted = now;
        timer.Interval = TimeSpan.FromMilliseconds(action is AshaAction.Idle or AshaAction.Sleep ? 33 : 16);
        if (action == AshaAction.Idle) idle.Reset(now);
    }
    public void LookToward(double direction, double vertical = 0)
    { look = Math.Clamp(direction, -1, 1); lookY = Math.Clamp(vertical, -1, 1); }
    private void Tick(object? sender, EventArgs e)
    {
        if (!CanMove) { StopMotion(); return; }
        Draw();
    }
    private void Draw()
        => DrawAt(elapsed.Elapsed.TotalSeconds);
    private void DrawAt(double now)
    {
        double phase = now - actionStarted;
        double delta = Math.Clamp(now - lastDraw, 0, .08); lastDraw = now;
        followedLook = CharacterMotion.Follow(followedLook, look, delta, .10);
        followedLookY = CharacterMotion.Follow(followedLookY, lookY, delta, .12);
        if (now >= nextBlink) { blinkEnds = now + .14; nextBlink = now + 4.2 + (now % 1.7); }
        if (Action == AshaAction.Turn && phase >= .20) Facing.ScaleX = turnLeft ? -1 : 1;
        if (Action == AshaAction.Play && phase > .9 || Action == AshaAction.Look && phase > 1.2 || Action == AshaAction.Reach && phase > 1.0 ||
            Action == AshaAction.Turn && phase > .34 || Action == AshaAction.Glance && phase > .65 ||
            Action == AshaAction.Dismiss && phase > .8 || Action == AshaAction.Groom && phase > .55 || Action == AshaAction.Sniff && phase > .95)
        { ChangeAction(AshaAction.Idle, now); phase = 0; }
        int next = Action switch
        {
            AshaAction.Walk => Character == CompanionKind.Fox ? FoxGait.Frame(strideDistance) : AshaWalk.Frame(strideDistance),
            AshaAction.Sleep => 6,
            AshaAction.Play or AshaAction.Carried => 7,
            AshaAction.Reach => 7,
            AshaAction.Groom => phase is > .12 and < .4 ? 7 : 4,
            AshaAction.Jump => phase < .35 ? 0 : 2,
            AshaAction.Drop => phase < .18 ? 1 : 2,
            _ => now < blinkEnds ? 5 : 4
        };
        if (Character == CompanionKind.Fox && Action == AshaAction.Walk) SetFoxWalkFrame(next);
        else SetFrame(next);
        // Stride frames supply the limb motion; transform offsets only soften the transitions.
        double scaleX = Action == AshaAction.Drop ? .98 : Action == AshaAction.Jump ? .97 : 1;
        double scaleY = Action switch
        {
            AshaAction.Crouch => 1 - (.04 + .1 * effort) * CharacterMotion.Smooth(phase / .16),
            AshaAction.Sniff => 1 - .025 * Math.Pow(Math.Sin(phase * Math.PI / .95), 2),
            AshaAction.Peek => 1 - .035 * CharacterMotion.Smooth(phase / .2),
            AshaAction.Land => 1 - (.035 + .12 * effort) * CharacterMotion.Landing(phase, effort),
            AshaAction.Drop => 1 - (.035 + .065 * effort) * (1 - Math.Min(phase / .35, 1)),
            AshaAction.Reach => 1 - .04 * Math.Sin(Math.Min(phase, 1) * Math.PI),
            AshaAction.Sleep => 1 + Math.Sin(now * 1.2) * .003,
            _ => 1
        };
        double turn = Action switch { AshaAction.Look or AshaAction.Glance => followedLook * 2, AshaAction.Carried => Math.Sin(phase * 4) * 2,
            AshaAction.Turn => (turnLeft ? -1 : 1) * Math.Sin(Math.Clamp((phase - .09) / .25, 0, 1) * Math.PI) * 2.5,
            AshaAction.Dismiss => Facing.ScaleX * -2 * Math.Sin(Math.Min(phase / .8, 1) * Math.PI),
            AshaAction.Sniff => Facing.ScaleX * 3 * Math.Sin(Math.Min(phase / .95, 1) * Math.PI),
            AshaAction.Groom => Facing.ScaleX * 2 * Math.Sin(Math.Min(phase / .55, 1) * Math.PI),
            AshaAction.Peek => Facing.ScaleX * 5 * CharacterMotion.Smooth(phase / .22),
            AshaAction.Play => -Math.Sin(Math.Min(phase / .9, 1) * Math.PI) * 5,
            AshaAction.Drop => Facing.ScaleX * 5 * (1 - Math.Min(phase / .45, 1)), _ => 0 };
        double lift = Action == AshaAction.Walk && Character == CompanionKind.Cat ? -Math.Pow(Math.Sin(strideDistance * Math.PI / 19.5), 2) * .65 : 0;
        double attention = CharacterMotion.Smooth(phase / .18);
        AshaExpression expression = Action switch
        {
            AshaAction.Idle => idle.Sample(now, narrowPerch),
            AshaAction.Sniff => new(.35, .55 * attention, Math.Sin(phase * 12) * .4, Math.Sin(phase * 6) * .7, 0, narrowPerch ? 1 : 0),
            AshaAction.Peek => new(.6 * attention, attention, Math.Sin(phase * 7) * .65, Math.Sin(phase * 5) * .6),
            AshaAction.Look or AshaAction.Glance => new(followedLook * Facing.ScaleX * attention, followedLookY * attention, Math.Sin(phase * 6) * .45, Math.Sin(phase * 3) * .25),
            AshaAction.Turn => new((turnLeft ? -1 : 1) * Facing.ScaleX * attention, 0, Math.Sin(phase * 8) * .35, -.25 * attention),
            AshaAction.Dismiss => new(-.65 * attention, -.1 * attention, -.3 * attention, Math.Sin(phase * 6) * .2),
            AshaAction.Groom => new(0, .5 * attention, .15, .12, 0, narrowPerch ? 1 : 0, Math.Sin(Math.Min(phase / .55, 1) * Math.PI) * .3),
            AshaAction.Walk => Character == CompanionKind.Fox ? default : new(0, 0, Math.Sin(strideDistance / 7.8) * .12, Math.Sin(strideDistance / 7.8) * .5),
            AshaAction.Jump or AshaAction.Drop => new(.25, Action == AshaAction.Drop ? .6 : -.35, .4, Math.Sin(phase * 5) * .65),
            AshaAction.Land => new(0, 0, Math.Sin(phase * 14) * Math.Exp(-phase * 7) * effort * .4, Math.Sin(phase * 10) * Math.Exp(-phase * 6) * effort * .5),
            _ => default
        };
        double t = transitionDuration == 0 ? 1 : Math.Clamp(phase / transitionDuration, 0, 1);
        t = CharacterMotion.Smooth(t);
        BodyScale.ScaleX = fromScaleX + (scaleX - fromScaleX) * t;
        BodyScale.ScaleY = fromScaleY + (scaleY - fromScaleY) * t;
        BodyTurn.Angle = fromTurn + (turn - fromTurn) * t;
        BodyLift.Y = fromLift + (lift - fromLift) * t;
        var blended = AshaExpression.Blend(fromExpression, expression, t);
        followedEars = CharacterMotion.Follow(followedEars, blended.Ears, delta, .055);
        followedTail = CharacterMotion.Follow(followedTail, blended.Tail, delta, Character == CompanionKind.Fox ? .14 : .10);
        Sprite.SetExpression(blended with { Ears = followedEars, Tail = followedTail });
    }
    private void SetFrame(int value)
    {
        if (frame == value && !walkingFrame) return;
        frame = value; walkingFrame = false;
        var pose = (Character == CompanionKind.Fox ? FoxFrames : CatFrames).Value[value];
        Sprite.SetFrame(pose.Image, value, pose.StandingHeight, pose.Ground, pose.Center);
    }
    public bool React()
    {
        if (!CanMove) return false;
        double now = elapsed.Elapsed.TotalSeconds;
        if (interaction.Tap(now) is not { } action) return false;
        LookToward(FacesLeft ? -1 : 1);
        if (Character == CompanionKind.Fox) action = action switch { AshaAction.Glance => AshaAction.Sniff, AshaAction.Dismiss => AshaAction.Look, _ => action };
        ChangeAction(action, now); actionStarted = now; Draw(); Reacted?.Invoke(this, EventArgs.Empty); return true;
    }
    public void StopMotion()
    {
        timer.Stop(); elapsed.Reset(); Action = AshaAction.Idle; look = lookY = lastDraw = followedLook = followedLookY = followedEars = followedTail = 0; transitionDuration = 0;
        timer.Interval = TimeSpan.FromMilliseconds(33);
        if (BodyScale is null) return;
        BodyScale.ScaleX = BodyScale.ScaleY = 1; BodyTurn.Angle = BodyLift.Y = 0; SetFrame(4);
        Sprite.SetExpression(default);
    }
    public void DisposeMotion() { disposed = true; StopMotion(); timer.Tick -= Tick; }
}
