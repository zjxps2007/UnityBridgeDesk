namespace UnityBridgeDesk.Desktop.Controls;

/// <summary>A quiet idle layer. Sampling does not change navigation, position, or its next departure.</summary>
public sealed class AshaIdleMotion
{
    private readonly Random random;
    private readonly CompanionKind kind;
    private readonly double breathPhase;
    private double nextGesture, gestureStarted, duration;
    private int gesture = -1, previousGesture = -1;
    private int remainingGestures;
    private double direction;
    public AshaIdleMotion(int seed, CompanionKind kind = CompanionKind.Cat)
    { random = new(seed); this.kind = kind; breathPhase = random.NextDouble() * Math.PI * 2; Reset(0); }
    public void Reset(double now)
    {
        gesture = -1; nextGesture = now + 2.8 + random.NextDouble() * 2;
    }
    public AshaExpression Sample(double now, bool narrow = false)
    {
        double breath = Math.Sin(now * Math.PI * 2 / (kind == CompanionKind.Cat ? 5.6 : 4.7) + breathPhase);
        if (gesture >= 0 && now >= gestureStarted + duration)
        { gesture = -1; nextGesture = now + 4 + random.NextDouble() * 4; }
        if (gesture < 0 && now >= nextGesture)
        {
            int count = narrow ? 3 : 5;
            int available = remainingGestures & ((1 << count) - 1);
            if (available == 0) available = ((1 << count) - 1) & ~(1 << Math.Max(0, previousGesture));
            if (previousGesture < 0) available = (1 << count) - 1;
            var choices = Enumerable.Range(0, count).Where(value => (available & (1 << value)) != 0).ToArray();
            gesture = choices[random.Next(choices.Length)];
            remainingGestures = available & ~(1 << gesture);
            previousGesture = gesture; gestureStarted = now;
            duration = gesture switch { 0 => 1.25, 1 => 2.8, 3 => 3.1, 4 => 2.4, _ => 2.6 };
            direction = random.Next(2) == 0 ? -1 : 1;
        }
        if (gesture < 0) return new(0, 0, 0, 0, breath, narrow ? 1 : 0);
        double t = Math.Clamp((now - gestureStarted) / duration, 0, 1);
        double envelope = Math.Pow(Math.Sin(t * Math.PI), 2);
        if (kind == CompanionKind.Fox)
            return gesture switch
            {
                0 => new(.25 * envelope, .55 * envelope, Math.Sin(t * Math.PI * 6) * envelope * .55, .2 * envelope, breath, narrow ? 1 : 0),
                1 => new(direction * .3 * envelope, 0, .2 * envelope, Math.Sin(t * Math.PI * 2) * envelope * .8, breath, narrow ? 1 : 0),
                3 when !narrow => new(.3 * envelope, -.2 * envelope, .45 * envelope, -.4 * envelope, breath, 0, .45 * envelope),
                4 when !narrow => new(direction * .65 * envelope, -.15 * envelope, .65 * envelope, -.25 * envelope, breath, 0, 0, direction * .7 * envelope),
                _ => new(direction * envelope * .8, -.2 * envelope, .4 * envelope, .35 * envelope, breath, narrow ? 1 : 0)
            };
        return gesture switch
        {
            0 => new(0, 0, Math.Sin(t * Math.PI * 4) * envelope * .7, 0, breath, narrow ? 1 : 0),
            1 => new(0, 0, 0, Math.Sin(t * Math.PI * 2) * envelope * .45, breath, narrow ? 1 : 0),
            3 when !narrow => new(0, -.1 * envelope, .2 * envelope, 0, breath, 0, envelope),
            4 when !narrow => new(0, 0, 0, 0, breath, 0, 0, direction * envelope),
            _ => new(direction * envelope * .7, -.12 * envelope, 0, 0, breath, narrow ? 1 : 0)
        };
    }
}
