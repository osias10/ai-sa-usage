using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyResetAlertSettingsAsync(SettingsWindow settings, AccountViewModel claude)
    {
        if (_controller is not { IsDemo: true }) throw new InvalidOperationException("Reset alert UI verification requires the isolated demo.");
        var tabs = (TabControl)settings.FindName("SettingsTabs");
        var previousTab = tabs.SelectedIndex;
        var previousWidth = settings.Width;
        var session = claude.UsageAlerts.Single(alert => alert.WindowId == "session::0");
        var weekly = claude.UsageAlerts.Single(alert => alert.WindowId == "weekly_all::0");
        var original = (session.Enabled, session.ResetEnabled, session.ThresholdPercent, WeeklyReset: weekly.ResetEnabled);
        var notified = false;
        var settingsChanges = 0;
        PropertyChangedEventHandler changed = (_, args) => notified |= args.PropertyName == nameof(UsageAlertViewModel.ResetEnabled);
        EventHandler saved = (_, _) => settingsChanges++;
        session.PropertyChanged += changed;
        _controller.UsageUpdated += saved;
        SettingsWindow? reopened = null;
        try
        {
            settings.Width = settings.MinWidth;
            tabs.SelectedIndex = 0;
            await ExpandAccountAsync(settings);
            var reset = Choice(settings, session, "UsageResetEnabled");
            var weeklyReset = Choice(settings, weekly, "UsageResetEnabled");
            var threshold = Choice(settings, session, "UsageThresholdEnabled");
            var input = VisualChildren<TextBox>(settings).Single(text => ReferenceEquals(text.DataContext, session) &&
                AutomationProperties.GetAutomationId(text) == "UsageAlertThreshold");
            if (reset.IsChecked != false || weeklyReset.IsChecked != false)
                throw new InvalidOperationException("Reset notifications must be opt-in for existing accounts.");

            session.ThresholdPercent = 85;
            threshold.SetCurrentValue(CheckBox.IsCheckedProperty, false);
            var beforeToggle = settingsChanges;
            reset.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            if (!session.ResetEnabled || session.Enabled || weekly.ResetEnabled || !notified || settingsChanges <= beforeToggle ||
                !claude.Settings.UsageAlerts.Any(rule => rule.WindowId == session.WindowId && rule.ResetEnabled && !rule.Enabled && rule.ThresholdPercent == 85))
                throw new InvalidOperationException("A reset switch must immediately update and save only that quota's independent reset preference.");

            threshold.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            if (!session.Enabled || !session.ResetEnabled || session.ThresholdPercent != 85)
                throw new InvalidOperationException("Threshold and reset notifications must be enabled independently.");
            threshold.SetCurrentValue(CheckBox.IsCheckedProperty, false);
            weeklyReset.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            reset.SetCurrentValue(CheckBox.IsCheckedProperty, false);
            if (session.Enabled || session.ResetEnabled || !weekly.ResetEnabled || session.ThresholdPercent != 85)
                throw new InvalidOperationException("Turning off session notifications must preserve the weekly reset choice and saved threshold.");
            reset.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            weeklyReset.SetCurrentValue(CheckBox.IsCheckedProperty, false);
            await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var row = (Grid)reset.Parent;
            var resetBounds = reset.TransformToAncestor(row).TransformBounds(new Rect(reset.RenderSize));
            var inputBounds = input.TransformToAncestor(row).TransformBounds(new Rect(input.RenderSize));
            var name = VisualChildren<TextBlock>(row).Single(text => AutomationProperties.GetAutomationId(text) == "UsageAlertName");
            if (input.IsEnabled || Math.Abs(input.ActualWidth - 64) > 1 || resetBounds.Top < inputBounds.Bottom ||
                resetBounds.Right > row.ActualWidth + 1 || reset.ActualWidth < 140 ||
                name.ActualWidth < MeasureText(name, session.Name))
                throw new InvalidOperationException("At 680 DIP, reset controls must use a separate readable row and preserve the 64 DIP threshold field.");

            // Recreate the actual settings view while retaining the app-owned account settings.
            // Disk persistence and legacy JSON defaults are checked separately in Core tests.
            reopened = new SettingsWindow(_controller) { Width = settings.MinWidth };
            reopened.Show();
            await ExpandAccountAsync(reopened);
            if (Choice(reopened, session, "UsageResetEnabled").IsChecked != true ||
                Choice(reopened, weekly, "UsageResetEnabled").IsChecked != false ||
                Choice(reopened, session, "UsageThresholdEnabled").IsChecked != false)
                throw new InvalidOperationException("Reopening settings must preserve per-quota reset choices independently of threshold alerts.");
        }
        finally
        {
            reopened?.Close();
            session.PropertyChanged -= changed;
            _controller.UsageUpdated -= saved;
            session.Enabled = original.Enabled;
            session.ResetEnabled = original.ResetEnabled;
            session.ThresholdPercent = original.ThresholdPercent;
            weekly.ResetEnabled = original.WeeklyReset;
            settings.Width = previousWidth;
            tabs.SelectedIndex = previousTab;
            await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
        }

        async Task ExpandAccountAsync(SettingsWindow view)
        {
            ((TabControl)view.FindName("SettingsTabs")).SelectedIndex = 0;
            await Dispatcher.InvokeAsync(view.UpdateLayout, DispatcherPriority.ApplicationIdle);
            VisualChildren<Expander>(view).First(expander => ReferenceEquals(expander.DataContext, claude)).IsExpanded = true;
            await Dispatcher.InvokeAsync(view.UpdateLayout, DispatcherPriority.ApplicationIdle);
        }

        static CheckBox Choice(SettingsWindow view, UsageAlertViewModel alert, string id) =>
            VisualChildren<CheckBox>(view).Single(choice => ReferenceEquals(choice.DataContext, alert) &&
                AutomationProperties.GetAutomationId(choice) == id);
    }
}
