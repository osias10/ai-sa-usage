namespace AIUsage.Core;

// Every rectangle in this contract is in physical desktop pixels. Only the
// policy's size arguments are DIPs; no WPF or WinForms coordinate conversion is implicit.
public sealed record DesktopRect(double Left, double Top, double Width, double Height);
public sealed record MonitorLayout(string Id, string DeviceName, DesktopRect Bounds,
    DesktopRect WorkArea, double Scale, bool IsPrimary);
public sealed record MonitorPlacement(string MonitorId, string DeviceName, DesktopRect Bounds,
    DesktopRect WorkArea, double Scale);
public sealed record PlacementResolution(MonitorLayout Monitor, DesktopRect Bounds, bool IsFallback);
public sealed record LegacyPlacement(WindowPlacement Placement, string? DeviceName);

public sealed class WidgetPlacementState
{
    public string? PreferredMonitorId { get; set; }
    public Dictionary<string, MonitorPlacement> Monitors { get; set; } = [];
    // A disconnected legacy monitor has no stable identity yet. Keep its original
    // placement until it can be migrated, rather than adopting the startup fallback.
    public LegacyPlacement? PendingLegacy { get; set; }
}

public static class WidgetPlacementPolicy
{
    public static WidgetPlacementState Capture(WidgetPlacementState? state, MonitorLayout monitor, DesktopRect bounds)
    {
        if (!IsUsable(monitor) || !IsValid(bounds)) return state ?? new();
        var result = Copy(state);
        result.PreferredMonitorId = monitor.Id;
        result.PendingLegacy = null;
        result.Monitors[monitor.Id] = new(monitor.Id, monitor.DeviceName, bounds, monitor.WorkArea, monitor.Scale);
        return result;
    }

    // Resolving is deliberately read-only. Windows can temporarily move a window
    // onto the primary display during startup, sleep, docking, or a DPI change.
    // Those positions must never replace the user's last explicit placement.
    public static PlacementResolution? Resolve(WidgetPlacementState? state, IReadOnlyList<MonitorLayout> monitors,
        double defaultWidthDip, double defaultHeightDip, double minWidthDip, double minHeightDip,
        double? fittedHeightDip = null)
    {
        var available = UsableMonitors(monitors);
        if (available.Length == 0) return null;
        if (state?.PendingLegacy is not null) state = AdoptLegacy(state, available);
        var preferred = available.FirstOrDefault(m => SameId(m.Id, state?.PreferredMonitorId));
        var monitor = preferred ?? available.FirstOrDefault(m => m.IsPrimary) ?? available[0];
        var fallback = preferred is null && (!string.IsNullOrWhiteSpace(state?.PreferredMonitorId) || state?.PendingLegacy is not null);
        var saved = FindSaved(state, monitor.Id);
        var area = monitor.WorkArea;
        var widthDip = saved is null ? Positive(defaultWidthDip, 240) : saved.Bounds.Width / saved.Scale;
        var heightDip = saved is null ? Positive(defaultHeightDip, 100) : saved.Bounds.Height / saved.Scale;
        var referenceWidth = SizeInPixels(widthDip, monitor.Scale, minWidthDip, area.Width);
        var referenceHeight = SizeInPixels(heightDip, monitor.Scale, minHeightDip, area.Height);
        var width = referenceWidth;
        var height = fittedHeightDip is { } fitted && IsPositive(fitted)
            ? SizeInPixels(fitted, monitor.Scale, minHeightDip, area.Height) : referenceHeight;
        double left, top;
        if (saved is not null)
        {
            left = MapCoordinate(saved.Bounds.Left, saved.Bounds.Width, saved.WorkArea.Left, saved.WorkArea.Width,
                saved.Scale, area.Left, area.Width, referenceWidth, monitor.Scale);
            top = MapCoordinate(saved.Bounds.Top, saved.Bounds.Height, saved.WorkArea.Top, saved.WorkArea.Height,
                saved.Scale, area.Top, area.Height, referenceHeight, monitor.Scale);
        }
        else
        {
            left = area.Left + area.Width - width - 10 * monitor.Scale;
            top = area.Top + area.Height - height - 10 * monitor.Scale;
        }
        left = Math.Clamp(left, area.Left, area.Left + area.Width - width);
        top = Math.Clamp(top, area.Top, area.Top + area.Height - height);
        return new(monitor, new(left, top, width, height), fallback);
    }

    public static WidgetPlacementState MigrateLegacy(WindowPlacement legacy, string? legacyDeviceName,
        IReadOnlyList<MonitorLayout> monitors)
        => AdoptLegacy(new() { PendingLegacy = new(legacy, legacyDeviceName) }, monitors);

