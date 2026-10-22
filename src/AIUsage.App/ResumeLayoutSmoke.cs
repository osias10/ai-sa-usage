using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyResumeLayoutAsync(MainWindow widget)
    {
        if (_controller is not { IsDemo: true })
            throw new InvalidOperationException("Resume-layout verification requires the isolated demo.");
        var native = MonitorInventory.Capture().FirstOrDefault(monitor => monitor.IsPrimary)
            ?? throw new InvalidOperationException("A native monitor is required for resume-layout verification.");
        var area = native.WorkArea;
        if (area.Width / native.Scale < 500 || area.Height / native.Scale < 400)
            throw new InvalidOperationException("The desktop is too small for the resume-layout fixture.");

        // These logical monitors share one physical display and its actual DPI.
        // Messages target only the demo HWND; no system display or power state changes.
        var half = Math.Floor(area.Width / 2);
        var firstArea = new DesktopRect(area.Left, area.Top, half, area.Height);
        var secondArea = new DesktopRect(area.Left + half, area.Top, area.Width - half, area.Height);
        var first = new MonitorLayout("resume:first", "RESUME1", firstArea, firstArea, native.Scale, true);
        var second = new MonitorLayout("resume:second", "RESUME2", secondArea, secondArea, native.Scale, false);
        IReadOnlyList<MonitorLayout> displays = new[] { first, second };
        var savedState = _controller.Settings.WidgetPlacement;
        var savedLegacy = new Dictionary<string, WindowPlacement>(_controller.Settings.Placements);
        var savedReader = widget.ReadMonitors;
        var handle = new WindowInteropHelper(widget).Handle;
        var expectedFittedHeight = 0d;
        try
        {
            widget.ReadMonitors = () => displays;
            var fittedHeight = widget.ActualHeight;
            var tallHeight = Math.Min(area.Height / native.Scale - 30, fittedHeight + 100);
            if (tallHeight < fittedHeight + 30)
                throw new InvalidOperationException("The resume-layout fixture requires spare vertical space.");
            var firstBounds = new DesktopRect(firstArea.Left + 10 * native.Scale, firstArea.Top + 10 * native.Scale,
                230 * native.Scale, tallHeight * native.Scale);
            var secondBounds = new DesktopRect(secondArea.Left + 10 * native.Scale, secondArea.Top + 30 * native.Scale,
                230 * native.Scale, fittedHeight * native.Scale);
            var state = WidgetPlacementPolicy.Capture(null, first, firstBounds);
            _controller.Settings.WidgetPlacement = WidgetPlacementPolicy.Capture(state, second, secondBounds);
            widget.ApplyMode(fitHeight: true);
            await FlushAsync();
            var initialFitted = PlacementBounds(widget);
            expectedFittedHeight = (initialFitted.Bottom - initialFitted.Top) / native.Scale;
            AssertFitted("initial preferred monitor");

            // Moving a fitted window must not turn its automatic height into manual
            // sizing. The other display deliberately remembers an obsolete tall size.
            var current = PlacementBounds(widget);
            SendPlacementMessage(handle, 0x0231, IntPtr.Zero, IntPtr.Zero); // WM_ENTERSIZEMOVE
            SetBounds(current.Left + 8, current.Top + 8, current.Right - current.Left, current.Bottom - current.Top);
            SendPlacementMessage(handle, 0x0232, IntPtr.Zero, IntPtr.Zero); // WM_EXITSIZEMOVE
            await FlushAsync();
            var moved = PlacementBounds(widget);
            var remembered = JsonSerializer.Serialize(_controller.Settings.WidgetPlacement);

            displays = new[] { first };
            SendPlacementMessage(handle, 0x0218, new IntPtr(0x0012), IntPtr.Zero); // PBT_APMRESUMEAUTOMATIC
            await Task.Delay(850);
            await FlushAsync();
            AssertInside(widget, first.WorkArea);
            AssertFitted("resume fallback after a pure move");
            AssertRemembered(remembered);

            displays = new[] { first, second };
            SendPlacementMessage(handle, 0x007E, IntPtr.Zero, IntPtr.Zero); // WM_DISPLAYCHANGE
            await Task.Delay(850);
            await FlushAsync();
            AssertPosition(widget, moved.Left, moved.Top);
            AssertFitted("preferred monitor reconnect");
            AssertRemembered(remembered);

            // Horizontal resizing changes width but should retain automatic height.
            // WM_SIZING differentiates this deliberate resize from a DPI-scaled move.
            current = PlacementBounds(widget);
            SendPlacementMessage(handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
            SendSizing(2, current); // WMSZ_RIGHT
            SetBounds(current.Left, current.Top, current.Right - current.Left + 10, current.Bottom - current.Top);
            SendPlacementMessage(handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
            await FlushAsync();
            remembered = JsonSerializer.Serialize(_controller.Settings.WidgetPlacement);
            displays = new[] { first };
            widget.ApplyMode();
            await FlushAsync();
            AssertFitted("fallback after width-only resizing");
            AssertRemembered(remembered);
            displays = new[] { first, second };
            widget.ApplyMode();
            await FlushAsync();
            AssertFitted("reconnect after width-only resizing");

            // Actual vertical resizing remains an intentional manual height. Restore
            // must preserve it, including a round trip through a temporary display.
            current = PlacementBounds(widget);
            var manualHeight = current.Bottom - current.Top + (int)Math.Round(65 * native.Scale);
            SendPlacementMessage(handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
            SendSizing(6, current); // WMSZ_BOTTOM
            SetBounds(current.Left, current.Top, current.Right - current.Left, manualHeight);
            SendPlacementMessage(handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
            await FlushAsync();
            remembered = JsonSerializer.Serialize(_controller.Settings.WidgetPlacement);
            displays = new[] { first };
            widget.ApplyMode();
            await FlushAsync();
            AssertRemembered(remembered);
            displays = new[] { first, second };
            widget.ApplyMode();
            await FlushAsync();
            var restored = PlacementBounds(widget);
            if (Math.Abs(restored.Bottom - restored.Top - manualHeight) > 1)
                throw new InvalidOperationException("An explicit vertical resize must survive monitor restoration.");
            AssertRemembered(remembered);

            // Inject one same-DPI message synchronously inside native placement. Its
            // follow-up must survive ApplyMode's timer cleanup. No monitor DPI changes.
            var source = HwndSource.FromHwnd(handle)
                ?? throw new InvalidOperationException("The resume-layout fixture requires an HWND source.");
            var monitorReads = 0;
            var dpiMessages = 0;
            var insideApplyMode = false;
            widget.ReadMonitors = () => { monitorReads++; return displays; };
            HwndSourceHook dpiHook = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message == 0x0047 && insideApplyMode && dpiMessages == 0) // WM_WINDOWPOSCHANGED
                {
                    dpiMessages++;
                    var dpi = VisualTreeHelper.GetDpi(widget);
                    var dpiX = (int)Math.Round(dpi.PixelsPerInchX);
                    var dpiY = (int)Math.Round(dpi.PixelsPerInchY);
                    var rectangle = Marshal.AllocHGlobal(Marshal.SizeOf<PlacementNativeRect>());
                    try
                    {
                        Marshal.StructureToPtr(PlacementBounds(widget), rectangle, false);
                        SendPlacementMessage(hwnd, 0x02E0, new IntPtr((dpiY << 16) | dpiX), rectangle); // WM_DPICHANGED
                    }
                    finally { Marshal.FreeHGlobal(rectangle); }
                }
                return IntPtr.Zero;
            };
            source.AddHook(dpiHook);
            try
            {
                insideApplyMode = true;
                try { widget.ApplyMode(fitHeight: true); }
                finally { insideApplyMode = false; }
                if (dpiMessages != 1)
                    throw new InvalidOperationException("The DPI fixture must inject exactly one message inside placement.");
                var outerRestoreReads = monitorReads;
                await FlushAsync(); // Drain the deferred Loaded callback before waiting for its timer.
                await Task.Delay(850);
                await FlushAsync();
                if (monitorReads < 2 || monitorReads <= outerRestoreReads)
                    throw new InvalidOperationException("A DPI message during placement must trigger a deferred second restore.");
                AssertFitted("synchronous DPI notification during placement");
                AssertRemembered(remembered);
                var settledReads = monitorReads;
                await Task.Delay(80);
                await FlushAsync();
                if (monitorReads != settledReads || dpiMessages != 1)
                    throw new InvalidOperationException("The settled DPI fixture must not recursively restore placement.");
            }
            finally
            {
                source.RemoveHook(dpiHook);
                widget.ReadMonitors = () => displays;
            }
        }
        finally
        {
            widget.ReadMonitors = savedReader;
            _controller.Settings.WidgetPlacement = savedState;
            _controller.Settings.Placements = savedLegacy;
            widget.ApplyMode(fitHeight: true);
            await FlushAsync();
        }

        Task FlushAsync() => Dispatcher.InvokeAsync(widget.UpdateLayout, DispatcherPriority.ApplicationIdle).Task;

        void AssertFitted(string scenario)
        {
            var bounds = PlacementBounds(widget);
            var height = (bounds.Bottom - bounds.Top) / native.Scale;
            var scroll = (ScrollViewer)widget.FindName("WidgetScroll");
            // This fixture has fixed-height rows at both tested widths. Compare with
            // its settled initial height without remeasuring or repairing the visual.
            if (Math.Abs(height - expectedFittedHeight) > 1.01 / native.Scale ||
                scroll.ScrollableHeight > 0.001 || scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                throw new InvalidOperationException($"Fitted widget must retain content height after {scenario}: height={height}, expected={expectedFittedHeight}, overflow={scroll.ScrollableHeight}.");
        }

        void SendSizing(int edge, PlacementNativeRect rectangle)
        {
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<PlacementNativeRect>());
            try
            {
                Marshal.StructureToPtr(rectangle, pointer, false);
                SendPlacementMessage(handle, 0x0214, new IntPtr(edge), pointer); // WM_SIZING
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }

        void AssertRemembered(string expected)
        {
            if (JsonSerializer.Serialize(_controller.Settings.WidgetPlacement) != expected)
                throw new InvalidOperationException("Automatic resume layout must not overwrite remembered monitor geometry.");
        }

        void SetBounds(int left, int top, int width, int height)
        {
            if (!MovePlacementWindow(handle, IntPtr.Zero, left, top, width, height, 0x0014))
                throw new InvalidOperationException("Unable to position the isolated resume-layout fixture.");
        }
    }
}
