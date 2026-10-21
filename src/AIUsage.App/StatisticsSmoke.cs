using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyStatisticsAsync()
    {
        // A separate demo controller keeps chart fixtures and destructive checks away from
        // both the user's data and the demo shared by the other UI smoke checks.
        await using var controller = await AppController.CreateAsync(true, false, ThemeManager.IsLight, statisticsDemo: true);
        var settings = new SettingsWindow(controller) { Width = 680 };
        SettingsWindow? reopened = null;
        try
        {
            settings.Show();
            var tabs = (TabControl)settings.FindName("SettingsTabs");
            tabs.SelectedIndex = 3;
            var page = (UsageStatisticsView)settings.FindName("StatisticsPage");
            var accounts = (ComboBox)page.FindName("StatisticsAccountChoice");
            var windows = (ComboBox)page.FindName("StatisticsWindowChoice");
            var range = (ComboBox)page.FindName("StatisticsRangeChoice");
            var chart = (UsageStatisticsChart)page.FindName("StatisticsChart");
            var recording = (CheckBox)page.FindName("StatisticsRecordingChoice");
            var widgetRecording = (CheckBox)settings.FindName("UsageStatisticsChoice");
            var claude = controller.Settings.Accounts.First(account => account.Provider == ProviderKind.Claude);
            var codex = controller.Settings.Accounts.First(account => account.Provider == ProviderKind.Codex);
            await LayoutAsync();
            if (!page.IsVisible || accounts.Items.Count != controller.Settings.Accounts.Count || range.SelectedIndex != 2 ||
                recording.IsChecked != true || widgetRecording.IsChecked != true || chart.ActualWidth < 200 || chart.ActualHeight < 220)
                throw new InvalidOperationException("The Statistics tab must show the recorded demo data at the minimum settings width.");

            accounts.SelectedValue = claude.Id;
            windows.SelectedValue = "session::0";
            await LayoutAsync();
            AssertSeries(claude, "session::0");
            range.SelectedIndex = 0;
            await LayoutAsync();
            var hourCount = Report().SampleCount;
            if (hourCount < 2 || Math.Abs((Report().To - Report().From).TotalHours - 1) > .001)
                throw new InvalidOperationException("The 1-hour statistics range must use its actual time window and seeded observations.");
            range.SelectedIndex = 1;
            await LayoutAsync();
            var sixHourCount = Report().SampleCount;
            if (sixHourCount < hourCount || Math.Abs((Report().To - Report().From).TotalHours - 6) > .001)
                throw new InvalidOperationException("Changing the statistics range must recalculate the selected account and quota.");
            range.SelectedIndex = 2;
            await LayoutAsync();
            var dayCount = Report().SampleCount;
            if (dayCount < sixHourCount || Math.Abs((Report().To - Report().From).TotalHours - 24) > .001)
                throw new InvalidOperationException("The 24-hour statistics range must include the retained observations.");

            windows.SelectedValue = "weekly_scoped:Fable:0";
            await LayoutAsync();
            AssertSeries(claude, "weekly_scoped:Fable:0");
            if (!Report().Samples.All(sample => sample.WindowName.Contains("Fable", StringComparison.Ordinal)))
                throw new InvalidOperationException("Fable statistics must show the provider's own distinct quota series.");
            windows.SelectedValue = "archived";
            await LayoutAsync();
            AssertSeries(claude, "archived");
            page.RefreshView();
            await LayoutAsync();
            if (!Equals(windows.SelectedValue, "archived"))
                throw new InvalidOperationException("A historical quota missing from the current response must stay selectable after refresh.");

            accounts.SelectedValue = codex.Id;
            windows.SelectedValue = "primary";
            await LayoutAsync();
            AssertSeries(codex, "primary");
            VerifyChartSelection(chart);
            VerifyChartRenderingAtScale(chart);

            var savedCount = controller.UsageStatisticsCount;
            recording.SetCurrentValue(CheckBox.IsCheckedProperty, false);
            await LayoutAsync();
            if (controller.Settings.SaveUsageStatistics || widgetRecording.IsChecked != false ||
                controller.UsageStatisticsCount != savedCount || Report().SampleCount == 0 ||
                string.IsNullOrWhiteSpace(((TextBlock)page.FindName("StatisticsStatusLabel")).Text))
                throw new InvalidOperationException("Pausing statistics must retain the chart and synchronize the existing widget setting.");

            widgetRecording.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            await LayoutAsync();
            if (!controller.Settings.SaveUsageStatistics || recording.IsChecked != true || controller.UsageStatisticsCount != savedCount)
                throw new InvalidOperationException("The existing statistics switch must also synchronize the Statistics tab without creating observations.");
            recording.SetCurrentValue(CheckBox.IsCheckedProperty, false);
            await LayoutAsync();

            reopened = new SettingsWindow(controller) { Width = 680 };
            reopened.Show();
            ((TabControl)reopened.FindName("SettingsTabs")).SelectedIndex = 3;
            await Dispatcher.InvokeAsync(reopened.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var reopenedPage = (UsageStatisticsView)reopened.FindName("StatisticsPage");
            if (((CheckBox)reopenedPage.FindName("StatisticsRecordingChoice")).IsChecked != false ||
                ((UsageStatisticsChart)reopenedPage.FindName("StatisticsChart")).Report is not { SampleCount: > 0 })
                throw new InvalidOperationException("Reopening Statistics must retain saved readings even when collection is paused.");
            reopened.Close();
            reopened = null;

            var codexVm = controller.ViewModel.Accounts.Single(account => account.Id == codex.Id);
            var codexIndex = controller.ViewModel.Accounts.IndexOf(codexVm);
            controller.Settings.Accounts.Remove(codex);
            controller.ViewModel.Accounts.Remove(codexVm);
            await LayoutAsync();
            if (Equals(accounts.SelectedValue, codex.Id) || Report().Samples.Any(sample => sample.AccountId == codex.Id))
                throw new InvalidOperationException("Removing the selected account must select another account and discard the old chart immediately.");
            controller.Settings.Accounts.Insert(codexIndex, codex);
            controller.ViewModel.Accounts.Insert(codexIndex, codexVm);
            await LayoutAsync();

            accounts.SelectedValue = claude.Id;
            windows.SelectedValue = "session::0";
            await LayoutAsync();
            var originalIdentity = claude.IdentityKey;
            try
            {
                // A stale identity can exist between a provider notification and history cleanup.
                // The view must never expose that previous account's series in this interval.
                claude.IdentityKey = "statistics-smoke-replacement";
                page.RefreshView();
                await LayoutAsync();
                AssertEmptyReport();
            }
            finally
            {
                claude.IdentityKey = originalIdentity;
                page.RefreshView();
            }
            await LayoutAsync();
            AssertSeries(claude, "session::0");

            await settings.ClearUsageStatisticsFromSettingsAsync();
            await LayoutAsync();
            AssertEmptyReport();
            if (controller.UsageStatisticsCount != 0 || controller.Settings.SaveUsageStatistics ||
                !Equals(accounts.SelectedValue, claude.Id) || !Equals(windows.SelectedValue, "session::0") ||
                ((TextBlock)page.FindName("StatisticsEmptyLabel")).Visibility != Visibility.Visible)
                throw new InvalidOperationException("Clearing records must leave account and current-quota choices usable and show an honest empty state.");

            controller.Settings.Accounts.Clear();
            controller.ViewModel.Accounts.Clear();
            await LayoutAsync();
            if (accounts.IsEnabled || windows.IsEnabled || accounts.Items.Count != 0 || windows.Items.Count != 0 || chart.Report is not null ||
                !((TextBlock)page.FindName("StatisticsEmptyLabel")).Text.Contains("계정", StringComparison.Ordinal))
                throw new InvalidOperationException("An empty account list must clear the chart and provide an account-specific empty state.");

            UsageStatisticsReport Report() => chart.Report ?? throw new InvalidOperationException("The statistics chart must receive a report.");

            void AssertSeries(AccountSettings account, string windowId)
            {
                var report = Report();
                if (report.SampleCount == 0 || report.LatestPercent is null || report.PeakPercent is null ||
                    report.Samples.Any(sample => sample.AccountId != account.Id || sample.Provider != account.Provider ||
                        sample.IdentityKey != account.IdentityKey || sample.WindowId != windowId))
                    throw new InvalidOperationException("Statistics selection must isolate account, identity and quota without mixing observations.");
            }

            void AssertEmptyReport()
            {
                var report = Report();
                if (report.SampleCount != 0 || report.LatestPercent is not null || report.PeakPercent is not null ||
                    report.ObservedIncreasePercent is not null ||
                    ((TextBlock)page.FindName("StatisticsLatestLabel")).Text != "—" ||
                    ((TextBlock)page.FindName("StatisticsPeakLabel")).Text != "—" ||
                    ((TextBlock)page.FindName("StatisticsIncreaseLabel")).Text != "—")
                    throw new InvalidOperationException("Missing statistics must display unavailable summaries rather than fabricated zero usage.");
            }

            async Task LayoutAsync() => await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
        }
        finally
        {
            reopened?.Close();
            settings.Close();
        }
    }

    private static void VerifyChartSelection(UsageStatisticsChart chart)
    {
        var report = chart.Report ?? throw new InvalidOperationException("Chart selection requires a statistics report.");
        chart.SelectNearestSampleAt(42);
        if (chart.SelectedSample != report.Samples[0])
            throw new InvalidOperationException("Selecting the start of a chart must select the first actual observation.");
        chart.SelectNearestSampleAt(chart.ActualWidth - 14);
        if (chart.SelectedSample != report.Samples[^1] || !chart.SelectedDescription.Contains('%'))
            throw new InvalidOperationException("Selecting the end of a chart must expose the actual last observation and its percentage.");
        var source = PresentationSource.FromVisual(chart) ?? throw new InvalidOperationException("The chart must be hosted for keyboard verification.");
        var peer = UIElementAutomationPeer.CreatePeerForElement(chart) ?? throw new InvalidOperationException("The statistics chart must provide an automation peer.");
        foreach (var (key, expected) in new[] { (Key.Home, 0), (Key.Right, 1), (Key.End, report.SampleCount - 1), (Key.Left, report.SampleCount - 2) })
        {
            var keyEvent = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent };
            chart.RaiseEvent(keyEvent);
            if (!keyEvent.Handled || chart.SelectedSampleIndex != expected || string.IsNullOrWhiteSpace(peer.GetItemStatus()))
                throw new InvalidOperationException("Chart keyboard navigation must select actual observations and update its accessible detail.");
        }
    }

    private static void VerifyChartRenderingAtScale(UsageStatisticsChart source)
    {
        // Render the same logical chart at common output DPI values without changing the user's
        // displays. Physical mixed-DPI monitor movement remains a separate manual validation.
        foreach (var scale in new[] { 1.25, 1.5, 2d })
        {
            var chart = new UsageStatisticsChart { Report = source.Report, Width = 440, Height = 230 };
            chart.Measure(new Size(440, 230));
            chart.Arrange(new Rect(0, 0, 440, 230));
            chart.SelectSample(0);
            chart.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(440 * scale), (int)Math.Ceiling(230 * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(chart);
            if (chart.ActualWidth != 440 || chart.ActualHeight != 230 || chart.SelectedSample != source.Report!.Samples[0])
                throw new InvalidOperationException("Chart rendering must preserve logical geometry and selected observations at common output scales.");
        }
    }
}
