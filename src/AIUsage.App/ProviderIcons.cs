using System.Windows;
using System.Windows.Media;
using AIUsage.Core;

namespace AIUsage.App;

/// <summary>Small service glyphs drawn for this widget, rather than official logo artwork.</summary>
internal static class ProviderIcons
{
    private static readonly Geometry Claude = CreateClaude();
    private static readonly Geometry Codex = ParseFrozen(
        "F0 M10,1.5 L17.4,5.75 17.4,14.25 10,18.5 2.6,14.25 2.6,5.75 Z " +
        "M10,3.55 L15.6,6.8 15.6,13.2 10,16.45 4.4,13.2 4.4,6.8 Z " +
        "M8.55,6.85 L5.4,10 8.55,13.15 9.8,11.9 7.9,10 9.8,8.1 Z " +
        "M11.45,6.85 L14.6,10 11.45,13.15 10.2,11.9 12.1,10 10.2,8.1 Z");
    private static readonly Geometry Antigravity = ParseFrozen(
        "F1 M8.1,3.3 L15.9,17.5 12.5,17.5 8.1,9.2 3.7,17.5 0.3,17.5 Z " +
        "M15.8,1.1 L16.75,3.95 19.6,4.9 16.75,5.85 15.8,8.7 14.85,5.85 12,4.9 14.85,3.95 Z");

    /// <summary>Returns a shared, frozen geometry in a 20 by 20 coordinate space; render with Fill.</summary>
    public static Geometry Get(ProviderKind provider) => provider switch
    {
        ProviderKind.Claude => Claude,
        ProviderKind.Codex => Codex,
        ProviderKind.Antigravity => Antigravity,
        _ => Geometry.Empty
    };

    private static Geometry CreateClaude()
    {
        var rays = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (var angle = 0; angle < 180; angle += 30)
        {
            rays.Children.Add(new RectangleGeometry(new Rect(8.95, 1.2, 2.1, 17.6), 0.8, 0.8)
            {
                Transform = new RotateTransform(angle, 10, 10)
            });
        }
        rays.Freeze();
        return rays;
    }

    private static Geometry ParseFrozen(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }
}