    public static WidgetPlacementState AdoptLegacy(WidgetPlacementState state, IReadOnlyList<MonitorLayout> monitors)
    {
        if (state.PendingLegacy is not { Placement: { } legacy } pending ||
            !IsPositive(legacy.Scale)) return state;
        var bounds = new DesktopRect(legacy.Left, legacy.Top, legacy.Width * legacy.Scale, legacy.Height * legacy.Scale);
        if (!IsValid(bounds)) return state;
        var available = UsableMonitors(monitors);
        MonitorLayout? monitor;
        if (!string.IsNullOrWhiteSpace(pending.DeviceName))
            monitor = available.FirstOrDefault(m => SameId(m.DeviceName, pending.DeviceName));
        else
            monitor = available.Select(m => (Monitor: m, Area: IntersectionArea(bounds, m.Bounds)))
                .Where(m => m.Area > 0).OrderByDescending(m => m.Area).Select(m => m.Monitor).FirstOrDefault();
        if (monitor is null) return state;
        var result = Copy(state);
        result.PreferredMonitorId = monitor.Id;
        result.PendingLegacy = null;
        result.Monitors[monitor.Id] = new(monitor.Id, monitor.DeviceName, bounds, monitor.WorkArea, legacy.Scale);
        return result;
    }

    public static bool IsValid(DesktopRect? rect) => rect is not null &&
        double.IsFinite(rect.Left) && double.IsFinite(rect.Top) && IsPositive(rect.Width) && IsPositive(rect.Height) &&
        double.IsFinite(rect.Left + rect.Width) && double.IsFinite(rect.Top + rect.Height);

    private static bool IsUsable(MonitorLayout? monitor) => monitor is not null &&
        !string.IsNullOrWhiteSpace(monitor.Id) && IsPositive(monitor.Scale) &&
        IsValid(monitor.Bounds) && IsValid(monitor.WorkArea) &&
        monitor.WorkArea.Left >= monitor.Bounds.Left && monitor.WorkArea.Top >= monitor.Bounds.Top &&
        monitor.WorkArea.Left + monitor.WorkArea.Width <= monitor.Bounds.Left + monitor.Bounds.Width &&
        monitor.WorkArea.Top + monitor.WorkArea.Height <= monitor.Bounds.Top + monitor.Bounds.Height;

    private static MonitorLayout[] UsableMonitors(IReadOnlyList<MonitorLayout>? monitors)
        => (monitors ?? []).Where(IsUsable).OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    private static MonitorPlacement? FindSaved(WidgetPlacementState? state, string id)
    {
        var saved = state?.Monitors?.FirstOrDefault(pair => SameId(pair.Key, id)).Value;
        return saved is not null && SameId(saved.MonitorId, id) && IsValid(saved.Bounds) &&
            IsValid(saved.WorkArea) && IsPositive(saved.Scale) ? saved : null;
    }

    private static WidgetPlacementState Copy(WidgetPlacementState? state) => new()
    {
        PreferredMonitorId = state?.PreferredMonitorId,
        PendingLegacy = state?.PendingLegacy,
        Monitors = (state?.Monitors ?? []).Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            .DistinctBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
    };

    private static double MapCoordinate(double position, double savedSize, double savedOrigin, double savedLength,
        double savedScale, double origin, double length, double referenceSize, double scale)
    {
        var startGap = Math.Max(0, position - savedOrigin);
        var endGap = Math.Max(0, savedOrigin + savedLength - position - savedSize);
        // Use the old window's mapped size for the anchor calculation. Growing or
        // shrinking content must not move its top edge unless it would leave the screen.
        var value = startGap <= endGap
            ? origin + startGap / savedScale * scale
            : origin + length - referenceSize - endGap / savedScale * scale;
        return double.IsFinite(value) ? value : origin;
    }

    private static double SizeInPixels(double size, double scale, double minimum, double available)
    {
        var pixels = size * scale;
        if (!double.IsFinite(pixels)) pixels = available;
        var minimumPixels = Positive(minimum, 1) * scale;
        if (!double.IsFinite(minimumPixels)) minimumPixels = available;
        return Math.Clamp(pixels, Math.Min(minimumPixels, available), available);
    }

    private static double IntersectionArea(DesktopRect a, DesktopRect b)
        => Math.Max(0, Math.Min(a.Left + a.Width, b.Left + b.Width) - Math.Max(a.Left, b.Left)) *
            Math.Max(0, Math.Min(a.Top + a.Height, b.Top + b.Height) - Math.Max(a.Top, b.Top));
    private static bool SameId(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && StringComparer.OrdinalIgnoreCase.Equals(left, right);
    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;
    private static double Positive(double value, double fallback) => IsPositive(value) ? value : fallback;
}
