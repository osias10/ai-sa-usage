using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyWindowBehaviorAsync(MainWindow widget, SettingsWindow settings)
    {
        await FlushAsync();
        AssertSwitcherStyles();
        var scroll = VisualChildren<ScrollViewer>(widget).First();
        if (Math.Abs(widget.ActualWidth - 230) > 1 || widget.ActualHeight >= 400 ||
            scroll.ScrollableHeight > 1 || Math.Abs(scroll.ViewportHeight - scroll.ExtentHeight) > 3)
            throw new InvalidOperationException($"Startup must fit content at the saved width instead of restoring a tall empty window: {widget.ActualWidth} x {widget.ActualHeight}.");
        var fittedHeight = widget.ActualHeight;
        widget.ApplyMode();
        await FlushAsync();
        if (Math.Abs(widget.ActualHeight - fittedHeight) > 1)
            throw new InvalidOperationException("Restoring placement after startup must not revive the previous oversized height.");

        widget.Hide();
        _controller!.ShowWidget();
        await FlushAsync();
        AssertSwitcherStyles();
        if (!widget.IsVisible || !settings.IsVisible)
            throw new InvalidOperationException("The tray must still reopen the widget independently of settings.");

        var lockChoice = (CheckBox)settings.FindName("LockChoice");
        var topmostChoice = (CheckBox)settings.FindName("TopmostChoice");
        var previousLock = lockChoice.IsChecked;
        var previousTopmost = topmostChoice.IsChecked;
        try
        {
            lockChoice.IsChecked = true;
            topmostChoice.IsChecked = false;
            await FlushAsync();
            AssertSwitcherStyles();
            if (widget.ResizeMode != ResizeMode.NoResize)
                throw new InvalidOperationException("Window locking must keep working for the tool window.");
        }
        finally
        {
            lockChoice.IsChecked = previousLock;
            topmostChoice.IsChecked = previousTopmost;
        }
        await FlushAsync();
        AssertSwitcherStyles();
        if (widget.ResizeMode != ResizeMode.CanResizeWithGrip)
            throw new InvalidOperationException("Unlocking must restore widget resizing.");

        async Task FlushAsync() => await Dispatcher.InvokeAsync(() =>
        {
            widget.UpdateLayout();
            settings.UpdateLayout();
        }, DispatcherPriority.ApplicationIdle);

        void AssertSwitcherStyles()
        {
            const long toolWindow = 0x80, appWindow = 0x40000, noActivate = 0x08000000;
            var widgetStyle = ReadExtendedWindowStyle(new WindowInteropHelper(widget).Handle, -20).ToInt64();
            var settingsStyle = ReadExtendedWindowStyle(new WindowInteropHelper(settings).Handle, -20).ToInt64();
            if (widget.ShowInTaskbar || (widgetStyle & toolWindow) == 0 || (widgetStyle & (appWindow | noActivate)) != 0)
                throw new InvalidOperationException($"Widget must be excluded from Alt+Tab while retaining normal input: 0x{widgetStyle:X}.");
            if (!settings.ShowInTaskbar || (settingsStyle & toolWindow) != 0 || (settingsStyle & appWindow) == 0)
                throw new InvalidOperationException($"Settings must remain an ordinary switchable window: 0x{settingsStyle:X}.");
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr ReadExtendedWindowStyle(IntPtr hwnd, int index);
}
