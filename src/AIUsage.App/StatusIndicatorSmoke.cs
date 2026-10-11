using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyStatusIndicatorsAsync(MainWindow window, SettingsWindow settings, AccountViewModel account)
    {
        var original = _controller!.GetStatuses().Single(status => status.AccountId == account.Id);
        var ready = original with { State = UsageState.Ready, Message = null, IsRefreshing = false };
        var summary = new TraySummary(_controller.ViewModel);
        var notifications = new HashSet<string>(StringComparer.Ordinal);
        PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName is { } name) notifications.Add(name);
        };
        account.PropertyChanged += changed;
        try
        {
            await ApplyAndAssertAsync(ready, false, false, "idle ready");
            await ApplyAndAssertAsync(ready with { IsRefreshing = true }, true, false, "refresh from ready");
            await ReportAndAssertAsync("상태 표시 검사: 사용량을 확인하고 있습니다.", true, "refresh progress");

            const string failedMessage = "상태 표시 검사: 사용량 연결에 실패했습니다.";
            var failed = ready with { State = UsageState.Offline, Message = failedMessage };
            await ApplyAndAssertAsync(failed, false, true, "completed failed refresh");
            if (account.Notice != failedMessage)
                throw new InvalidOperationException("A completed refresh failure must retain its explanatory message.");

            // The coordinator retains the previous failure state/message until the retry finishes.
            // In-flight presentation must still be a refresh, without the stale failure warning.
            await ApplyAndAssertAsync(failed with { IsRefreshing = true }, true, false, "retry after failure");
            if (!account.HasNotice || account.Notice.Contains(failedMessage, StringComparison.Ordinal))
                throw new InvalidOperationException("Retrying must replace the previous failure notice with refresh progress.");
            await ReportAndAssertAsync("상태 표시 검사: 다시 확인하고 있습니다.", true, "retry progress");

            await ApplyAndAssertAsync(ready with
            {
                State = UsageState.RateLimited,
                Message = "상태 표시 검사: 잠시 후 다시 확인하세요."
            }, false, true, "retry finished with rate limit");
            await ApplyAndAssertAsync(ready, false, false, "successful recovery");
            if (account.HasNotice)
                throw new InvalidOperationException("A successful refresh must clear the previous failure and progress notice.");

            // An informational notice alone must never cause an exclamation mark.
            const string information = "상태 표시 검사: 계정 정보를 확인했습니다.";
            await ApplyAndAssertAsync(ready with { Message = information }, false, false, "ready informational notice");
            account.Report(new ProviderProgress("상태 표시 검사: 늦게 도착한 진행 메시지"));
            await UpdateSurfacesAsync();
            AssertSurfaces(false, false, "late progress after completion");
            if (!account.HasNotice || account.Notice != information)
                throw new InvalidOperationException("Late progress must not replace the completed account's informational notice or show a warning.");
            await ApplyAndAssertAsync(ready with { IsRefreshing = true }, true, false, "next refresh after late progress");
            if (account.Notice != account.StateLabel)
                throw new InvalidOperationException("A new refresh must not revive an earlier progress message.");
        }
        finally
        {
            account.PropertyChanged -= changed;
            account.Apply(original);
            await UpdateSurfacesAsync();
            summary.DataContext = null;
        }

        async Task ApplyAndAssertAsync(AccountUsageStatus status, bool refreshing, bool issue, string phase)
        {
            var previous = IndicatorProperties();
            notifications.Clear();
            account.Apply(status);
            foreach (var (property, value) in IndicatorProperties())
                if (!Equals(previous[property], value) && !notifications.Contains(property))
                    throw new InvalidOperationException($"Status transition '{phase}' did not notify {property}.");
            await UpdateSurfacesAsync();
            AssertSurfaces(refreshing, issue, phase);
        }

        async Task ReportAndAssertAsync(string message, bool refreshing, string phase)
        {
            var hadNotice = account.HasNotice;
            notifications.Clear();
            account.Report(new ProviderProgress(message));
            if (!notifications.Contains(nameof(AccountViewModel.Notice)) ||
                (!hadNotice && !notifications.Contains(nameof(AccountViewModel.HasNotice))) || !account.HasNotice || account.Notice != message)
                throw new InvalidOperationException($"Progress '{phase}' did not update its informational notice.");
            await UpdateSurfacesAsync();
            AssertSurfaces(refreshing, false, phase);
        }

        Dictionary<string, object?> IndicatorProperties() => new()
        {
            [nameof(AccountViewModel.IsRefreshing)] = account.IsRefreshing,
            [nameof(AccountViewModel.HasUsageIssue)] = account.HasUsageIssue,
            [nameof(AccountViewModel.HasStatusIndicator)] = account.HasStatusIndicator,
            [nameof(AccountViewModel.StatusIcon)] = account.StatusIcon,
            [nameof(AccountViewModel.StateBrush)] = account.StateBrush,
            [nameof(AccountViewModel.StateLabel)] = account.StateLabel,
            [nameof(AccountViewModel.Notice)] = account.Notice,
            [nameof(AccountViewModel.HasNotice)] = account.HasNotice
        };

        async Task UpdateSurfacesAsync()
        {
            await Dispatcher.InvokeAsync(() =>
            {
                window.UpdateLayout();
                settings.UpdateLayout();
                // Realize the tray DataTemplate without opening another native popup.
                summary.Measure(new Size(300, double.PositiveInfinity));
                summary.Arrange(new Rect(summary.DesiredSize));
                summary.UpdateLayout();
            }, DispatcherPriority.ApplicationIdle);
        }

        void AssertSurfaces(bool refreshing, bool issue, string phase)
        {
            var visible = refreshing || issue;
            var glyph = refreshing ? "\uE72C" : "\uE7BA";
            var brush = (Brush)FindResource(refreshing ? "AccentBrush" : issue ? "WarningBrush" : "MutedBrush");
            if (account.IsRefreshing != refreshing || account.HasUsageIssue != issue ||
                account.HasStatusIndicator != visible || account.StatusIcon != glyph || !ReferenceEquals(account.StateBrush, brush))
                throw new InvalidOperationException($"Status presentation is inconsistent during '{phase}'.");

            foreach (var (surface, name) in new (DependencyObject, string)[]
            {
                (window, "widget"), (settings, "settings"), (summary, "tray summary")
            })
            {
                var icon = VisualChildren<TextBlock>(surface).SingleOrDefault(text =>
                    text.Name == "AccountStatusIcon" && ReferenceEquals(text.DataContext, account));
                if (icon is null || icon.Text != glyph || icon.Visibility != (visible ? Visibility.Visible : Visibility.Collapsed) ||
                    !ReferenceEquals(icon.Foreground, brush))
                    throw new InvalidOperationException($"The {name} status icon did not follow '{phase}'.");
            }
        }
    }
}
