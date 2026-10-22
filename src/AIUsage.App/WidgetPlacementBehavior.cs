using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _placementRestoreTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(750)
    };
    private bool _placementClosed;
    private bool _restoringPlacement;
    private bool _displayRestorePending;
    private bool _userMoveActive;
    private bool _userHeightResize;
    private double _moveStartScale = 1;
    private bool _dpiRestoreQueued;
    private bool _fitPlacementHeight;
    private bool _temporaryPlacement;
    private string _moveTopology = "";
    private string _placementTopology = "";
    private DesktopRect? _moveStartBounds;

    // Test injection is confined to the isolated demo; the normal constructor always
    // uses native enumeration. It permits topology transitions without changing displays.
    internal Func<IReadOnlyList<MonitorLayout>> ReadMonitors { get; set; } = MonitorInventory.Capture;

    private void InitializePlacementBehavior()
    {
        _placementRestoreTimer.Tick += RestoreAfterDisplayChange;
        Closed += (_, _) =>
        {
            _placementClosed = true;
            _placementRestoreTimer.Stop();
            _placementRestoreTimer.Tick -= RestoreAfterDisplayChange;
        };
    }

    // Preserve the public entry point used by older callers and smoke checks.
    public void ApplyMode(bool fitHeight = false)
    {
        if (!_ready || _placementClosed || _userMoveActive) return;
        _fitPlacementHeight |= fitHeight;
        var monitors = ReadMonitors();
        if (monitors.Count == 0) { QueueDisplayRestore(); return; }

        var previousState = _controller.Settings.WidgetPlacement;
        var state = previousState ?? ReadLegacyPlacement(monitors);
        if (state is not null) state = WidgetPlacementPolicy.AdoptLegacy(state, monitors);
        var resolution = WidgetPlacementPolicy.Resolve(state, monitors, DefaultWidth, ContentHeight(DefaultWidth), MinWidth, MinHeight);
        if (resolution is null) { QueueDisplayRestore(); return; }
        if (_fitPlacementHeight)
        {
            var contentHeight = ContentHeight(resolution.Bounds.Width / resolution.Monitor.Scale);
            resolution = WidgetPlacementPolicy.Resolve(state, monitors, DefaultWidth, contentHeight, MinWidth, MinHeight, contentHeight)!;
        }
        ApplyPlacement(resolution);
        _temporaryPlacement = resolution.IsFallback;
        _placementTopology = TopologyKey(monitors);
        _displayRestorePending = false;
        _placementRestoreTimer.Stop();

        // A brand new profile gets an initial location. Migration retains the original
        // coordinates; neither fallback nor automatic height fitting is a user move.
        state ??= WidgetPlacementPolicy.Capture(null, resolution.Monitor, resolution.Bounds);
        if (!ReferenceEquals(previousState, state))
        {
            _controller.Settings.WidgetPlacement = state;
            _ = SavePositionAsync();
        }
    }

    private WidgetPlacementState? ReadLegacyPlacement(IReadOnlyList<MonitorLayout> monitors)
    {
        var placements = _controller.Settings.Placements;
        var keys = new[] { $"{PlacementKey}:last", _controller.Settings.MiniMode ? "mini:last" : "widget:last", "widget:last", "mini:last" };
        foreach (var key in keys.Distinct())
        {
            if (!placements.TryGetValue(key, out var legacy) || legacy is null) continue;
            var prefix = key[..key.LastIndexOf(':')] + ":";
            var deviceEntry = placements.FirstOrDefault(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)
                && pair.Key != key && pair.Value == legacy);
            var deviceName = deviceEntry.Key is { } name ? name[prefix.Length..] : null;
            return WidgetPlacementPolicy.MigrateLegacy(legacy, deviceName, monitors);
        }
        return null;
    }

    public void FitContent()
    {
        if (!_ready || _placementClosed) return;
        _fitPlacementHeight = true;
        if (_displayRestorePending) return;
        var monitors = ReadMonitors();
        if (monitors.Count == 0) { QueueDisplayRestore(); return; }
        if (_placementTopology.Length != 0 && _placementTopology != TopologyKey(monitors))
        {
            QueueDisplayRestore();
            return;
        }
        if (!TryReadBounds(out var bounds)) return;
        var monitor = MonitorAt(bounds, monitors);
        if (monitor is null) return;
        ApplyPlacement(SizeAtCurrentPosition(bounds, monitor, DefaultWidth, ContentHeight(DefaultWidth)));
        // Explicit size-fit on a temporary display must still remember the absent monitor.
        if (!_temporaryPlacement) RememberCurrentPlacement(monitors);
    }

    private void FitHeight()
    {
        if (!_ready || _placementClosed) return;
        _fitPlacementHeight = true;
        if (_displayRestorePending || _userMoveActive) return;
        var monitors = ReadMonitors();
        if (monitors.Count == 0) { QueueDisplayRestore(); return; }
        if (_placementTopology.Length != 0 && _placementTopology != TopologyKey(monitors))
        {
            QueueDisplayRestore();
            return;
        }
        if (!TryReadBounds(out var bounds)) return;
        var monitor = MonitorAt(bounds, monitors);
        if (monitor is null) return;
        var width = Math.Max(MinWidth, ActualWidth);
        ApplyPlacement(SizeAtCurrentPosition(bounds, monitor, width, ContentHeight(width)));
        // Content-driven layout must never overwrite the user's remembered position.
    }

    private PlacementResolution SizeAtCurrentPosition(DesktopRect bounds, MonitorLayout monitor, double widthDip, double heightDip)
    {
        var area = monitor.WorkArea;
        var width = Math.Clamp(widthDip * monitor.Scale, Math.Min(MinWidth * monitor.Scale, area.Width), area.Width);
        var height = Math.Clamp(heightDip * monitor.Scale, Math.Min(MinHeight * monitor.Scale, area.Height), area.Height);
        return new(monitor, new(Math.Clamp(bounds.Left, area.Left, area.Left + area.Width - width),
            Math.Clamp(bounds.Top, area.Top, area.Top + area.Height - height), width, height), _temporaryPlacement);
    }

    private void ApplyPlacement(PlacementResolution resolution)
    {
        var rect = resolution.Bounds;
        _restoringPlacement = true;
        try
        {
            // Native geometry is always pixels; WPF layout sizes remain DIPs.
            if (!SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
                (int)Math.Round(rect.Left), (int)Math.Round(rect.Top),
                Math.Max(1, (int)Math.Ceiling(rect.Width)), Math.Max(1, (int)Math.Ceiling(rect.Height)), 0x0014))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "위젯 위치를 복원하지 못했습니다.");
        }
        finally { _restoringPlacement = false; }
    }

    // Close/update callers may flush an in-progress user move, but never save an
    // OS-assigned temporary position after docking, sleep, or display reconfiguration.
    public void CapturePlacement()
    {
        if (!_ready || _placementClosed || !_userMoveActive || _restoringPlacement || _displayRestorePending || WindowState != WindowState.Normal) return;
        if (_moveStartBounds is null || !TryReadBounds(out var currentBounds) || currentBounds == _moveStartBounds) return;
        var monitors = ReadMonitors();
        if (monitors.Count == 0 || _moveTopology != TopologyKey(monitors)) { QueueDisplayRestore(); return; }
        RememberCurrentPlacement(monitors);
        // A drag (including one across different DPIs) is not a request for manual
        // height. Only a real vertical sizing gesture that changed logical height is.
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        if (_userHeightResize && Math.Abs(currentBounds.Height / scale - _moveStartBounds.Height / _moveStartScale) > 0.5 / scale)
            _fitPlacementHeight = false;
        _moveStartBounds = currentBounds;
        _moveStartScale = scale;
    }

    private void RememberCurrentPlacement(IReadOnlyList<MonitorLayout> monitors)
    {
        if (!TryReadBounds(out var bounds)) return;
        var monitor = MonitorAt(bounds, monitors);
        if (monitor is null) return;
        _controller.Settings.WidgetPlacement = WidgetPlacementPolicy.Capture(_controller.Settings.WidgetPlacement, monitor, bounds);
        // Retain useful coordinates for returning to older releases. Only deliberate
        // user actions update this legacy copy as well.
        var legacy = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width / monitor.Scale, bounds.Height / monitor.Scale, monitor.Scale);
        _controller.Settings.Placements[$"{PlacementKey}:{monitor.DeviceName}"] = legacy;
        _controller.Settings.Placements[$"{PlacementKey}:last"] = legacy;
        _temporaryPlacement = false;
        _placementTopology = TopologyKey(monitors);
        _ = SavePositionAsync();
    }

    private void HandlePlacementMessage(int message, IntPtr wParam)
    {
        if (_placementClosed) return;
        if (message == 0x02E0 && !_userMoveActive) // WM_DPICHANGED
        {
            // SetWindowPos can deliver DPI changes synchronously during ApplyMode.
            // Re-measure after WPF handles the new DPI and the outer restore finishes;
            // otherwise its timer cleanup would discard this necessary second pass.
            if (!_dpiRestoreQueued)
            {
                _dpiRestoreQueued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    _dpiRestoreQueued = false;
                    if (!_placementClosed) QueueDisplayRestore();
                }));
            }
            return;
        }
        if (_restoringPlacement) return;
        switch (message)
        {
            case 0x0231: // WM_ENTERSIZEMOVE: actual system move/resize loop
                _userMoveActive = true;
                _userHeightResize = false;
                _moveStartScale = VisualTreeHelper.GetDpi(this).DpiScaleY;
                _moveTopology = TopologyKey(ReadMonitors());
                _moveStartBounds = TryReadBounds(out var initialBounds) ? initialBounds : null;
                break;
            case 0x0214 when _userMoveActive: // WM_SIZING, WMSZ_TOP through WMSZ_BOTTOMRIGHT
                _userHeightResize |= wParam.ToInt64() is >= 3 and <= 8;
                break;
            case 0x0232: // WM_EXITSIZEMOVE
                if (_userMoveActive) CapturePlacement();
                _userMoveActive = false;
                _userHeightResize = false;
                _moveStartBounds = null;
                if (_displayRestorePending) QueueDisplayRestore();
                else if (_fitPlacementHeight) FitHeight();
                break;
            case 0x007E: // WM_DISPLAYCHANGE
            case 0x0219 when wParam.ToInt64() == 7: // DBT_DEVNODES_CHANGED (dock/display devices)
            case 0x001A when wParam.ToInt64() == 0x002F: // SPI_SETWORKAREA (taskbar)
            case 0x0218 when wParam.ToInt64() is 0x0007 or 0x0012: // resume
                QueueDisplayRestore();
                break;
        }
    }

    internal void QueueDisplayRestore()
    {
        if (_placementClosed) return;
        _displayRestorePending = true;
        _placementRestoreTimer.Stop();
        _placementRestoreTimer.Start();
    }

    private void RestoreAfterDisplayChange(object? sender, EventArgs e)
    {
        _placementRestoreTimer.Stop();
        if (_placementClosed) return;
        if (!_ready || _userMoveActive) { _placementRestoreTimer.Start(); return; }
        _displayRestorePending = false;
        try { ApplyMode(); }
        catch (Exception error) { _controller.ShowError(error); }
    }

    internal void EnsurePlacementVisible()
    {
        if (!_ready || _placementClosed || _userMoveActive) return;
        var monitors = ReadMonitors();
        if (_displayRestorePending || monitors.Count == 0 || TopologyKey(monitors) != _placementTopology)
            QueueDisplayRestore();
    }

    private bool TryReadBounds(out DesktopRect bounds)
    {
        bounds = null!;
        if (!GetWindowRect(new WindowInteropHelper(this).Handle, out var rect)) return false;
        bounds = new(rect.Left, rect.Top, (double)rect.Right - rect.Left, (double)rect.Bottom - rect.Top);
        return WidgetPlacementPolicy.IsValid(bounds);
    }

    private static MonitorLayout? MonitorAt(DesktopRect bounds, IReadOnlyList<MonitorLayout> monitors)
        => monitors.OrderByDescending(monitor =>
            Math.Max(0, Math.Min(bounds.Left + bounds.Width, monitor.Bounds.Left + monitor.Bounds.Width) - Math.Max(bounds.Left, monitor.Bounds.Left)) *
            Math.Max(0, Math.Min(bounds.Top + bounds.Height, monitor.Bounds.Top + monitor.Bounds.Height) - Math.Max(bounds.Top, monitor.Bounds.Top)))
            .ThenBy(monitor => Math.Abs(bounds.Left + bounds.Width / 2 - monitor.Bounds.Left - monitor.Bounds.Width / 2)
                + Math.Abs(bounds.Top + bounds.Height / 2 - monitor.Bounds.Top - monitor.Bounds.Height / 2)).FirstOrDefault();

    private static string TopologyKey(IReadOnlyList<MonitorLayout> monitors)
        => string.Join("|", monitors.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Select(m =>
            string.Create(CultureInfo.InvariantCulture, $"{m.Id}:{m.Bounds.Left},{m.Bounds.Top},{m.Bounds.Width},{m.Bounds.Height}:{m.WorkArea.Left},{m.WorkArea.Top},{m.WorkArea.Width},{m.WorkArea.Height}:{m.Scale}:{m.IsPrimary}")));
}
