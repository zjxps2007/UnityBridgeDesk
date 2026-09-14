using System.Windows;
using System.Windows.Media;
using UnityBridgeDesk.Desktop.Shell;

namespace UnityBridgeDesk.Desktop.Themes;

public static class Palette
{
    public static void Apply(DeskPalette palette)
    {
        var colors = palette switch
        {
            DeskPalette.Rose => new[] { "#E2C5D1", "#F0C9D8", "#79455F", "#FBEBF0" },
            DeskPalette.Mint => new[] { "#BFD8CF", "#C5E7DA", "#365F55", "#E8F5EF" },
            _ => new[] { "#D7CBE4", "#DCCCF1", "#584278", "#F0E9F8" }
        };
        string[] keys = ["Line", "Accent", "AccentInk", "Tint"];
        for (int i = 0; i < keys.Length; i++)
            Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
}
