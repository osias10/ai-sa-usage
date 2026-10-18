using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyPlacementMemoryAsync(MainWindow widget)
    {
        if (_controller is not { IsDemo: true }) throw new InvalidOperationException("Placement verification requires the isolated demo.");
        var native = MonitorInventory.Capture().FirstOrDefault(m => m.IsPrimary)
            ?? throw new InvalidOperationException("A native monitor is required for UI placement verification.");
        var area = native.WorkArea;
        if (area.Width / native.Scale < 500 || area.Height / native.Scale < 400)
            throw new InvalidOperationException("The desktop is too small for the placement smoke fixture.");
        // Both simulated displays stay inside the real monitor. No system display
        // settings, pointer movement, or physical keyboard input are changed.
        var half = Math.Floor(area.Width / 2);
        var leftArea = new DesktopRect(area.Left, area.Top, half, area.Height);
        var rightArea = new DesktopRect(area.Left + half, area.Top, area.Width - half, area.Height);
        var first = new MonitorLayout("smoke:left", "SMOKE1", leftArea, leftArea, native.Scale, true);
        var second = new MonitorLayout("smoke:right", "SMOKE2", rightArea, rightArea, native.Scale, false);
        IReadOnlyList<MonitorLayout> displays = new[] { first, second };
        var savedState = _controller.Settings.WidgetPlacement;
        var savedLegacy = new Dictionary<string, WindowPlacement>(_controller.Settings.Placements);
        var savedReader = widget.ReadMonitors;
        var preferredBounds = new DesktopRect(rightArea.Left + 20 * native.Scale, rightArea.Top + 45 * native.Scale,
            230 * native.Scale, 150 * native.Scale);
        MainWindow? restarted = null;
        var preference = "";
        try
        {
            widget.ReadMonitors = () => displays;
            _controller.Settings.WidgetPlacement = WidgetPlacementPolicy.Capture(null, second, preferredBounds);
            preference = JsonSerializer.Serialize(_controller.Settings.WidgetPlacement);
            widget.ApplyMode(fitHeight: true);
            await FlushAsync();
            AssertPosition(widget, preferredBounds.Left, preferredBounds.Top);

            displays = new[] { first };
            widget.QueueDisplayRestore();
            widget.CapturePlacement(); // shutdown/update during transient topology
            await Task.Delay(850);
            await FlushAsync();
            AssertInside(widget, first.WorkArea);
            widget.FitContent(); // resizing fallback must not adopt it as the preferred monitor
            widget.CapturePlacement();
            AssertRemembered();
            // A plain click or cancelled drag enters the move loop without changing
            // geometry; it must not choose the temporary screen as a new preference.
            var fallbackHandle = new WindowInteropHelper(widget).Handle;
            SendPlacementMessage(fallbackHandle, 0x0231, IntPtr.Zero, IntPtr.Zero);
            SendPlacementMessage(fallbackHandle, 0x0232, IntPtr.Zero, IntPtr.Zero);
            AssertRemembered();

            restarted = new MainWindow(_controller) { ReadMonitors = () => displays, AllowClose = true };
            restarted.Show();
            await FlushAsync();
            AssertInside(restarted, first.WorkArea);
            restarted.Close();
            restarted = null;
            AssertRemembered();

            displays = new[] { second with { DeviceName = "SMOKE1" }, first with { DeviceName = "SMOKE2" } };
            widget.QueueDisplayRestore();
            await Task.Delay(850);
            await FlushAsync();
            AssertPosition(widget, preferredBounds.Left, preferredBounds.Top);
            AssertRemembered();

            // A real WM_ENTERSIZEMOVE/WM_EXITSIZEMOVE route is deliberate movement.
            // Adopting this monitor must then survive subsequent display restoration.
            var handle = new WindowInteropHelper(widget).Handle;
            var movedLeft = first.WorkArea.Left + 18 * native.Scale;
            var movedTop = first.WorkArea.Top + 55 * native.Scale;
            SendPlacementMessage(handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
            if (!MovePlacementWindow(handle, IntPtr.Zero, (int)Math.Round(movedLeft), (int)Math.Round(movedTop), 0, 0, 0x0015))
                throw new InvalidOperationException("Unable to move the isolated placement fixture.");
            SendPlacementMessage(handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
            await FlushAsync();
            if (_controller.Settings.WidgetPlacement?.PreferredMonitorId != first.Id)
                throw new InvalidOperationException("Deliberate user movement must select the new monitor.");
            widget.ApplyMode();
            await FlushAsync();
            AssertPosition(widget, movedLeft, movedTop);

            var afterMove = JsonSerializer.Serialize(_controller.Settings.WidgetPlacement);
            displays = Array.Empty<MonitorLayout>();
            widget.ApplyMode();
            widget.CapturePlacement();
            if (JsonSerializer.Serialize(_controller.Settings.WidgetPlacement) != afterMove)
                throw new InvalidOperationException("A temporary empty inventory must not overwrite placement memory.");
        }
        finally
        {
            restarted?.Close();
            widget.ReadMonitors = savedReader;
            _controller.Settings.WidgetPlacement = savedState;
            _controller.Settings.Placements = savedLegacy;
            widget.ApplyMode(fitHeight: true);
            await FlushAsync();
        }

        async Task FlushAsync() => await Dispatcher.InvokeAsync(widget.UpdateLayout, DispatcherPriority.ApplicationIdle);

        void AssertRemembered()
        {
            if (JsonSerializer.Serialize(_controller.Settings.WidgetPlacement) != preference)
                throw new InvalidOperationException("Automatic fallback, resize, restart, and reconnect must not overwrite the original monitor position.");
        }
    }

    private static void AssertPosition(MainWindow window, double left, double top)
    {
        var bounds = PlacementBounds(window);
        if (Math.Abs(bounds.Left - left) > 2 || Math.Abs(bounds.Top - top) > 2)
            throw new InvalidOperationException($"Remembered position was not restored: {bounds.Left},{bounds.Top}, expected {left},{top}.");
    }

    private static void AssertInside(MainWindow window, DesktopRect area)
    {
        var bounds = PlacementBounds(window);
        if (bounds.Left < area.Left - 1 || bounds.Top < area.Top - 1 || bounds.Right > area.Left + area.Width + 1 || bounds.Bottom > area.Top + area.Height + 1)
            throw new InvalidOperationException("A temporary widget must remain inside the available monitor.");
    }

    private static PlacementNativeRect PlacementBounds(MainWindow window)
    {
        if (!GetPlacementBounds(new WindowInteropHelper(window).Handle, out var bounds))
            throw new InvalidOperationException("Unable to read placement fixture bounds.");
        return bounds;
    }

    [StructLayout(LayoutKind.Sequential)] private struct PlacementNativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool GetPlacementBounds(IntPtr hwnd, out PlacementNativeRect bounds);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] private static extern bool MovePlacementWindow(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendPlacementMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
