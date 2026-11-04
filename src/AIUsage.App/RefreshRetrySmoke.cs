using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyRefreshRetryAsync(MainWindow widget, SettingsWindow settings, AccountViewModel account)
    {
        if (_controller is not { IsDemo: true } controller)
            throw new InvalidOperationException("Retry verification requires the isolated demo.");
        var original = controller.GetStatuses().Single(status => status.AccountId == account.Id);
        var originalSnapshot = original.Snapshot ?? throw new InvalidOperationException("Retry verification requires cached usage.");
        var originalAutoRefresh = account.Settings.AutoRefresh;
        var summary = new TraySummary(controller.ViewModel);
        var notifications = new HashSet<string>(StringComparer.Ordinal);
        PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName is { } name) notifications.Add(name);
        };
        account.PropertyChanged += changed;
        try
        {
            // A failed manual refresh also retries when periodic polling is disabled.
            account.Settings.AutoRefresh = false;
            const string failure = "사용량 연결에 실패했습니다.";
            var failed = original with
            {
                State = UsageState.Offline,
                Message = failure,
                IsRefreshing = false,
                IsRetryScheduled = false,
                NextAttemptAt = null
            };
            account.Apply(failed);
            await FlushAsync();
            var oldHeight = widget.ActualHeight;
            var oldWidth = widget.ActualWidth;
            var oldLeft = widget.Left;
            var oldTop = widget.Top;

            var retry = failed with
            {
                IsRetryScheduled = true,
                NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(45)
            };
            account.Apply(retry);
            await FlushAsync();
            AssertNotice(failure, "초 후 재시도", true, false);
            if (account.Windows.Count != originalSnapshot.Windows.Count ||
                !account.Windows.Select(window => window.Source).SequenceEqual(originalSnapshot.Windows))
                throw new InvalidOperationException("Retry presentation must retain the last successful usage rows.");
            if (Math.Abs(widget.ActualHeight - oldHeight) > 1 || Math.Abs(widget.ActualWidth - oldWidth) > 1 ||
                Math.Abs(widget.Left - oldLeft) > 1 || Math.Abs(widget.Top - oldTop) > 1)
                throw new InvalidOperationException("Retry countdowns must not add widget space or move it.");

            notifications.Clear();
            account.Tick();
            await FlushAsync();
            if (!notifications.Contains(nameof(AccountViewModel.Notice)) || !notifications.Contains(nameof(AccountViewModel.HasNotice)))
                throw new InvalidOperationException("A scheduled retry countdown must refresh on timer ticks.");
            AssertNotice(failure, "초 후 재시도", true, false);

            account.Apply(retry with { State = UsageState.Unavailable });
            await FlushAsync();
            AssertNotice(failure, "초 후 재시도", true, false);

            account.Apply(retry with { State = UsageState.RateLimited, Message = null, NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(90) });
            await FlushAsync();
            AssertNotice("조회 제한", "분 후 재시도", true, false);
            if (account.Notice.Contains("대기 ·", StringComparison.Ordinal))
                throw new InvalidOperationException("Rate limits must not repeat retry waiting text before the countdown.");

            account.Apply(retry with { NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
            await FlushAsync();
            AssertNotice(failure, "재시도 대기", true, false);

            // A running retry uses the refresh glyph and progress, regardless of the retained error.
            account.Apply(retry with { IsRefreshing = true });
            await FlushAsync();
            AssertNotice("새로고침 중", "새로고침 중", false, true);
            const string progress = "사용량을 다시 확인하고 있습니다.";
            account.Report(new(progress));
            account.Tick();
            await FlushAsync();
            AssertNotice(progress, progress, false, true);

            // A normal future poll is not a retry, and recovery removes the error notice.
            account.Apply(original with
            {
                State = UsageState.Ready, Message = null, IsRefreshing = false,
                IsRetryScheduled = false, NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(5)
            });
            await FlushAsync();
            AssertNotice("", "", false, false);
            if (account.HasNotice)
                throw new InvalidOperationException("Recovery must clear the retry countdown and failure notice.");

            foreach (var state in new[] { UsageState.SignInRequired, UsageState.UpdateRequired })
            {
                account.Apply(failed with { State = state, NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(5) });
                account.Tick();
                await FlushAsync();
                AssertNotice(failure, failure, true, false);
            }
        }
        finally
        {
            account.PropertyChanged -= changed;
            account.Settings.AutoRefresh = originalAutoRefresh;
            account.Apply(original);
            await FlushAsync();
            summary.DataContext = null;
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

        void AssertNotice(string prefix, string suffix, bool issue, bool busy)
        {
            if (account.HasUsageIssue != issue || account.IsRefreshing != busy ||
                !Matches(account.Notice))
                throw new InvalidOperationException("Retry state did not match its notice or refresh indicator.");
            foreach (var (surface, name) in new (DependencyObject, string)[]
            {
                (widget, "widget"), (settings, "settings"), (summary, "tray")
            })
            {
                var icon = VisualChildren<TextBlock>(surface).Single(text =>
                    text.Name == "AccountStatusIcon" && ReferenceEquals(text.DataContext, account));
                if (icon.ToolTip is not string tooltip || !Matches(tooltip) ||
                    icon.Text != (busy ? "\uE72C" : "\uE7BA") ||
                    icon.Visibility != (issue || busy ? Visibility.Visible : Visibility.Collapsed))
                    throw new InvalidOperationException($"The {name} retry tooltip or icon did not update.");
            }
            var notice = VisualChildren<TextBlock>(settings).Single(text => ReferenceEquals(text.DataContext, account) &&
                BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == nameof(AccountViewModel.Notice));
            if (!Matches(notice.Text) || notice.Visibility != (account.HasNotice ? Visibility.Visible : Visibility.Collapsed))
                throw new InvalidOperationException("The account notice did not display the retry countdown.");

            bool Matches(string value) => value.StartsWith(prefix, StringComparison.Ordinal) && value.EndsWith(suffix, StringComparison.Ordinal) &&
                (prefix.Length > 0 || value.Length == 0);
        }
    }
}
