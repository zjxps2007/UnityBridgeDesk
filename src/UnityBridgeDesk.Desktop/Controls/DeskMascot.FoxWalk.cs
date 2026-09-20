using System.Windows;
using System.Windows.Media.Imaging;

namespace UnityBridgeDesk.Desktop.Controls;

public partial class DeskMascot
{
    private static readonly Lazy<SpriteFrame[]> FoxWalkFrames = new(LoadFoxWalkFrames);
    private static SpriteFrame[] LoadFoxWalkFrames()
    {
        var atlas = new BitmapImage(); atlas.BeginInit(); atlas.CacheOption = BitmapCacheOption.OnLoad;
        string assembly = typeof(DeskMascot).Assembly.GetName().Name!;
        atlas.UriSource = new Uri($"pack://application:,,,/{assembly};component/Assets/asha-fox-walk-v2.png");
        atlas.EndInit(); atlas.Freeze();
        int width = atlas.PixelWidth / 4, height = atlas.PixelHeight / 2;
        // Source calibration: stable head/body axis and contacting sole, not each
        // frame's bounding-box center (the lifted boot and tail change that box).
        int[] ground = [423, 423, 422, 420, 417, 417, 416, 417];
        int[] center = [260, 264, 264, 266, 263, 263, 264, 264];
        var frames = new SpriteFrame[8];
        for (int i = 0; i < frames.Length; i++)
        {
            var crop = new CroppedBitmap(atlas, new Int32Rect(i % 4 * width, i / 4 * height, width, height));
            crop.Freeze(); frames[i] = new(crop, i < 4 ? 362 : 357, ground[i], center[i]);
        }
        return frames;
    }
    private void SetFoxWalkFrame(int value)
    {
        if (frame == value && walkingFrame) return;
        frame = value; walkingFrame = true;
        var pose = FoxWalkFrames.Value[value];
        // Every walking frame has its tail on the same side, including frames 4–7.
        Sprite.SetFrame(pose.Image, 0, pose.StandingHeight, pose.Ground, pose.Center);
    }
}
