using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public sealed partial class AppController
{
    internal static async Task VerifyResetMessageFlowAsync()
    {
        foreach (var kind in new[] { ProviderKind.Claude, ProviderKind.Codex })
        foreach (var scenario in new[] { "success", "unknown", "save-failure", "disabled", "already-active", "update-race", "disable-race" })
        {
            var now = DateTimeOffset.UtcNow;
            var account = new AccountSettings
            {
                Provider = kind, IdentityKey = "reset-smoke-identity", Alias = "검증",
                ResetMessageEnabled = scenario != "disabled",
                ResetMessageWindowId = kind == ProviderKind.Claude ? "session::0" : "codex:primary"
            };
            var before = new UsageSnapshot(kind, account.Id, account.IdentityKey, "Pro", now.AddMinutes(-1),
                [new(account.ResetMessageWindowId, "세션", 20, now.AddSeconds(-10))]);
            var engine = new ResetMessageEngine();
            engine.Observe(account, account.ResetMessageEnabled, account.ResetMessageWindowId, before, before.FetchedAt, true);
            ResetMessageState persisted = engine.GetState();
            var provider = new ResetFlowProvider(kind, before, scenario == "already-active", scenario == "unknown", () =>
            {
                if (!persisted.Attempts.Any(attempt => attempt.AccountId == account.Id && attempt.Outcome == ResetMessageOutcome.Reserved))
                    throw new InvalidOperationException("An automatic message must have a persisted reservation before invocation.");
            });
            var settings = new AppSettings { Accounts = [account] };
            await using (var controller = new AppController(settings, new JsonSettingsStore(new AppPaths()), [provider], [before],
                demo: true, resetMessageState: engine.GetState()))
            {
                controller._saveResetStateForVerification = state =>
                {
                    if (scenario == "save-failure") throw new IOException("Simulated receipt storage failure.");
                    persisted = state;
                    if (state.Attempts.Any(attempt => attempt.Outcome == ResetMessageOutcome.Reserved))
                    {
                        // Change state while the reservation write is in progress. Eligibility
                        // must be checked again before control reaches the provider.
                        if (scenario == "update-race") controller._preparingUpdate = true;
                        if (scenario == "disable-race") account.ResetMessageEnabled = false;
                    }
                    return Task.CompletedTask;
                };
                await controller.RunResetMessageCycleAsync();
                await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await controller.RunResetMessageCycleAsync();
                await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var expectedSends = scenario is "success" or "unknown" ? 1 : 0;
                if (provider.Sends != expectedSends)
                    throw new InvalidOperationException($"Reset message {kind}/{scenario}: expected {expectedSends} calls, got {provider.Sends}.");
                if (expectedSends == 1 && persisted.Attempts.Single().Outcome !=
                    (scenario == "success" ? ResetMessageOutcome.Succeeded : ResetMessageOutcome.Unknown))
                    throw new InvalidOperationException("The final reset-message receipt must preserve its outcome.");
                if (scenario is "update-race" or "disable-race" && persisted.Attempts.Single().Outcome != ResetMessageOutcome.Unknown)
                    throw new InvalidOperationException("Interrupted reservations must still suppress future sends.");
                if (controller.ViewModel.Error is not null)
                    throw new InvalidOperationException("Reset-message failures must not alter the usage widget's error state: " + controller.ViewModel.Error);
            }
            // Reloading receipts must not send the same reset again, including uncertain results.
            if (scenario is not ("success" or "unknown")) continue;
            await using var restarted = new AppController(settings, new JsonSettingsStore(new AppPaths()), [provider], [before],
                demo: true, resetMessageState: persisted);
            var previousSends = provider.Sends;
            await restarted.RunResetMessageCycleAsync();
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (provider.Sends != previousSends) throw new InvalidOperationException("Restart must preserve reset-message suppression.");
        }
    }

    private sealed class ResetFlowProvider(ProviderKind kind, UsageSnapshot baseline, bool alreadyActive, bool failSend,
        Action assertReceipt) : IUsageProvider, IResetMessageProvider
    {
        public ProviderKind Kind => kind;
        public int Sends { get; private set; }
        public Task<ConnectionResult> ConnectAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken token)
            => throw new InvalidOperationException("Reset verification cannot log in.");
        public Task DisconnectAsync(AccountSettings account, CancellationToken token) => Task.CompletedTask;
        public Task<UsageSnapshot> FetchAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var active = alreadyActive || Sends > 0 && !failSend;
            return Task.FromResult(baseline with
            {
                FetchedAt = DateTimeOffset.UtcNow,
                Windows = [baseline.Windows[0] with { UsedPercent = active ? 1 : 0, ResetsAt = active ? DateTimeOffset.UtcNow.AddHours(5) : null }]
            });
        }
        public Task SendResetMessageAsync(AccountSettings account, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            assertReceipt();
            Sends++;
            if (failSend) throw new IOException("Simulated uncertain completion.");
            return Task.CompletedTask;
        }
    }
}

public partial class App
{
    private async Task VerifyResetMessageSettingsAsync(SettingsWindow settings)
    {
        if (_controller is not { IsDemo: true }) throw new InvalidOperationException("Reset verification requires demo mode.");
        var accounts = _controller.ViewModel.Accounts;
        if (accounts.Any(account => account.ResetMessageEnabled) ||
            accounts.Any(account => account.SupportsResetMessage != (account.Settings.Provider is ProviderKind.Claude or ProviderKind.Codex)))
            throw new InvalidOperationException("Automatic reset messages must default off and support Claude/Codex only.");
        foreach (var account in accounts.Where(account => account.SupportsResetMessage))
        {
            var expander = VisualChildren<Expander>(settings).First(item => ReferenceEquals(item.DataContext, account));
            expander.IsExpanded = true;
            await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var checkbox = VisualChildren<CheckBox>(settings).Single(item => ReferenceEquals(item.DataContext, account) &&
                AutomationProperties.GetAutomationId(item) == "ResetMessageEnabled");
            checkbox.IsChecked = true;
            await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
            if (!account.Settings.ResetMessageEnabled || accounts.Any(other => other != account && other.ResetMessageEnabled))
                throw new InvalidOperationException("Automatic messages must be independently configurable per account.");
            checkbox.IsChecked = false;
            await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
        }
        await AppController.VerifyResetMessageFlowAsync();
    }
}
