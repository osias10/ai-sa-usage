using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyWidgetVisibilityAsync(MainWindow widget, SettingsWindow settings)
    {
        if (_controller is not { IsDemo: true } || _tray is null)
            throw new InvalidOperationException("Widget visibility verification requires the isolated demo.");
        var controller = _controller;
        var choice = (CheckBox)settings.FindName("WidgetVisibleChoice");
        var original = (controller.Settings.ShowWidget, controller.Settings.ClickThrough);
        SettingsWindow? reopened = null;
        try
        {
            if (JsonSerializer.Deserialize<AppSettings>("{}")?.ShowWidget != true)
                throw new InvalidOperationException("Existing profiles must keep showing the widget by default.");
            await controller.SetWidgetVisibilityAsync(true);
            await FlushAsync();
            AssertVisible(true);

            // The settings switch hides only the widget; the settings and tray stay usable.
            choice.SetCurrentValue(CheckBox.IsCheckedProperty, false);
            await FlushAsync();
            AssertVisible(false);
            if (!settings.IsVisible) throw new InvalidOperationException("Hiding the widget must keep settings open.");
            if (JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(controller.Settings))?.ShowWidget != false)
                throw new InvalidOperationException("The hidden preference must survive settings serialization.");
            reopened = new SettingsWindow(controller);
            if (((CheckBox)reopened.FindName("WidgetVisibleChoice")).IsChecked != false)
                throw new InvalidOperationException("Reopened settings must retain widget visibility.");
            reopened.Close();
            reopened = null;

            _tray.WidgetVisibilityItem.PerformClick();
            await FlushAsync();
            AssertVisible(true);
            _tray.WidgetVisibilityItem.PerformClick();
            await FlushAsync();
            AssertVisible(false);
            controller.ShowWidget();
            await FlushAsync();
            AssertVisible(true);

            controller.ViewModel.HideCommand.Execute(null);
            await FlushAsync();
            AssertVisible(false);
            await controller.SetWidgetVisibilityAsync(true);
            var source = HwndSource.FromHwnd(new WindowInteropHelper(widget).Handle)
                ?? throw new InvalidOperationException("The widget must have an input source.");
            widget.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await FlushAsync();
            AssertVisible(false);
            await controller.SetWidgetVisibilityAsync(true);
            widget.Close();
            await FlushAsync();
            AssertVisible(false);
            await controller.SetWidgetVisibilityAsync(true);
            AssertVisible(true);

            // A delayed/failing disk write cannot change the live widget prematurely.
            var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.SaveWidgetVisibilityForVerification = () => releaseSave.Task;
            var pending = controller.SetWidgetVisibilityAsync(false);
            await FlushAsync();
            if (!widget.IsVisible || pending.IsCompleted)
                throw new InvalidOperationException("Visibility must wait for a successful durable save.");
            releaseSave.SetException(new IOException("Simulated visibility-save failure."));
            try
            {
                await pending;
                throw new InvalidOperationException("The injected visibility-save failure was lost.");
            }
            catch (IOException) { }
            await FlushAsync();
            AssertVisible(true);
            controller.SaveWidgetVisibilityForVerification = null;

            await controller.SetClickThroughAsync(true);
            await controller.SetWidgetVisibilityAsync(false);
            _tray.WidgetVisibilityItem.PerformClick();
            await FlushAsync();
            AssertVisible(true);
            if (!controller.Settings.ClickThrough || widget.IsHitTestVisible || !settings.IsVisible)
                throw new InvalidOperationException("Reopening must retain click-through and the independent settings window.");
        }
        finally
        {
            controller.SaveWidgetVisibilityForVerification = null;
            reopened?.Close();
            await controller.SetClickThroughAsync(original.ClickThrough);
            await controller.SetWidgetVisibilityAsync(original.ShowWidget);
            await FlushAsync();
        }

        async Task FlushAsync() => await Dispatcher.InvokeAsync(() =>
        {
            widget.UpdateLayout();
            settings.UpdateLayout();
        }, DispatcherPriority.ApplicationIdle);

        void AssertVisible(bool expected)
        {
            if (controller.Settings.ShowWidget != expected || widget.IsVisible != expected || choice.IsChecked != expected ||
                _tray.WidgetVisibilityItem.Checked != expected || _tray.WidgetVisibilityItem.Text != (expected ? "위젯 숨기기" : "위젯 열기"))
                throw new InvalidOperationException($"Widget, settings and tray visibility must agree (expected={expected}).");
        }
    }
}
