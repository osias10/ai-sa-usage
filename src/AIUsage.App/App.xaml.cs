using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AIUsage.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _eventWait;
    private AppController? _controller;
    private TrayService? _tray;
    private bool _exiting;
    private bool _verification;
    private bool _hasUiError;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var demo = e.Args.Contains("--demo") || e.Args.Contains("--smoke-test") || e.Args.Contains("--screenshot");
        _verification = e.Args.Contains("--smoke-test") || e.Args.Contains("--screenshot");
        string key = @"Local\AIUsageWidget-" + (demo ? "Demo-" + Environment.ProcessId : Environment.UserName);
        _instanceMutex = new Mutex(true, key, out var isFirst);
        if (!isFirst)
        {
            try { using var signal = EventWaitHandle.OpenExisting(key + "-Show"); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            _instanceMutex.Dispose(); _instanceMutex = null;
            Shutdown(); return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, key + "-Show");
        _eventWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(() => _controller?.ShowWidget()), null, Timeout.Infinite, false);
        DispatcherUnhandledException += HandleDispatcherError;
        try
        {
            _controller = await AppController.CreateAsync(demo, e.Args.Contains("--mini"), e.Args.Contains("--light"));
            ThemeManager.Apply(_controller.Settings.Theme);
            if (e.Args.Contains("--smoke-test"))
                _controller.Settings.Placements["compact-v2:last"] = new(100, 100, 230, 600);
            var window = new MainWindow(_controller);
            if (demo) window.Title = "AI SA · Demo";
            _controller.Window = window;
            MainWindow = window;
            _tray = new TrayService(_controller, ExitApplication);
            window.Show();
            _controller.Start();
            if (e.Args.Contains("--settings") || (!demo && _controller.ViewModel.IsEmpty)) _controller.ShowSettings();
            if (demo && (e.Args.Contains("--settings-widget") || e.Args.Contains("--settings-info")))
            {
                _controller.ShowSettings();
                var previewSettings = Windows.OfType<SettingsWindow>().Single();
                var previewTabs = (System.Windows.Controls.TabControl)previewSettings.FindName("SettingsTabs");
                previewTabs.SelectedIndex = e.Args.Contains("--settings-widget") ? 1 : 2;
            }
            if (e.Args.Contains("--screenshot"))
            {
                int index = Array.IndexOf(e.Args, "--screenshot");
                if (index + 1 >= e.Args.Length) throw new ArgumentException("Screenshot output path is required.");
                await Task.Delay(800);
                var target = Windows.OfType<Window>().LastOrDefault(w => w.IsVisible) ?? window;
                SaveScreenshot(target, e.Args[index + 1]);
                await ExitAsync();
            }
            else if (e.Args.Contains("--smoke-test"))
            {
                await Task.Delay(1000);
                if (!window.IsVisible || window.ActualWidth < 200 || _controller.ViewModel.Accounts.Count != 3)
                    throw new InvalidOperationException("The demo window did not initialize correctly.");
                var claude = _controller.ViewModel.Accounts.First(a => a.Settings.Provider == AIUsage.Core.ProviderKind.Claude);
                if (claude.CompactWindows.Count != 2 || claude.CompactWindows[0].Source.UsedPercent != 37 || claude.CompactWindows[1].Source.UsedPercent != 68)
                    throw new InvalidOperationException("Claude must show both session and overall weekly usage.");
                _controller.ShowSettings();
                var settings = Windows.OfType<SettingsWindow>().Single();
                if (settings.Owner is not null || !settings.IsVisible)
                    throw new InvalidOperationException("Settings must open as an independent window.");
                await VerifyWindowBehaviorAsync(window, settings);
                var settingsTabs = (System.Windows.Controls.TabControl)settings.FindName("SettingsTabs");
                for (var index = 0; index < settingsTabs.Items.Count; index++)
                {
                    settingsTabs.SelectedIndex = index;
                    await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
                }
                settingsTabs.SelectedIndex = 0;
                await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
                ((System.Windows.Controls.Expander)settings.FindName("AddAccountExpander")).IsExpanded = true;
                var accountOptions = VisualChildren<System.Windows.Controls.Expander>(settings).First(expander => ReferenceEquals(expander.DataContext, claude));
                accountOptions.IsExpanded = true;
                await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
                await VerifyStatusIndicatorsAsync(window, settings, claude);
                var menuButton = VisualChildren<System.Windows.Controls.Button>(settings).First(button => ReferenceEquals(button.DataContext, claude) && button.ContextMenu is not null);
                menuButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var accountMenu = menuButton.ContextMenu!;
                if (!ReferenceEquals(accountMenu.DataContext, claude) || !accountMenu.Items.OfType<System.Windows.Controls.MenuItem>().Any(item => ReferenceEquals(item.Command, claude.ConnectCommand)))
                    throw new InvalidOperationException("The account menu must target its own account.");
                accountMenu.IsOpen = false;
                // Exercise the actual settings controls so preferences and all surfaces stay in sync.
                settingsTabs.SelectedIndex = 1;
                await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var lastCheckedChoice = (System.Windows.Controls.CheckBox)settings.FindName("LastCheckedChoice");
                var checkedLabels = VisualChildren<System.Windows.Controls.TextBlock>(window).Where(text => text.Name == "LastCheckedLabel").ToArray();
                if (lastCheckedChoice.IsChecked != true || checkedLabels.Length != 3 || checkedLabels.Any(text => text.Visibility != Visibility.Visible))
                    throw new InvalidOperationException("Each widget account must show its last successful check by default.");
                window.FitContent();
                await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var heightWithChecks = window.ActualHeight;
                var widthWithChecks = window.ActualWidth;
                lastCheckedChoice.IsChecked = false;
                await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                if (_controller.Settings.ShowLastChecked || _controller.ViewModel.Accounts.Any(account => account.ShowLastChecked) ||
                    checkedLabels.Any(text => text.Visibility != Visibility.Collapsed) || window.ActualHeight >= heightWithChecks || window.ActualWidth != widthWithChecks)
                    throw new InvalidOperationException("Hiding check times must remove their rows and keep the chosen widget width.");
                lastCheckedChoice.IsChecked = true;
                await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                if (!_controller.Settings.ShowLastChecked || checkedLabels.Any(text => text.Visibility != Visibility.Visible) || Math.Abs(window.ActualHeight - heightWithChecks) > 1)
                    throw new InvalidOperationException("Enabling check times must restore their rows and fitted height.");
                var originalStatus = _controller.GetStatuses().Single(status => status.AccountId == claude.Id);
                var originalCheck = claude.CompactUpdatedLabel;
                claude.Apply(originalStatus with { State = AIUsage.Core.UsageState.Offline });
                if (claude.CompactUpdatedLabel != "이전 " + originalCheck)
                    throw new InvalidOperationException("A failed refresh must keep the prior successful timestamp.");
                claude.Apply(originalStatus with { Snapshot = null, State = AIUsage.Core.UsageState.SignInRequired });
                if (claude.CompactUpdatedLabel != "확인 기록 없음")
                    throw new InvalidOperationException("An unqueried account must not show a fabricated check time.");
                claude.Apply(originalStatus);
                var transparency = (System.Windows.Controls.Slider)settings.FindName("WidgetTransparencySlider");
                foreach (var percent in new[] { 0, 100, 45 })
                {
                    transparency.Value = percent;
                    await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    var opacity = (100 - percent) / 100d;
                    if (_controller.Settings.WidgetTransparencyPercent != percent || Math.Abs(window.Opacity - opacity) > 0.000001 ||
                        ((SolidColorBrush)FindResource("WidgetBrush")).Color.A != 255 ||
                        ((SolidColorBrush)FindResource("TextBrush")).Color.A != 255 ||
                        ((SolidColorBrush)FindResource("SurfaceBrush")).Color.A != 255 ||
                        Windows.OfType<Window>().Any(other => !ReferenceEquals(other, window) && other.Opacity != 1))
                        throw new InvalidOperationException("The transparency slider must change the entire widget immediately while leaving other windows and shared brushes opaque.");
                }
                var themeChoice = (System.Windows.Controls.ComboBox)settings.FindName("ThemeChoice");
                var originalTheme = themeChoice.SelectedIndex;
                themeChoice.SelectedIndex = originalTheme == 1 ? 2 : 1;
                await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
                if (Math.Abs(window.Opacity - 0.55) > 0.000001 || ((SolidColorBrush)FindResource("WidgetBrush")).Color.A != 255 || settings.Opacity != 1)
                    throw new InvalidOperationException("Changing the theme must preserve whole-widget transparency without applying opacity to the settings window.");
                themeChoice.SelectedIndex = originalTheme;
                ((System.Windows.Controls.ComboBox)settings.FindName("DisplayModeChoice")).SelectedIndex = 1;
                var codex = _controller.ViewModel.Accounts.First(a => a.Settings.Provider == AIUsage.Core.ProviderKind.Codex);
                ((System.Windows.Controls.ComboBox)settings.FindName("TrayAccountChoice")).SelectedValue = codex.Id;
                await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
                if (!_controller.Settings.ShowRemaining || claude.CompactWindows[0].Value != 63 || claude.CompactWindows[1].Percent != "32%" ||
                    _controller.Settings.TrayAccountId != codex.Id || _tray.CurrentReading.IconText != "76")
                    throw new InvalidOperationException("Remaining mode must update both meters and the selected tray account.");
                var unknown = new UsageWindowViewModel(new("unknown", "미제공", null, null), () => claude.Accent, () => true);
                if (unknown.HasValue || unknown.Percent != "—")
                    throw new InvalidOperationException("Missing usage must stay unknown in remaining mode.");
                var sessionAlert = claude.UsageAlerts.Single(a => a.WindowId == "session::0");
                var weeklyAlert = claude.UsageAlerts.Single(a => a.WindowId == "weekly_all::0");
                sessionAlert.ThresholdPercent = 85;
                sessionAlert.Enabled = true;
                if (!claude.Settings.UsageAlerts.Any(a => a.WindowId == "session::0" && a.Enabled && a.ThresholdPercent == 85) || weeklyAlert.Enabled)
                    throw new InvalidOperationException("Session and weekly alerts must be configured independently.");
                _tray.ShowSummary();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (!_tray.IsSummaryOpen) throw new InvalidOperationException("The tray account summary must open.");
                _tray.HideSummary();
                ((System.Windows.Controls.ComboBox)settings.FindName("DisplayModeChoice")).SelectedIndex = 0;
                await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
                if (claude.CompactWindows[0].Value != 37 || _tray.CurrentReading.IconText != "24")
                    throw new InvalidOperationException("Switching back must restore used percentages.");
                await VerifyWidgetSelectionAsync(window, settings, claude, codex);
                await VerifyTrayWindowSelectionAsync(settings, claude, codex);
                lastCheckedChoice.IsChecked = false;
                window.Hide();
                if (!settings.IsVisible) throw new InvalidOperationException("Hiding the widget must keep settings open.");
                window.Show();
                settings.Close();
                if (!window.IsVisible) throw new InvalidOperationException("Closing settings must keep the widget open.");
                _controller.ShowSettings();
                var reopenedSettings = Windows.OfType<SettingsWindow>().Single();
                if (((System.Windows.Controls.Slider)reopenedSettings.FindName("WidgetTransparencySlider")).Value != 45 ||
                    Math.Abs(window.Opacity - 0.55) > 0.000001 || reopenedSettings.Opacity != 1)
                    throw new InvalidOperationException("Reopening settings must restore the selected transparency.");
                if (((System.Windows.Controls.CheckBox)reopenedSettings.FindName("LastCheckedChoice")).IsChecked != false)
                    throw new InvalidOperationException("Reopening settings must preserve hidden check times.");
                if ((string?)((System.Windows.Controls.ComboBox)reopenedSettings.FindName("TrayWindowChoice")).SelectedValue != "weekly_scoped:Fable:0")
                    throw new InvalidOperationException("Reopening settings must preserve the chosen tray quota.");
                reopenedSettings.Close();
                foreach (var account in _controller.ViewModel.Accounts) account.Pinned = ReferenceEquals(account, claude);
                window.FitContent();
                window.Width = window.MinWidth;
                await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                if (window.ActualWidth > 205 || window.ActualHeight > 85)
                    throw new InvalidOperationException($"A single Claude account must fit in a small widget (actual {window.ActualWidth} × {window.ActualHeight}; minimum {window.MinWidth} × {window.MinHeight}).");
                var background = (SolidColorBrush)FindResource("WidgetBrush");
                if (background.Color.A != 255 || Math.Abs(window.Opacity - 0.55) > 0.000001)
                    throw new InvalidOperationException("45% transparency must leave the entire widget at 55% opacity, with an opaque base brush.");
                await ExitAsync();
            }
        }
        catch (Exception error)
        {
            if (!demo) MessageBox.Show("앱을 시작할 수 없습니다.\n" + error.Message, "AI SA", MessageBoxButton.OK, MessageBoxImage.Error);
            else Console.Error.WriteLine(error.ToString());
            await ExitAsync(1);
        }
    }
    private void HandleDispatcherError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _controller?.ShowError(e.Exception);
        if (_verification) { _hasUiError = true; Console.Error.WriteLine(e.Exception.ToString()); }
        e.Handled = true;
    }
    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }
    private async void ExitApplication() => await ExitAsync();
    private async Task ExitAsync(int code = 0)
    {
        if (_exiting) return;
        _exiting = true;
        if (_hasUiError) code = 1;
        _tray?.Dispose();
        if (_controller is not null)
        {
            _controller.Window?.CapturePlacement();
            try { await _controller.DisposeAsync(); } catch { code = 1; }
            if (_controller.Window is { } window) { window.AllowClose = true; window.Close(); }
        }
        _eventWait?.Unregister(null);
        _showEvent?.Dispose();
        try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _instanceMutex?.Dispose();
        Shutdown(code);
    }
    private static void SaveScreenshot(Window window, string path)
    {
        window.UpdateLayout();
        var visual = window.Content as FrameworkElement ?? window;
        visual.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(visual) { AutoLayoutContent = false }, null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
        bitmap.Render(drawing);
        bitmap.Freeze();
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        if (!pixels.Any(value => value != 0))
            throw new InvalidOperationException("WPF renderer returned an empty frame. Capture the demo on an interactive Windows desktop.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var absolutePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        using var output = File.Create(absolutePath);
        encoder.Save(output);
    }
}
