using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnityBridgeDesk.Desktop.Shell;

namespace UnityBridgeDesk.Desktop.Themes;

public static class Palette
{
    private static readonly Dictionary<DeskPalette, ImageBrush> wallpapers = [];
    public static string Name(DeskPalette palette) => palette switch
    { DeskPalette.Rose => "로즈", DeskPalette.Mint => "민트", _ => "연보라" };

    private static ImageBrush Wallpaper(DeskPalette palette)
    {
        if (wallpapers.TryGetValue(palette, out var cached)) return cached;
        string file = palette switch
        { DeskPalette.Rose => "cloud-station-rose.png", DeskPalette.Mint => "cloud-station-mint.png", _ => "cloud-station.png" };
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri($"pack://application:,,,/UnityBridgeDesk;component/Assets/{file}", UriKind.Absolute);
        image.EndInit(); image.Freeze();
        var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        brush.Freeze(); wallpapers[palette] = brush;
        return brush;
    }
    public static void Apply(DeskPalette palette)
    {
        if (!Enum.IsDefined(palette)) palette = DeskPalette.Lilac;
        var wallpaper = Wallpaper(palette);
        var colors = palette switch
        {
            DeskPalette.Rose => new[] { "#E2C5D1", "#F0C9D8", "#79455F", "#FBEBF0" },
            DeskPalette.Mint => new[] { "#BFD8CF", "#C5E7DA", "#365F55", "#E8F5EF" },
            _ => new[] { "#D7CBE4", "#DCCCF1", "#584278", "#F0E9F8" }
        };
        string[] keys = ["Line", "Accent", "AccentInk", "Tint"];
        for (int i = 0; i < keys.Length; i++)
            Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        Application.Current.Resources["DeskWallpaper"] = wallpaper;
    }
}
