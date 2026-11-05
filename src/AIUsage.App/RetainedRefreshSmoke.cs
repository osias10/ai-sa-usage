using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyRetainedRefreshAsync(MainWindow widget, SettingsWindow settings, AccountViewModel account)
    {
        if (_controller is not { IsDemo: true } controller)
            throw new InvalidOperationException("Retained refresh verification requires the isolated demo.");
        var original = controller.GetStatuses().Single(status => status.AccountId == account.Id);
        var originalSnapshot = original.Snapshot ?? throw new InvalidOperationException("Retained refresh verification requires cached usage.");
        var originalSelections = account.Settings.WidgetWindows;
        var originalCredits = account.ShowCreditsInWidget;
        var originalRemaining = account.ShowRemaining;
        var originalLastChecked = account.ShowLastChecked;
        var originalWidth = widget.Width;
        var originalLeft = widget.Left;
        var originalTop = widget.Top;
        var summary = new TraySummary(controller.ViewModel);
        var surfaces = new (DependencyObject Surface, string Name)[]
        {
            (widget, "widget"), (settings, "settings"), (summary, "tray summary")
        };
        try
        {
            account.Settings.WidgetWindows = [new("session::0", "현재 세션"), new("weekly_all::0", "주간 전체")];
            account.ShowCreditsInWidget = true;
            account.ShowRemaining = false;
            account.ShowLastChecked = true;
            await FlushAsync();
            widget.Width = widget.MinWidth;
            widget.Left = 100;
            widget.Top = 100;
            var now = DateTimeOffset.UtcNow;
            var cachedSnapshot = originalSnapshot with
            {
                State = UsageState.Ready, Message = null, FetchedAt = now.AddMinutes(-7),
                Windows = [new("session::0", "현재 세션", 37, now.AddHours(2)),
                    new("weekly_all::0", "주간 전체", 68, now.AddDays(3))],
                Credits = [new("retained-credit", "크레딧", "credits", Balance: 10)]
            };
            var ready = original with
            {
                State = UsageState.Ready, Message = null, IsRefreshing = false,
                IsRetryScheduled = false, IsPartialResponse = false, NextAttemptAt = null,
                Snapshot = cachedSnapshot, RetainedSnapshot = null
            };
            account.Apply(ready);
            widget.FitContent();
            await FlushAsync();
            var baselineHeight = widget.ActualHeight;
            var baselineWidth = widget.ActualWidth;
            var baselineLeft = widget.Left;
            var baselineTop = widget.Top;
            var cachedRows = account.Windows.ToArray();
            var cachedCompactRows = account.CompactWindows;
            var cachedCredits = account.Credits;
            var cachedTime = account.UpdatedDetail;
            var cachedCompactTime = account.CompactUpdatedLabel;
            var controls = surfaces.Select(surface =>
            {
                var source = ReferenceEquals(surface.Surface, settings) ? nameof(AccountViewModel.Windows) : nameof(AccountViewModel.CompactWindows);
                var meters = VisualChildren<ItemsControl>(surface.Surface).Single(control =>
                    ReferenceEquals(control.DataContext, account) &&
                    BindingOperations.GetBinding(control, ItemsControl.ItemsSourceProperty)?.Path?.Path == source);
                return (surface.Surface, surface.Name, Meters: meters, Source: meters.ItemsSource,
                    Numbers: PercentageLabels(surface.Surface), Gauges: Gauges(surface.Surface));
            }).ToArray();
            AssertDisplay(37, 68, 10, cachedSnapshot.FetchedAt, false, false, false);

            // Starting a request or receiving progress must leave every existing meter in place.
            account.Apply(ready with { IsRefreshing = true });
            await FlushAsync();
            AssertRetainedRows(false, true, false);
            account.Report(new("사용량을 확인하고 있습니다."));
            account.Tick();
            await FlushAsync();
            AssertRetainedRows(false, true, false);

            const string partialMessage = "일부 사용량을 확인하지 못했습니다.";
            var partial = ready with
            {
                State = UsageState.Unavailable, Message = partialMessage,
                IsRetryScheduled = true, IsPartialResponse = true, NextAttemptAt = now.AddSeconds(45),
                Snapshot = cachedSnapshot with
                {
                    State = UsageState.Unavailable, Message = partialMessage, FetchedAt = now.AddMinutes(-1),
                    Windows = [new("weekly_all::0", "주간 전체", 74, now.AddDays(3))],
                    Credits = [new("retained-credit", "크레딧", "credits", Balance: 9)]
                },
                RetainedSnapshot = cachedSnapshot
            };
            account.Apply(partial);
            await FlushAsync();
            AssertRetainedRows(true, false, true);
            if (!account.Notice.Contains("재시도", StringComparison.Ordinal))
                throw new InvalidOperationException("A retained partial response must still explain its scheduled retry.");

            account.Apply(partial with { IsRefreshing = true });
            await FlushAsync();
            AssertRetainedRows(true, true, false);
            account.Report(new("사용량을 다시 확인하고 있습니다."));
            account.Tick();
            await FlushAsync();
            AssertRetainedRows(true, true, false);

            // A retry with no response must retain the same successful sample and its timestamp.
            account.Apply(partial with
            {
                State = UsageState.Offline, Message = "사용량 연결에 실패했습니다.",
                IsPartialResponse = false, NextAttemptAt = now.AddMinutes(1)
            });
            await FlushAsync();
            AssertRetainedRows(true, false, true);

            var recoveredSnapshot = cachedSnapshot with
            {
                FetchedAt = now,
                Windows = [new("session::0", "현재 세션", 41, now.AddHours(2)),
                    new("weekly_all::0", "주간 전체", 69, now.AddDays(3))],
                Credits = [new("retained-credit", "크레딧", "credits", Balance: 8)]
            };
            account.Apply(ready with { Snapshot = recoveredSnapshot });
            await FlushAsync();
            AssertDisplay(41, 69, 8, recoveredSnapshot.FetchedAt, false, false, false);
            AssertLayout(false);
            if (ReferenceEquals(account.CompactWindows, cachedCompactRows) || ReferenceEquals(account.Credits, cachedCredits) ||
                account.HasNotice || account.UpdatedDetail == cachedTime)
                throw new InvalidOperationException("A successful refresh must replace retained values and time together and clear the notice.");

            void AssertRetainedRows(bool retained, bool busy, bool issue)
            {
                AssertDisplay(37, 68, 10, cachedSnapshot.FetchedAt, retained, busy, issue);
                AssertLayout(retained);
                if (!ReferenceEquals(account.CompactWindows, cachedCompactRows) || !ReferenceEquals(account.Credits, cachedCredits) ||
                    !account.Windows.SequenceEqual(cachedRows) || account.UpdatedDetail != cachedTime ||
                    account.CompactUpdatedLabel != (retained ? "이전 " : "") + cachedCompactTime)
                    throw new InvalidOperationException("Refreshing must retain the same usage rows, credit rows, and successful check time.");
                foreach (var control in controls)
                    if (!ReferenceEquals(control.Meters.ItemsSource, control.Source) ||
                        !PercentageLabels(control.Surface).SequenceEqual(control.Numbers) ||
                        !Gauges(control.Surface).SequenceEqual(control.Gauges))
                        throw new InvalidOperationException($"The {control.Name} must keep its existing ItemsSource, numbers, and gauges during refresh.");
            }

            void AssertLayout(bool staleCredit)
            {
                // The existing stale-credit label adds one 12-DIP row; retries add no further space.
                var expectedHeight = baselineHeight + (staleCredit ? 12 : 0);
                if (Math.Abs(widget.ActualHeight - expectedHeight) > 1 || Math.Abs(widget.ActualWidth - baselineWidth) > 1 ||
                    Math.Abs(widget.Left - baselineLeft) > 1 || Math.Abs(widget.Top - baselineTop) > 1)
                    throw new InvalidOperationException("Retained refresh transitions must preserve widget position, width, and expected content height.");
            }
        }
        finally
        {
            account.Settings.WidgetWindows = originalSelections;
            account.ShowCreditsInWidget = originalCredits;
            account.ShowRemaining = originalRemaining;
            account.ShowLastChecked = originalLastChecked;
            await FlushAsync();
            account.Apply(original);
            widget.Width = originalWidth;
            widget.Left = originalLeft;
            widget.Top = originalTop;
            widget.FitContent();
            await FlushAsync();
            summary.DataContext = null;
        }

        TextBlock[] PercentageLabels(DependencyObject surface) => VisualChildren<TextBlock>(surface).Where(text =>
            text.DataContext is UsageWindowViewModel row && account.Windows.Contains(row) &&
            BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == nameof(UsageWindowViewModel.Percent)).ToArray();

        ProgressBar[] Gauges(DependencyObject surface) => VisualChildren<ProgressBar>(surface).Where(gauge =>
            gauge.DataContext is UsageWindowViewModel row && account.Windows.Contains(row)).ToArray();

        void AssertDisplay(double session, double weekly, decimal credit, DateTimeOffset fetchedAt, bool retained, bool busy, bool issue)
        {
            var expectedValues = new[] { session, weekly };
            var expectedPercentages = expectedValues.Select(value => $"{value:0.#}%").ToArray();
            if (!account.CompactWindows.Select(row => row.Value).SequenceEqual(expectedValues) ||
                account.CompactWindows.Any(row => !row.HasValue) || account.Credits.Single().Source?.Balance != credit ||
                account.CreditsAreStale != retained || account.IsRefreshing != busy || account.HasUsageIssue != issue ||
                account.Notice.Contains("이전 값 표시", StringComparison.Ordinal) != retained ||
                account.UpdatedDetail != fetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ||
                !account.LastCheckedDetail.StartsWith("마지막 성공 확인:", StringComparison.Ordinal) ||
                account.CompactUpdatedLabel.Contains("일부", StringComparison.Ordinal))
                throw new InvalidOperationException("Retained refresh values, credits, check time, and status must describe the displayed complete sample.");
            foreach (var (surface, name) in surfaces)
            {
                if (!PercentageLabels(surface).Select(text => text.Text).SequenceEqual(expectedPercentages))
                    throw new InvalidOperationException($"The {name} must keep complete percentages visible while refreshing.");
                if (!ReferenceEquals(surface, summary) &&
                    (!Gauges(surface).Select(gauge => gauge.Value).SequenceEqual(expectedValues) ||
                        Gauges(surface).Any(gauge => gauge.Visibility != Visibility.Visible)))
                    throw new InvalidOperationException($"The {name} must keep complete usage gauges visible while refreshing.");
                var icon = VisualChildren<TextBlock>(surface).Single(text =>
                    text.Name == "AccountStatusIcon" && ReferenceEquals(text.DataContext, account));
                if (icon.Visibility != (busy || issue ? Visibility.Visible : Visibility.Collapsed) ||
                    icon.Text != (busy ? "\uE72C" : "\uE7BA") || icon.ToolTip as string != account.Notice)
                    throw new InvalidOperationException($"The {name} must show refresh and failure indicators beside retained values.");
            }
            foreach (var (surface, valueName, staleName) in new (DependencyObject, string, string)[]
            {
                (widget, "WidgetCreditValue", "WidgetCreditStaleLabel"),
                (settings, "AccountCreditValue", "AccountCreditStaleLabel")
            })
            {
                var amount = VisualChildren<TextBlock>(surface).Single(text =>
                    text.Name == valueName && ReferenceEquals(text.DataContext, account.Credits[0]));
                var stale = VisualChildren<TextBlock>(surface).Single(text =>
                    text.Name == staleName && ReferenceEquals(text.DataContext, account));
                if (amount.Text != $"{credit:0} credits" || stale.Visibility != (retained ? Visibility.Visible : Visibility.Collapsed))
                    throw new InvalidOperationException("Retained credits must keep their previous amount and visibly identify its age.");
            }
            var checkedLabel = VisualChildren<TextBlock>(widget).Single(text =>
                text.Name == "LastCheckedLabel" && ReferenceEquals(text.DataContext, account));
            if (checkedLabel.Text != account.CompactUpdatedLabel || checkedLabel.ToolTip as string != account.LastCheckedDetail)
                throw new InvalidOperationException("The retained widget check time must describe the displayed sample.");
        }

        async Task FlushAsync()
        {
            await Dispatcher.InvokeAsync(() =>
            {
                widget.UpdateLayout();
                settings.UpdateLayout();
                summary.Measure(new Size(300, double.PositiveInfinity));
                summary.Arrange(new Rect(summary.DesiredSize));
                summary.UpdateLayout();
            }, DispatcherPriority.ApplicationIdle);
        }
    }
}
