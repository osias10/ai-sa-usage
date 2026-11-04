using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyPartialUsageAsync(MainWindow widget, SettingsWindow settings, AccountViewModel account)
    {
        if (_controller is not { IsDemo: true } controller)
            throw new InvalidOperationException("Partial usage verification requires the isolated demo.");
        var original = controller.GetStatuses().Single(status => status.AccountId == account.Id);
        var originalSnapshot = original.Snapshot ?? throw new InvalidOperationException("Partial usage verification requires cached usage.");
        var originalSelections = account.Settings.WidgetWindows;
        var originalCredits = account.ShowCreditsInWidget;
        var originalWidth = widget.Width;
        var originalLeft = widget.Left;
        var originalTop = widget.Top;
        var summary = new TraySummary(controller.ViewModel);
        try
        {
            account.Settings.WidgetWindows = [new("session::0", "현재 세션"), new("weekly_all::0", "주간 전체")];
            account.ShowCreditsInWidget = true;
            await FlushAsync();
            widget.Width = widget.MinWidth;
            widget.Left = 100;
            widget.Top = 100;
            var ready = original with
            {
                State = UsageState.Ready, Message = null, IsRefreshing = false,
                IsRetryScheduled = false, IsPartialResponse = false, NextAttemptAt = null,
                Snapshot = originalSnapshot with
                {
                    State = UsageState.Ready, Message = null,
                    Credits = [new("partial-credit", "크레딧", "credits", Balance: 10)]
                }
            };
            account.Apply(ready);
            widget.FitContent();
            await FlushAsync();
            var baselineHeight = widget.ActualHeight;
            var baselineWidth = widget.ActualWidth;
            var baselineLeft = widget.Left;
            var baselineTop = widget.Top;
            const string message = "일부 사용량을 확인하지 못했습니다.";
            var partial = ready with
            {
                State = UsageState.Unavailable, Message = message,
                IsPartialResponse = true, IsRetryScheduled = true,
                NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(30),
                Snapshot = ready.Snapshot! with
                {
                    State = UsageState.Unavailable, Message = message,
                    FetchedAt = DateTimeOffset.UtcNow,
                    Windows = [new("weekly_all::0", "주간 전체", 74, DateTimeOffset.UtcNow.AddDays(3))]
                }
            };
            account.Apply(partial);
            await FlushAsync();
            AssertMissingSession();
            AssertIndicators(issue: true, busy: false);
            AssertPartialTime();
            AssertPosition();
            if (account.CreditsAreStale || Math.Abs(widget.ActualHeight - baselineHeight) > 1)
                throw new InvalidOperationException("A partial quota response must retain fresh credits without adding stale rows or empty space.");

            // A present window with an absent percentage has the same unknown presentation.
            account.Apply(partial with
            {
                Snapshot = partial.Snapshot! with
                {
                    Windows = [new("session::0", "현재 세션", null, null), partial.Snapshot.Windows.Single()]
                }
            });
            await FlushAsync();
            AssertMissingSession();
            AssertIndicators(issue: true, busy: false);
            AssertPosition();

            // Retrying uses the refresh glyph while retaining the incomplete values and timestamp.
            account.Apply(partial with { IsRefreshing = true });
            account.Tick();
            await FlushAsync();
            AssertMissingSession();
            AssertIndicators(issue: false, busy: true);
            AssertPartialTime();
            if (account.CreditsAreStale)
                throw new InvalidOperationException("Starting a retry must not mark current partial-response credits as cached failures.");

            // Credits alone must not hide a missing subscription quota or replace it with zero.
            account.Apply(partial with { Snapshot = partial.Snapshot! with { Windows = [] } });
            await FlushAsync();
            AssertIndicators(issue: true, busy: false);
            AssertPartialTime();
            AssertPosition();
            if (account.HasWindows || !account.HasCredits || account.CreditsAreStale ||
                account.CompactWindows.Count != 2 || account.CompactWindows.Any(window => window.HasValue || window.Percent != "—") ||
                VisualChildren<TextBlock>(widget).Single(text => text.Name == "WidgetCreditValue" &&
                    ReferenceEquals(text.DataContext, account.Credits[0])).Text != "10 credits")
                throw new InvalidOperationException("Credit-only partial data must show credits, unknown quota selections, and a retry notice together.");

            // A failed retry keeps the partial values, but now identifies retained credits as old.
            foreach (var state in new[] { UsageState.Offline, UsageState.Unavailable })
            {
                account.Apply(partial with { State = state, IsPartialResponse = false, Message = "사용량을 확인하지 못했습니다." });
                await FlushAsync();
                AssertMissingSession();
                AssertIndicators(issue: true, busy: false);
                AssertPosition();
                if (!account.CreditsAreStale || !account.CompactUpdatedLabel.StartsWith("이전 일부 확인 ", StringComparison.Ordinal) ||
                    !account.LastCheckedDetail.StartsWith("마지막 부분 확인:", StringComparison.Ordinal) ||
                    account.LastCheckedDetail.Contains("마지막 성공 확인", StringComparison.Ordinal))
                    throw new InvalidOperationException("Failed retries must not relabel retained partial data as a successful fetch.");
            }

            account.Apply(ready);
            await FlushAsync();
            AssertIndicators(issue: false, busy: false);
            AssertPosition();
            if (account.HasNotice || account.CreditsAreStale || account.CompactWindows.Any(window => !window.HasValue) ||
                !account.LastCheckedDetail.StartsWith("마지막 성공 확인:", StringComparison.Ordinal) ||
                !account.CompactUpdatedLabel.StartsWith("확인 ", StringComparison.Ordinal) ||
                Math.Abs(widget.ActualHeight - baselineHeight) > 1)
                throw new InvalidOperationException("A complete response must clear partial warnings, restore normal timestamps, and fit the original content height.");

            void AssertPosition()
            {
                if (Math.Abs(widget.ActualWidth - baselineWidth) > 1 || Math.Abs(widget.Left - baselineLeft) > 1 || Math.Abs(widget.Top - baselineTop) > 1)
                    throw new InvalidOperationException("Partial usage transitions must retain widget width and position.");
            }
        }
        finally
        {
            account.Settings.WidgetWindows = originalSelections;
            account.ShowCreditsInWidget = originalCredits;
            await FlushAsync();
            account.Apply(original);
            widget.Width = originalWidth;
            widget.Left = originalLeft;
            widget.Top = originalTop;
            widget.FitContent();
            await FlushAsync();
            summary.DataContext = null;
        }

        void AssertMissingSession()
        {
            if (account.CompactWindows.Count != 2 || account.CompactWindows[0].Id != "session::0" ||
                account.CompactWindows[0].HasValue || account.CompactWindows[0].Percent != "—" ||
                account.CompactWindows[1].Id != "weekly_all::0" || account.CompactWindows[1].Percent != "74%")
                throw new InvalidOperationException("Partial quota data must keep the selected missing session unknown and show the newly fetched weekly value.");
            var rows = VisualChildren<TextBlock>(widget).Where(text => text.DataContext is UsageWindowViewModel window &&
                account.CompactWindows.Contains(window) &&
                BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == nameof(UsageWindowViewModel.Percent));
            if (!rows.Select(text => text.Text).SequenceEqual(new[] { "—", "74%" }))
                throw new InvalidOperationException("The widget must render the missing session and current weekly value without substituting a stale or zero percentage.");
        }

        void AssertPartialTime()
        {
            var label = VisualChildren<TextBlock>(widget).Single(text => text.Name == "LastCheckedLabel" && ReferenceEquals(text.DataContext, account));
            if (!account.UpdatedLabel.StartsWith("일부 확인 · ", StringComparison.Ordinal) ||
                !account.CompactUpdatedLabel.StartsWith("일부 확인 ", StringComparison.Ordinal) ||
                !account.LastCheckedDetail.StartsWith("마지막 부분 확인:", StringComparison.Ordinal) ||
                account.LastCheckedDetail.Contains("마지막 성공 확인", StringComparison.Ordinal) ||
                label.Text != account.CompactUpdatedLabel || label.ToolTip is not string detail || detail != account.LastCheckedDetail)
                throw new InvalidOperationException("Partial response timestamps must be identified as partial checks in the widget and its tooltip.");
        }

        void AssertIndicators(bool issue, bool busy)
        {
            if (account.HasUsageIssue != issue || account.IsRefreshing != busy || account.Credits.Count != 1 ||
                issue && (!account.HasNotice || !account.Notice.Contains("재시도", StringComparison.Ordinal)))
                throw new InvalidOperationException("Partial response status must expose its failure and automatic retry while preserving valid credits.");
            foreach (var (surface, name) in new (DependencyObject, string)[] { (widget, "widget"), (settings, "settings"), (summary, "tray") })
            {
                var icon = VisualChildren<TextBlock>(surface).Single(text => text.Name == "AccountStatusIcon" && ReferenceEquals(text.DataContext, account));
                if (icon.Visibility != (issue || busy ? Visibility.Visible : Visibility.Collapsed) ||
                    icon.Text != (busy ? "\uE72C" : "\uE7BA") || icon.ToolTip as string != account.Notice)
                    throw new InvalidOperationException($"The {name} partial response indicator must follow retry and recovery state.");
            }
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
