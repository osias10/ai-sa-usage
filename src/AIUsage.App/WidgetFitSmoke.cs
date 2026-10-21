using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyWidgetFitAsync(MainWindow widget)
    {
        if (_controller is not { IsDemo: true }) throw new InvalidOperationException("Widget-fit verification requires the isolated demo.");
        var controller = _controller;
        var claude = controller.ViewModel.Accounts.First(account => account.Settings.Provider == ProviderKind.Claude);
        var fable = claude.WidgetWindowOptions.Single(option => option.Id == "weekly_scoped:Fable:0");
        var originalFable = fable.IsSelected;
        var originalPins = controller.ViewModel.PinnedAccounts.ToArray();
        var originalSettings = (controller.Settings.ShowLastChecked, controller.Settings.UsageForecastEnabled,
            controller.Settings.ShowUsageForecast, controller.Settings.WidgetPlacement);
        var originalLegacy = new Dictionary<string, WindowPlacement>(controller.Settings.Placements);
        try
        {
            // Fit immediately after changing selection, before the collection's deferred
            // layout has run. The third Claude row must not be omitted from measurement.
            fable.IsSelected = true;
            if (claude.CompactWindows.Count != 3) throw new InvalidOperationException("The fit fixture requires session, weekly and Fable rows.");
            widget.FitContent();
            await FlushAsync();
            AssertNoScroll((ScrollViewer)widget.FindName("WidgetScroll"), "three Claude quotas / three accounts");

            var codexStatus = controller.GetStatuses().First(status => status.Snapshot?.Provider == ProviderKind.Codex);
            var fourthSettings = new AccountSettings { Id = "widget-fit-fourth", Provider = ProviderKind.Codex, Alias = "추가 계정" };
            var fourth = new AccountViewModel(fourthSettings, _ => Task.CompletedTask, _ => Task.CompletedTask,
                _ => Task.CompletedTask, _ => { }, (_, _) => { }, _ => { }, error => throw error);
            fourth.Apply(codexStatus with { AccountId = fourth.Id, Snapshot = codexStatus.Snapshot! with { AccountId = fourth.Id } });
            controller.ViewModel.PinnedAccounts.Add(fourth);

            foreach (var showDetails in new[] { true, false })
            {
                await SetDetailsAsync(showDetails);
                fourth.ShowLastChecked = showDetails;
                fourth.ConfigureForecast(showDetails, true, _ => new(UsageForecastState.Collecting));
                widget.FitContent();
                await FlushAsync();
                AssertNoScroll((ScrollViewer)widget.FindName("WidgetScroll"), $"four accounts, details={showDetails}");

                // Apply real WPF DPI rounding to detached copies of the widget visual.
                // No HWND, monitor configuration, pointer or system scale is changed.
                foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                {
                    var copy = new MainWindow(controller) { AllowClose = true };
                    var frame = (FrameworkElement)copy.Content;
                    frame.Resources = copy.Resources;
                    frame.DataContext = controller.ViewModel;
                    frame.UseLayoutRounding = true;
                    frame.SetValue(TextElement.FontFamilyProperty, copy.FontFamily);
                    copy.Content = null;
                    copy.Close();
                    VisualTreeHelper.SetRootDpi(frame, new DpiScale(scale, scale));
                    foreach (var width in new[] { 200d, 240d, 320d })
                    {
                        var height = AIUsage.App.MainWindow.MeasureWidgetHeight(frame, width, widget.MinHeight);
                        frame.Arrange(new Rect(0, 0, width, height));
                        frame.UpdateLayout();
                        var scroll = VisualChildren<ScrollViewer>(frame).First();
                        AssertNoScroll(scroll, $"width={width}, DPI={scale}, details={showDetails}");
                        // The fix must not disable scrolling for actual height constraints.
                        frame.Measure(new Size(width, 70));
                        frame.Arrange(new Rect(0, 0, width, 70));
                        frame.UpdateLayout();
                        if (scroll.ScrollableHeight <= 0 || scroll.ComputedVerticalScrollBarVisibility != Visibility.Visible)
                            throw new InvalidOperationException("Height-limited widget content must retain a usable scrollbar.");
                    }
                }
            }
        }
        finally
        {
            fable.IsSelected = originalFable;
            controller.ViewModel.PinnedAccounts.Clear();
            foreach (var account in originalPins) controller.ViewModel.PinnedAccounts.Add(account);
            await controller.SetForecastPreferencesAsync(originalSettings.UsageForecastEnabled, originalSettings.ShowUsageForecast);
            await SetLastCheckedAsync(originalSettings.ShowLastChecked);
            controller.Settings.WidgetPlacement = originalSettings.WidgetPlacement;
            controller.Settings.Placements = originalLegacy;
            widget.ApplyMode(fitHeight: true);
            await FlushAsync();
        }

        async Task FlushAsync() => await Dispatcher.InvokeAsync(widget.UpdateLayout, DispatcherPriority.ApplicationIdle);
        async Task SetDetailsAsync(bool show)
        {
            await controller.SetForecastPreferencesAsync(show, true);
            await SetLastCheckedAsync(show);
        }
        Task SetLastCheckedAsync(bool show)
        {
            var settings = controller.Settings;
            return controller.SetPreferencesAsync(settings.AlwaysOnTop, settings.PositionLocked, settings.StartWithWindows,
                settings.Theme, settings.ShowRemaining, settings.TrayAccountId, show);
        }
        static void AssertNoScroll(ScrollViewer scroll, string fixture)
        {
            if (scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible || scroll.ScrollableHeight > 0.001)
                throw new InvalidOperationException($"Fitted widget must not scroll ({fixture}): extent={scroll.ExtentHeight}, viewport={scroll.ViewportHeight}, overflow={scroll.ScrollableHeight}.");
        }
    }
}
