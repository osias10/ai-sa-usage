using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace AIUsage.App;

internal static class ThemeManager
{
    public static bool IsLight { get; private set; }
    public static void Apply(string theme)
    {
        bool light = theme == "Light";
        if (theme == "System")
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                light = key?.GetValue("AppsUseLightTheme") is int value && value == 1;
            }
            catch (System.Security.SecurityException) { }
        }
        IsLight = light;
        var palette = light ? new Dictionary<string, string>
        {
            ["CanvasBrush"] = "#FAFAFB", ["WidgetBrush"] = "#FAFAFB", ["SurfaceBrush"] = "#FFFFFF", ["RaisedBrush"] = "#F0F0F2", ["BorderBrush"] = "#DEDFE3",
            ["TextBrush"] = "#242529", ["MutedBrush"] = "#656771", ["SubtleBrush"] = "#767983", ["TrackBrush"] = "#E7E8EB",
            ["HoverBrush"] = "#E8E9EC", ["AccentBrush"] = "#315FA8", ["AccentTextBrush"] = "#FFFFFF", ["WarningBrush"] = "#8B652E",
            ["ActionBrush"] = "#2C5EBB", ["ActionHoverBrush"] = "#244E9B", ["SelectionBrush"] = "#DCE7F6"
        } : new Dictionary<string, string>
        {
            ["CanvasBrush"] = "#191A1D", ["WidgetBrush"] = "#191A1D", ["SurfaceBrush"] = "#1E1F23", ["RaisedBrush"] = "#26272B", ["BorderBrush"] = "#36373C",
            ["TextBrush"] = "#F0F0F1", ["MutedBrush"] = "#ABACB4", ["SubtleBrush"] = "#858791", ["TrackBrush"] = "#34353A",
            ["HoverBrush"] = "#303137", ["AccentBrush"] = "#8FADE3", ["AccentTextBrush"] = "#FFFFFF", ["WarningBrush"] = "#D6AC70",
            ["ActionBrush"] = "#3B66B6", ["ActionHoverBrush"] = "#4672C0", ["SelectionBrush"] = "#36527C"
        };
        foreach (var (name, hex) in palette) Application.Current.Resources[name] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }
}
