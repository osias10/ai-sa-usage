using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyWidgetErrorLayoutAsync(MainWindow widget)
    {
        if (_controller is not { IsDemo: true } controller)
            throw new InvalidOperationException("Error layout verification requires the isolated demo.");
        var originalError = controller.ViewModel.Error;
        var originalPlacement = controller.Settings.WidgetPlacement;
        var originalLegacy = new Dictionary<string, WindowPlacement>(controller.Settings.Placements);
        var savedPlacement = JsonSerializer.Serialize(originalPlacement);
        var savedLegacy = JsonSerializer.Serialize(originalLegacy);
        var handle = new WindowInteropHelper(widget).Handle;
        var scroll = (ScrollViewer)widget.FindName("WidgetScroll");
        try
        {
            controller.ViewModel.Error = null;
            widget.ApplyMode(fitHeight: true);
            await FlushAsync();
            if (!GetMeasureBounds(handle, out var baseline))
                throw new InvalidOperationException("Unable to read error-layout fixture bounds.");
            var baselineHeight = widget.ActualHeight;

            // A resume placement restoration may fit the widget while a previous
            // storage or connection operation has left its global error visible.
            controller.ViewModel.Error = "레이아웃 검증용 오류";
            widget.ApplyMode(fitHeight: true);
            await FlushAsync();
            if (widget.ActualHeight <= baselineHeight + 5)
                throw new InvalidOperationException("The error-layout fixture must include the visible warning row.");

            // Manual refresh clears Error before any account quotas or IDs change.
            // Clearing the banner alone must therefore trigger height adjustment.
            controller.ViewModel.Error = null;
            await FlushAsync();
            if (Math.Abs(widget.ActualHeight - baselineHeight) > 1 ||
                Math.Abs(scroll.ViewportHeight - scroll.ExtentHeight) > 2)
                throw new InvalidOperationException($"Clearing a fitted warning row must remove its empty space: " +
                    $"height={widget.ActualHeight}, baseline={baselineHeight}, viewport={scroll.ViewportHeight}, extent={scroll.ExtentHeight}.");
            if (!GetMeasureBounds(handle, out var after) ||
                after.Left != baseline.Left || after.Top != baseline.Top ||
                after.Right - after.Left != baseline.Right - baseline.Left)
                throw new InvalidOperationException("Warning-row layout changes must preserve widget position and width.");
            if (JsonSerializer.Serialize(controller.Settings.WidgetPlacement) != savedPlacement ||
                JsonSerializer.Serialize(controller.Settings.Placements) != savedLegacy)
                throw new InvalidOperationException("Warning-row layout changes must preserve remembered widget placement.");
        }
        finally
        {
            controller.ViewModel.Error = originalError;
            controller.Settings.WidgetPlacement = originalPlacement;
            controller.Settings.Placements = originalLegacy;
            widget.ApplyMode(fitHeight: true);
            await FlushAsync();
        }

        Task FlushAsync() => Dispatcher.InvokeAsync(widget.UpdateLayout, DispatcherPriority.ApplicationIdle).Task;
    }

    // This can be called from the isolated demo verification sequence. It checks
    // the live visual, rather than the detached visual used by WidgetFitSmoke.
    private async Task VerifyWidgetMeasureAsync(MainWindow widget)
    {
        if (_controller is not { IsDemo: true })
            throw new InvalidOperationException("Live measurement verification requires the isolated demo.");
        var handle = new WindowInteropHelper(widget).Handle;
        if (!GetMeasureBounds(handle, out var originalBounds))
            throw new InvalidOperationException("Unable to read measurement fixture bounds.");
        var frame = (FrameworkElement)widget.FindName("WidgetFrame");
        var scroll = (ScrollViewer)widget.FindName("WidgetScroll");
        try
        {
            await ProbeAsync("fitted live window");
            // Explicit fit probes must not leave the ScrollViewer measured against
            // infinite height when the HWND itself still has a height constraint.
            if (!MoveMeasureWindow(handle, IntPtr.Zero, originalBounds.Left, originalBounds.Top,
                    originalBounds.Right - originalBounds.Left, 80, 0x0014))
                throw new InvalidOperationException("Unable to size the isolated measurement fixture.");
            await FlushAsync();
            if (scroll.ScrollableHeight <= 0)
                throw new InvalidOperationException("The measurement fixture requires vertically constrained content.");
            await ProbeAsync("height-constrained live window");
        }
        finally
        {
            MoveMeasureWindow(handle, IntPtr.Zero, originalBounds.Left, originalBounds.Top,
                originalBounds.Right - originalBounds.Left, originalBounds.Bottom - originalBounds.Top, 0x0014);
            // Native geometry changes in this fixture are never user moves and do
            // not update the profile's remembered size or monitor.
            await FlushAsync();
        }

        async Task ProbeAsync(string fixture)
        {
            await FlushAsync();
            if (!GetMeasureBounds(handle, out var before))
                throw new InvalidOperationException("Unable to read measurement fixture bounds.");
            var beforeFrame = frame.RenderSize;
            var beforeViewport = (scroll.ViewportWidth, scroll.ViewportHeight);
            var beforeExtent = (scroll.ExtentWidth, scroll.ExtentHeight);
            var beforeBar = scroll.ComputedVerticalScrollBarVisibility;
            foreach (var width in new[] { 240d, widget.ActualWidth + 80, widget.MinWidth, widget.ActualWidth })
            {
                _ = AIUsage.App.MainWindow.MeasureWidgetHeight(frame, width, widget.MinHeight);
                await FlushAsync();
                if (!GetMeasureBounds(handle, out var after) || after != before)
                    throw new InvalidOperationException($"A content measurement must not move or resize its HWND ({fixture}).");
                if (Math.Abs(frame.RenderSize.Width - beforeFrame.Width) > 1 ||
                    Math.Abs(frame.RenderSize.Height - beforeFrame.Height) > 1 ||
                    Math.Abs(scroll.ViewportWidth - beforeViewport.ViewportWidth) > 1 ||
                    Math.Abs(scroll.ViewportHeight - beforeViewport.ViewportHeight) > 1 ||
                    Math.Abs(scroll.ExtentWidth - beforeExtent.ExtentWidth) > 1 ||
                    Math.Abs(scroll.ExtentHeight - beforeExtent.ExtentHeight) > 1 ||
                    scroll.ComputedVerticalScrollBarVisibility != beforeBar)
                    throw new InvalidOperationException($"A content measurement left a stale live layout ({fixture}, width={width}): " +
                        $"frame={frame.RenderSize}, viewport={scroll.ViewportWidth}x{scroll.ViewportHeight}, " +
                        $"extent={scroll.ExtentWidth}x{scroll.ExtentHeight}.");
            }
        }

        Task FlushAsync() => Dispatcher.InvokeAsync(widget.UpdateLayout, DispatcherPriority.ApplicationIdle).Task;
    }

    [StructLayout(LayoutKind.Sequential)]
    private record struct MeasureNativeRect(int Left, int Top, int Right, int Bottom);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool GetMeasureBounds(IntPtr handle, out MeasureNativeRect bounds);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool MoveMeasureWindow(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
}
