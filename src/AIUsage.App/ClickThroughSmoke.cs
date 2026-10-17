using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyClickThroughAsync(MainWindow widget, SettingsWindow settings)
    {
        if (_controller is not { IsDemo: true } || _tray is null)
            throw new InvalidOperationException("Click-through verification requires the isolated demo.");
        var choice = (CheckBox)settings.FindName("ClickThroughChoice");
        var lockChoice = (CheckBox)settings.FindName("LockChoice");
        var topmostChoice = (CheckBox)settings.FindName("TopmostChoice");
        var hint = (TextBlock)settings.FindName("ClickThroughHint");
        var original = (_controller.Settings.WidgetTransparencyPercent, _controller.Settings.PositionLocked, _controller.Settings.AlwaysOnTop,
            widget.Left, widget.Top, widget.ActualWidth, widget.ActualHeight);
        MainWindow? coldWindow = null;
        SettingsWindow? reopened = null;
        try
        {
            AssertMode(false);
            if (hint.Text.Contains("Ctrl+Alt+W") != widget.ClickThroughHotkeyAvailable)
                throw new InvalidOperationException("Only an available shortcut may be offered in settings.");
            var applyError = new InvalidOperationException("Simulated input-style failure.");
            var failOnce = true;
            EventHandler failApplying = (_, _) =>
            {
                if (!failOnce) return;
                failOnce = false;
                throw applyError;
            };
            _controller.PreferencesUpdated += failApplying;
            try
            {
                try
                {
                    await _controller.SetClickThroughAsync(true);
                    throw new InvalidOperationException("The failed input-style change should be reported.");
                }
                catch (InvalidOperationException error) when (ReferenceEquals(error.InnerException, applyError)) { }
            }
            finally { _controller.PreferencesUpdated -= failApplying; }
            AssertMode(false);
            choice.IsChecked = true;
            await FlushAsync();
            AssertMode(true);
            if (Math.Abs(widget.Left - original.Left) > 1 || Math.Abs(widget.Top - original.Top) > 1 ||
                Math.Abs(widget.ActualWidth - original.ActualWidth) > 1 || Math.Abs(widget.ActualHeight - original.ActualHeight) > 1)
                throw new InvalidOperationException("Click-through must preserve widget position and dimensions.");
            topmostChoice.IsChecked = !original.AlwaysOnTop;
            await FlushAsync();
            AssertMode(true);
            topmostChoice.IsChecked = original.AlwaysOnTop;

            foreach (var percent in new[] { 0, 45, 100 })
            {
                _controller.SetWidgetTransparency(percent);
                await FlushAsync();
                AssertMode(true);
                if (Math.Abs(widget.Opacity - (100 - percent) / 100d) > 0.000001 || settings.Opacity != 1)
                    throw new InvalidOperationException("Click-through must work independently of whole-widget transparency.");
            }
            // The tray must remain usable even when the entire widget is invisible.
            _tray.ClickThroughItem.PerformClick();
            await FlushAsync();
            AssertMode(false);
            if (_controller.Settings.WidgetTransparencyPercent != 100)
                throw new InvalidOperationException("Disabling click-through must not change transparency.");

            _controller.SetWidgetTransparency(original.WidgetTransparencyPercent);
            lockChoice.IsChecked = true;
            await _controller.SetClickThroughAsync(true);
            await _controller.SetClickThroughAsync(false);
            await FlushAsync();
            AssertMode(false);
            if (!_controller.Settings.PositionLocked || widget.ResizeMode != ResizeMode.NoResize)
                throw new InvalidOperationException("Leaving click-through must preserve a separate position lock.");
            lockChoice.IsChecked = original.PositionLocked;
            await FlushAsync();

            if (widget.ClickThroughHotkeyAvailable)
            {
                await SendToggleAsync();
                AssertMode(true);
                widget.Hide();
                await SendToggleAsync();
                AssertMode(false);
                if (widget.IsVisible) throw new InvalidOperationException("Toggling input must not unhide the widget.");
                _controller.ShowWidget();
            }

            await _controller.SetClickThroughAsync(true);
            widget.Hide();
            _controller.ShowWidget();
            await FlushAsync();
            AssertMode(true);
            if (!widget.IsVisible || !settings.IsVisible)
                throw new InvalidOperationException("The tray must reopen a click-through widget without hiding settings.");

            // Create an HWND from already-enabled settings: the first frame must never
            // be clickable. A second window must also tolerate an occupied shortcut.
            coldWindow = new MainWindow(_controller) { AllowClose = true };
            var coldHandle = new WindowInteropHelper(coldWindow).EnsureHandle();
            AssertStyles(coldHandle, true);
            if (coldWindow.ShowActivated || coldWindow.IsHitTestVisible || coldWindow.ResizeMode != ResizeMode.NoResize)
                throw new InvalidOperationException("Startup must restore click-through before showing the widget.");
            if (widget.ClickThroughHotkeyAvailable && coldWindow.ClickThroughHotkeyAvailable)
                throw new InvalidOperationException("An occupied global shortcut must not be registered twice.");
            _controller.Window = coldWindow;
            reopened = new SettingsWindow(_controller);
            if (coldWindow.ClickThroughHotkeyAvailable ||
                ((TextBlock)reopened.FindName("ClickThroughHint")).Text.Contains("Ctrl+Alt+W"))
                throw new InvalidOperationException("An occupied shortcut must leave settings with a tray recovery hint.");
            reopened.Close();
            reopened = null;
            _controller.Window = widget;
            coldWindow.Close();
            coldWindow = null;

            reopened = new SettingsWindow(_controller);
            if (((CheckBox)reopened.FindName("ClickThroughChoice")).IsChecked != true)
                throw new InvalidOperationException("Reopened settings must show the current input mode.");
            reopened.Close();
            reopened = null;
            _tray.ClickThroughItem.PerformClick();
            await FlushAsync();
            AssertMode(false);
            if (widget.ClickThroughHotkeyAvailable)
            {
                await SendToggleAsync();
                AssertMode(true);
                await SendToggleAsync();
                AssertMode(false);
            }
        }
        finally
        {
            _controller.Window = widget;
            coldWindow?.Close();
            reopened?.Close();
            await _controller.SetClickThroughAsync(false);
            lockChoice.IsChecked = original.PositionLocked;
            topmostChoice.IsChecked = original.AlwaysOnTop;
            _controller.SetWidgetTransparency(original.WidgetTransparencyPercent);
            if (!widget.IsVisible) _controller.ShowWidget();
            await FlushAsync();
        }

        async Task FlushAsync() => await Dispatcher.InvokeAsync(() =>
        {
            widget.UpdateLayout();
            settings.UpdateLayout();
        }, DispatcherPriority.ApplicationIdle);

        async Task SendToggleAsync()
        {
            // Exercise the same native message route without injecting keyboard input
            // into the user's desktop or another application.
            if (!PostWidgetMessage(new WindowInteropHelper(widget).Handle, 0x0312,
                new IntPtr(AIUsage.App.MainWindow.ClickThroughHotkeyId), new IntPtr((0x57 << 16) | 0x0003)))
                throw new InvalidOperationException("Could not post the widget shortcut message.");
            await FlushAsync();
        }

        void AssertMode(bool enabled)
        {
            if (_controller.Settings.ClickThrough != enabled || choice.IsChecked != enabled || _tray.ClickThroughItem.Checked != enabled)
                throw new InvalidOperationException("Settings, tray, and controller input modes must agree.");
            AssertStyles(new WindowInteropHelper(widget).Handle, enabled);
            var resize = enabled || _controller.Settings.PositionLocked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
            if (widget.ShowActivated == enabled || widget.IsHitTestVisible == enabled || widget.Focusable == enabled || widget.ResizeMode != resize)
                throw new InvalidOperationException("Mouse, focus, and resizing must follow the click-through preference.");
            var settingsStyle = ReadExtendedWindowStyle(new WindowInteropHelper(settings).Handle, -20).ToInt64();
            if ((settingsStyle & (0x20 | 0x08000000)) != 0 || !settings.IsHitTestVisible)
                throw new InvalidOperationException("Click-through must never apply to the settings window.");
        }

        static void AssertStyles(IntPtr handle, bool enabled)
        {
            const long transparent = 0x20, noActivate = 0x08000000, layered = 0x80000, toolWindow = 0x80, appWindow = 0x40000;
            var style = ReadExtendedWindowStyle(handle, -20).ToInt64();
            if ((style & layered) == 0 || (style & toolWindow) == 0 || (style & appWindow) != 0 ||
                (style & (transparent | noActivate)) != (enabled ? transparent | noActivate : 0))
                throw new InvalidOperationException($"Unexpected native click-through/switcher styles: 0x{style:X} (enabled={enabled}).");
        }
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    private static extern bool PostWidgetMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
