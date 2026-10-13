using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyForecastAsync(MainWindow window, SettingsWindow settings, AccountViewModel account)
    {
        if (_controller is not { IsDemo: true }) throw new InvalidOperationException("Forecast UI verification requires the isolated demo.");
        var tabs = (TabControl)settings.FindName("SettingsTabs");
        var enabled = (CheckBox)settings.FindName("UsageForecastChoice");
        var show = (CheckBox)settings.FindName("ShowUsageForecastChoice");
        var statistics = (CheckBox)settings.FindName("UsageStatisticsChoice");
        var clearStatistics = (Button)settings.FindName("ClearUsageStatisticsButton");
        var statisticsCount = (TextBlock)settings.FindName("UsageStatisticsCountLabel");
        var previous = (_controller.Settings.UsageForecastEnabled, _controller.Settings.ShowUsageForecast,
            _controller.Settings.SaveUsageStatistics, Tab: tabs.SelectedIndex, window.Width, window.Height, window.Left, window.Top);
        SettingsWindow? reopened = null;
        try
        {
            if (enabled.IsChecked != false || show.IsChecked != true || show.IsEnabled)
                throw new InvalidOperationException("Forecasting must start disabled while retaining the default widget-display choice.");
            if (statistics.IsChecked != false || _controller.UsageStatisticsCount != 0 || clearStatistics.IsEnabled)
                throw new InvalidOperationException("New profiles must start with statistics collection disabled and no deletable history.");
            await RefreshUsageAsync();
            if (_controller.UsageStatisticsCount != 0)
                throw new InvalidOperationException("Fresh usage reads must not create history while statistics collection is disabled.");
            statistics.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            await LayoutAsync();
            if (!_controller.Settings.SaveUsageStatistics || enabled.IsChecked != false || show.IsChecked != true)
                throw new InvalidOperationException("Enabling statistics must leave both forecast preferences unchanged.");
            await RefreshUsageAsync();
            var savedCount = _controller.UsageStatisticsCount;
            if (savedCount <= 0 || !clearStatistics.IsEnabled || statisticsCount.Text != $"저장된 기록 {savedCount:N0}건")
                throw new InvalidOperationException("Statistics must collect normal fresh reads and update their count even with forecasting disabled.");
            statistics.SetCurrentValue(CheckBox.IsCheckedProperty, false);
            await RefreshUsageAsync();
            if (_controller.Settings.SaveUsageStatistics || _controller.UsageStatisticsCount != savedCount || enabled.IsChecked != false)
                throw new InvalidOperationException("Disabling statistics must stop new records while retaining existing history and forecast preferences.");
            window.Width = window.MinWidth;
            window.Left = 100;
            window.Top = 100;
            await LayoutAsync();
            var baselineHeight = window.ActualHeight;
            var fixedWidth = window.ActualWidth;
            var fixedLeft = window.Left;
            var fixedTop = window.Top;
            AssertLabels(window, "WidgetForecastLabel", false);
            tabs.SelectedIndex = 0;
            await LayoutAsync();
            AssertLabels(settings, "AccountForecastLabel", false);

            tabs.SelectedIndex = 1;
            enabled.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            await LayoutAsync();
            if (!_controller.Settings.UsageForecastEnabled || !show.IsEnabled || window.ActualHeight <= baselineHeight)
                throw new InvalidOperationException("Enabling forecasts must show their rows and fit the widget height.");
            if (_controller.Settings.SaveUsageStatistics || _controller.UsageStatisticsCount != savedCount)
                throw new InvalidOperationException("Enabling forecasts must not enable statistics or alter existing history.");
            AssertGeometry();
            AssertLabels(window, "WidgetForecastLabel", true);
            var enabledHeight = window.ActualHeight;
            show.SetCurrentValue(CheckBox.IsCheckedProperty, false);
            await LayoutAsync();
            if (!_controller.Settings.UsageForecastEnabled || _controller.Settings.ShowUsageForecast ||
                Math.Abs(window.ActualHeight - baselineHeight) > 1)
                throw new InvalidOperationException("Hiding only widget forecasts must keep the feature enabled and remove all widget row space.");
            AssertGeometry();
            AssertLabels(window, "WidgetForecastLabel", false);
            tabs.SelectedIndex = 0;
            await LayoutAsync();
            AssertLabels(settings, "AccountForecastLabel", true);

            tabs.SelectedIndex = 1;
            enabled.SetCurrentValue(CheckBox.IsCheckedProperty, false);
            await LayoutAsync();
            if (show.IsEnabled || show.IsChecked != false || _controller.Settings.ShowUsageForecast)
                throw new InvalidOperationException("Disabling forecasts must retain the independent widget-display selection.");
            if (statistics.IsChecked != false || _controller.UsageStatisticsCount != savedCount)
                throw new InvalidOperationException("Turning forecasting off must not change statistics collection or delete saved history.");
            tabs.SelectedIndex = 0;
            await LayoutAsync();
            AssertLabels(settings, "AccountForecastLabel", false);
            var disabledQueries = 0;
            account.ConfigureForecast(false, true, _ => { disabledQueries++; return new(UsageForecastState.Collecting); });
            account.Tick();
            if (disabledQueries != 0)
                throw new InvalidOperationException("Disabled forecast views must not calculate or request historical data.");

            tabs.SelectedIndex = 1;
            enabled.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            show.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            await LayoutAsync();
            if (Math.Abs(window.ActualHeight - enabledHeight) > 1 || !window.IsVisible || !settings.IsVisible || settings.Owner is not null)
                throw new InvalidOperationException("Restoring forecast rows must preserve the independent settings window and previous fitted height.");
            AssertGeometry();

            statistics.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            await RefreshUsageAsync();
            if (!_controller.Settings.SaveUsageStatistics || enabled.IsChecked != true || show.IsChecked != true ||
                _controller.UsageStatisticsCount <= savedCount)
                throw new InvalidOperationException("Statistics must resume collecting without changing active forecast preferences.");

            tabs.SelectedIndex = 0;
            var meter = account.CompactWindows[0];
            var usedValue = meter.Value;
            UsageForecast result = new(UsageForecastState.Collecting);
            account.ConfigureForecast(true, true, _ => result, saveUsageStatistics: true);
            foreach (var (state, label) in new[]
            {
                (UsageForecastState.Collecting, "추이 수집 중"),
                (UsageForecastState.NoGrowth, "사용 증가 없음"),
                (UsageForecastState.ResetFirst, "초기화가 먼저"),
                (UsageForecastState.Exhausted, "한도 도달"),
                (UsageForecastState.Stale, "새 조회 필요"),
                (UsageForecastState.Unavailable, "예측 —")
            })
            {
                result = new(state, ObservedAt: DateTimeOffset.UtcNow, SampleCount: 3, SampleSpan: TimeSpan.FromMinutes(20));
                account.Tick();
                await LayoutAsync();
                AssertForecastText(label);
            }
            result = new(UsageForecastState.Collecting);
            account.ConfigureForecast(true, true, _ => result, saveUsageStatistics: false);
            await LayoutAsync();
            AssertForecastText("통계 부족");
            if (!meter.ForecastDetail.Contains("통계 저장을 켜고", StringComparison.Ordinal))
                throw new InvalidOperationException("Insufficient statistics must not claim collection is running when collection is disabled.");
            account.ConfigureForecast(true, true, _ => result, saveUsageStatistics: true);
            result = new(UsageForecastState.Estimated, DateTimeOffset.UtcNow.AddHours(1.5), 15,
                DateTimeOffset.UtcNow, 4, TimeSpan.FromMinutes(30));
            account.Tick();
            await LayoutAsync();
            AssertForecastText("소진 예상 1시간 30분 후");
            if (!meter.ForecastDetail.Contains("관측 4회", StringComparison.Ordinal) ||
                !meter.ForecastDetail.Contains("30분", StringComparison.Ordinal) ||
                !meter.ForecastDetail.Contains("가정", StringComparison.Ordinal) ||
                !meter.ForecastDetail.Contains(meter.ResetDetail, StringComparison.Ordinal) || meter.Value != usedValue)
                throw new InvalidOperationException("Forecast details must explain the observed interval, current-rate assumption and reset without changing usage.");

            // The prediction remains fixed while normal ticks update its remaining time.
            result = result with { ExhaustsAt = DateTimeOffset.UtcNow.AddMilliseconds(1100) };
            account.Tick();
            await LayoutAsync();
            AssertForecastText("소진 예상 1분 이내");
            await Task.Delay(1300);
            account.Tick();
            await LayoutAsync();
            AssertForecastText("예상 시각 지남");

            reopened = new SettingsWindow(_controller);
            reopened.Show();
            await Dispatcher.InvokeAsync(reopened.UpdateLayout, DispatcherPriority.ApplicationIdle);
            if (((CheckBox)reopened.FindName("UsageForecastChoice")).IsChecked != true ||
                ((CheckBox)reopened.FindName("ShowUsageForecastChoice")).IsChecked != true ||
                ((CheckBox)reopened.FindName("UsageStatisticsChoice")).IsChecked != true)
                throw new InvalidOperationException("Reopening settings must retain statistics and both forecast preferences independently.");
            // Use the same operation as the confirmed delete button, without opening a modal dialog in smoke mode.
            await settings.ClearUsageStatisticsFromSettingsAsync();
            await LayoutAsync();
            if (_controller.UsageStatisticsCount != 0 || clearStatistics.IsEnabled || statisticsCount.Text != "저장된 기록 0건" ||
                statistics.IsChecked != true || enabled.IsChecked != true || show.IsChecked != true)
                throw new InvalidOperationException("Explicit deletion must clear stored statistics and refresh its UI without changing any preference.");

            void AssertGeometry()
            {
                if (Math.Abs(window.ActualWidth - fixedWidth) > 1 || Math.Abs(window.Left - fixedLeft) > 1 || Math.Abs(window.Top - fixedTop) > 1)
                    throw new InvalidOperationException("Changing forecast visibility must preserve the widget's 200 DIP width and screen position.");
            }
            void AssertForecastText(string expected)
            {
                var widgetText = VisualChildren<TextBlock>(window).Single(text => text.Name == "WidgetForecastLabel" && ReferenceEquals(text.DataContext, meter));
                var accountText = VisualChildren<TextBlock>(settings).Single(text => text.Name == "AccountForecastLabel" && ReferenceEquals(text.DataContext, meter));
                if (widgetText.Text != expected || accountText.Text != expected || widgetText.Visibility != Visibility.Visible ||
                    accountText.Visibility != Visibility.Visible || widgetText.ActualWidth + 1 < MeasureText(widgetText, expected))
                    throw new InvalidOperationException($"Forecast '{expected}' must update both actual labels and fit the minimum widget width.");
            }
        }
        finally
        {
            reopened?.Close();
            await _controller.SetUsageStatisticsEnabledAsync(previous.SaveUsageStatistics);
            await _controller.SetForecastPreferencesAsync(previous.UsageForecastEnabled, previous.ShowUsageForecast);
            foreach (var vm in _controller.ViewModel.Accounts)
                vm.ConfigureForecast(previous.UsageForecastEnabled, previous.ShowUsageForecast, id => _controller.GetForecast(vm.Settings, id), previous.SaveUsageStatistics);
            tabs.SelectedIndex = previous.Tab;
            window.Width = previous.Width;
            window.Height = previous.Height;
            window.Left = previous.Left;
            window.Top = previous.Top;
            await LayoutAsync();
        }

        async Task LayoutAsync()
        {
            await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
            await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        }
        async Task RefreshUsageAsync()
        {
            await _controller.RefreshAllAsync();
            await LayoutAsync();
        }
        static void AssertLabels(DependencyObject view, string name, bool visible)
        {
            var labels = VisualChildren<TextBlock>(view).Where(text => text.Name == name).ToArray();
            if (labels.Length == 0 || labels.Any(text => text.Visibility != (visible ? Visibility.Visible : Visibility.Collapsed)))
                throw new InvalidOperationException($"Forecast label visibility is inconsistent on {name}.");
        }
    }
}
