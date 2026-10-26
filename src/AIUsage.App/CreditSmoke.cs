using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyCreditsAsync(MainWindow widget, SettingsWindow settings)
    {
        if (_controller is not { IsDemo: true } controller)
            throw new InvalidOperationException("Credit verification requires the isolated demo.");
        var account = controller.ViewModel.Accounts.First(item => item.Settings.Provider == ProviderKind.Codex);
        var original = controller.GetStatuses().Single(status => status.AccountId == account.Id);
        var originalShowCredits = account.ShowCreditsInWidget;
        var originalShowLastChecked = account.ShowLastChecked;
        var originalWidth = widget.Width;
        var originalLeft = widget.Left;
        var originalTop = widget.Top;
        var tabs = (TabControl)settings.FindName("SettingsTabs");
        var originalTab = tabs.SelectedIndex;
        tabs.SelectedIndex = 0;
        await FlushAsync();
        var expander = VisualChildren<Expander>(settings).Single(item => ReferenceEquals(item.DataContext, account));
        var originalExpanded = expander.IsExpanded;
        expander.IsExpanded = true;
        await FlushAsync();
        var toggle = VisualChildren<CheckBox>(settings).Single(item =>
            ReferenceEquals(item.DataContext, account) && AutomationProperties.GetAutomationId(item) == "ShowCreditsInWidget");
        try
        {
            if (toggle.IsChecked != false || originalShowCredits)
                throw new InvalidOperationException("Widget credits must default off.");
            widget.Width = widget.MinWidth;
            widget.Left = 100;
            widget.Top = 100;
            var fixture = original with
            {
                State = UsageState.Ready,
                Message = null,
                IsRefreshing = false,
                Snapshot = original.Snapshot! with
                {
                    Credits = [new("credit:balance", "크레딧", "credits", Balance: 0)]
                }
            };
            account.Apply(fixture);
            widget.FitContent();
            await FlushAsync();
            var baselineHeight = widget.ActualHeight;
            var baselineWidth = widget.ActualWidth;
            var baselineLeft = widget.Left;
            var baselineTop = widget.Top;
            AssertAccountValue("0 credits");
            if (CreditBlock().Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Account credit details must not enable widget rows automatically.");

            toggle.IsChecked = true;
            await FlushAsync();
            // Preference saving republishes the coordinator's immutable demo data.
            // Apply the synthetic response after that event, without fetching anything.
            account.Apply(fixture);
            await FlushAsync();
            if (!account.Settings.ShowCreditsInWidget || CreditBlock().Visibility != Visibility.Visible || widget.ActualHeight <= baselineHeight)
                throw new InvalidOperationException("The credit setting must reveal rows and fit height immediately.");
            AssertWidgetValues("0 credits");
            AssertPosition();
            var cached = account.Credits;
            account.Tick();
            account.Apply(fixture with { IsRefreshing = true });
            await FlushAsync();
            if (!ReferenceEquals(account.Credits, cached) || account.CreditsAreStale)
                throw new InvalidOperationException("A timer tick or refresh start must retain the same successful credit rows.");

            // Cached amounts remain identifiable even when the timestamp preference is off.
            account.ShowLastChecked = false;
            account.Apply(fixture with { State = UsageState.Offline });
            await FlushAsync();
            var stale = VisualChildren<TextBlock>(widget).Single(item => item.Name == "WidgetCreditStaleLabel" && ReferenceEquals(item.DataContext, account));
            if (!account.CreditsAreStale || stale.Visibility != Visibility.Visible || stale.Text != "이전 값" ||
                !account.Credits[0].Detail.Contains("이전 값", StringComparison.Ordinal))
                throw new InvalidOperationException("Failed credit queries must identify cached values independently of timestamp visibility.");
            AssertWidgetValues("0 credits");
            account.Apply(fixture with { State = UsageState.Offline, IsRefreshing = true });
            await FlushAsync();
            if (!account.CreditsAreStale || !ReferenceEquals(account.Credits, cached))
                throw new InvalidOperationException("Retrying must not reclassify cached credits as a successful response.");

            account.ShowLastChecked = originalShowLastChecked;
            var twoCredits = fixture with
            {
                Snapshot = fixture.Snapshot! with
                {
                    Credits = [new("credit:balance", "크레딧", "credits", Balance: 0),
                        new("credit:unlimited", "추가 크레딧", "credits", Unlimited: true)]
                }
            };
            account.Apply(twoCredits);
            await FlushAsync();
            AssertWidgetValues("0 credits", "무제한");
            AssertPosition();
            if (stale.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("A successful credit query must remove the stale marker.");
            var twoRowsHeight = widget.ActualHeight;
            account.Apply(twoCredits with { Snapshot = twoCredits.Snapshot! with { Credits = null } });
            await FlushAsync();
            AssertWidgetValues("—");
            if (account.HasCredits || account.Credits.Count != 0 || account.CompactCredits.Single().Name != "크레딧" ||
                widget.ActualHeight >= twoRowsHeight || !account.ShowCreditsInWidget)
                throw new InvalidOperationException("Missing credits must clear balances and retain the opted-in placeholder.");
            var unavailable = VisualChildren<TextBlock>(settings).Single(item => item.Name == "AccountCreditsUnavailable" && ReferenceEquals(item.DataContext, account));
            if (unavailable.Visibility != Visibility.Visible || unavailable.Text != "크레딧 정보 미제공")
                throw new InvalidOperationException("The account page must distinguish missing credits from zero.");
            AssertPosition();

            const string creditFailure = "크레딧을 확인하지 못했습니다.";
            account.Apply(fixture with { Snapshot = fixture.Snapshot! with { Credits = null, CreditsMessage = creditFailure } });
            await FlushAsync();
            AssertWidgetValues("—");
            if (account.HasUsageIssue || account.StateLabel != "연결됨" ||
                !account.Windows.Select(item => item.Source).SequenceEqual(fixture.Snapshot!.Windows) ||
                unavailable.Text != creditFailure || account.CreditsUnavailableLabel != creditFailure ||
                !account.CompactCredits.Single().Detail.Contains(creditFailure, StringComparison.Ordinal))
                throw new InvalidOperationException("A credit-only failure must explain its placeholder without changing quota values or readiness.");

            // Credits are useful even when the provider supplies no quota windows.
            account.Apply(fixture with { Snapshot = fixture.Snapshot! with { Windows = [] } });
            await FlushAsync();
            AssertAccountValue("0 credits");
            AssertWidgetValues("0 credits");
            if (account.HasWindows || !VisualChildren<TextBlock>(settings).Single(item => item.Name == "AccountCreditValue" && ReferenceEquals(item.DataContext, account.Credits[0])).IsVisible)
                throw new InvalidOperationException("Credit details must remain visible without quota windows.");
            account.Apply(fixture with { State = UsageState.SignInRequired, Snapshot = null });
            await FlushAsync();
            AssertWidgetValues("—");
            if (account.HasCredits || account.CreditsAreStale)
                throw new InvalidOperationException("Clearing account identity must clear its previous credit balance.");
            account.Apply(fixture with { Snapshot = fixture.Snapshot! with { IdentityKey = "other-demo-account", Credits = null } });
            await FlushAsync();
            AssertWidgetValues("—");

            account.Apply(fixture);
            toggle.IsChecked = false;
            await FlushAsync();
            if (account.ShowCreditsInWidget || CreditBlock().Visibility != Visibility.Collapsed || Math.Abs(widget.ActualHeight - baselineHeight) > 1)
                throw new InvalidOperationException("Hiding credits must remove their height without leaving empty space.");
            AssertPosition();

            void AssertPosition()
            {
                if (Math.Abs(widget.ActualWidth - baselineWidth) > 1 || Math.Abs(widget.Left - baselineLeft) > 1 || Math.Abs(widget.Top - baselineTop) > 1)
                    throw new InvalidOperationException("Credit row changes must preserve widget width and position.");
            }
        }
        finally
        {
            account.ShowCreditsInWidget = originalShowCredits;
            account.ShowLastChecked = originalShowLastChecked;
            account.Apply(original);
            expander.IsExpanded = originalExpanded;
            tabs.SelectedIndex = originalTab;
            widget.Width = originalWidth;
            widget.Left = originalLeft;
            widget.Top = originalTop;
            widget.FitContent();
            await FlushAsync();
        }

        StackPanel CreditBlock() => VisualChildren<StackPanel>(widget).Single(item => item.Name == "WidgetCredits" && ReferenceEquals(item.DataContext, account));
        void AssertAccountValue(string expected)
        {
            if (account.Credits.Count != 1 || account.Credits[0].Value != expected ||
                VisualChildren<TextBlock>(settings).Single(item => item.Name == "AccountCreditValue" && ReferenceEquals(item.DataContext, account.Credits[0])).Text != expected)
                throw new InvalidOperationException($"The account credit value must be '{expected}'.");
        }
        void AssertWidgetValues(params string[] expected)
        {
            var rows = VisualChildren<TextBlock>(CreditBlock()).Where(item => item.Name == "WidgetCreditValue").ToArray();
            if (!rows.Select(item => item.Text).SequenceEqual(expected) ||
                rows.Any(item => item.DataContext is not AccountCreditViewModel credit || string.IsNullOrWhiteSpace(credit.Detail)))
                throw new InvalidOperationException("Widget credit values or their explanatory tooltips did not follow the account snapshot.");
        }
        Task FlushAsync() => Dispatcher.InvokeAsync(() => { widget.UpdateLayout(); settings.UpdateLayout(); }, DispatcherPriority.ApplicationIdle).Task;
    }
}
