using AIUsage.Core;
using AIUsage.Providers;
using Xunit;

namespace AIUsage.Tests;

public sealed class MissingUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PartialResponseDisplaysFreshValuesButRetriesAndKeepsOnlyCompleteCache()
    {
        var account = Account();
        account.AutoRefresh = false;
        account.WidgetWindows = [new("session", "Session"), new("weekly", "Weekly")];
        var previous = Snapshot(account, new UsageWindow("session", "Session", 10, null), new UsageWindow("weekly", "Weekly", 40, null));
        var partial = Snapshot(account, new UsageWindow("session", "Session", 25, null)) with { FetchedAt = Now.AddMinutes(1) };
        var recovered = Snapshot(account, new UsageWindow("session", "Session", 26, null), new UsageWindow("weekly", "Weekly", 45, null))
            with { FetchedAt = Now.AddMinutes(2) };
        var time = new ManualTime(Now);
        var provider = new FakeProvider(_ => partial);
        await using var coordinator = new UsageCoordinator([provider], [account], [previous], time);

        await coordinator.RefreshAsync(account.Id);

        var status = coordinator.GetStatus(account.Id);
        AssertRetry(status, Now.AddSeconds(30));
        Assert.Equal(UsageState.Unavailable, status.Snapshot!.State);
        Assert.Equal(partial.FetchedAt, status.Snapshot.FetchedAt);
        Assert.Equal(25, Assert.Single(status.Snapshot.Windows).UsedPercent);
        Assert.Same(previous, Assert.Single(coordinator.GetSnapshots()));

        time.Advance(TimeSpan.FromSeconds(29));
        await coordinator.RefreshDueAsync();
        Assert.Equal(1, provider.Calls);
        provider.Fetch = _ => recovered;
        time.Advance(TimeSpan.FromSeconds(1));
        await coordinator.RefreshDueAsync();

        Assert.Equal(2, provider.Calls);
        Assert.Equal(UsageState.Ready, coordinator.GetStatus(account.Id).State);
        Assert.False(coordinator.GetStatus(account.Id).IsRetryScheduled);
        Assert.Same(recovered, Assert.Single(coordinator.GetSnapshots()));
    }

    [Theory]
    [InlineData("widget", false)]
    [InlineData("widget", true)]
    [InlineData("tray", false)]
    [InlineData("tray", true)]
    [InlineData("legacy", false)]
    [InlineData("legacy", true)]
    public async Task ExplicitSelectionMissingOrUnknownSchedulesRetry(string selection, bool present)
    {
        var account = Account();
        switch (selection)
        {
            case "widget": account.WidgetWindows = [new("wanted", "Wanted")]; break;
            case "tray": account.TrayWindow = new("wanted", "Wanted"); break;
            case "legacy": account.SelectedWindowId = "wanted"; break;
        }
        List<UsageWindow> windows = [new("other", "Other", 20, null)];
        if (present) windows.Add(new("wanted", "Wanted", null, Now.AddHours(1)));
        var response = Snapshot(account, windows.ToArray());
        await using var coordinator = new UsageCoordinator([new FakeProvider(_ => response)], [account], timeProvider: new ManualTime(Now));

        await coordinator.RefreshAsync(account.Id);

        AssertRetry(coordinator.GetStatus(account.Id), Now.AddSeconds(30));
        Assert.Empty(coordinator.GetSnapshots());
    }

    [Fact]
    public async Task ExplicitWidgetSelectionSupersedesLegacySelection()
    {
        var account = Account();
        account.SelectedWindowId = "old-id";
        account.WidgetWindows = [new("session", "Session")];
        var response = Snapshot(account, new UsageWindow("session", "Session", 20, null));
        await using var coordinator = new UsageCoordinator([new FakeProvider(_ => response)], [account], timeProvider: new ManualTime(Now));

        await coordinator.RefreshAsync(account.Id);

        Assert.Equal(UsageState.Ready, coordinator.GetStatus(account.Id).State);
        Assert.False(coordinator.GetStatus(account.Id).IsRetryScheduled);
    }

    [Fact]
    public async Task PreviouslyKnownValueBecomingUnknownRetriesEvenWhenNotSelected()
    {
        var account = Account();
        account.WidgetWindows = [new("session", "Session")];
        var previous = Snapshot(account, new UsageWindow("session", "Session", 20, null), new UsageWindow("weekly", "Weekly", 30, null));
        var response = Snapshot(account, new UsageWindow("session", "Session", 25, null), new UsageWindow("weekly", "Weekly", null, null));
        await using var coordinator = new UsageCoordinator([new FakeProvider(_ => response)], [account], [previous], new ManualTime(Now));

        await coordinator.RefreshAsync(account.Id);

        AssertRetry(coordinator.GetStatus(account.Id), Now.AddSeconds(30));
        Assert.Null(coordinator.GetStatus(account.Id).Snapshot!.Windows.Single(w => w.Id == "weekly").UsedPercent);
        Assert.Same(previous, Assert.Single(coordinator.GetSnapshots()));
    }

    [Fact]
    public async Task RemovedUnselectedLimitDoesNotCauseEndlessRetry()
    {
        var account = Account();
        account.WidgetWindows = [new("session", "Session")];
        var previous = Snapshot(account, new UsageWindow("session", "Session", 20, null), new UsageWindow("retired", "Retired", 30, null));
        var response = Snapshot(account, new UsageWindow("session", "Session", 25, null));
        await using var coordinator = new UsageCoordinator([new FakeProvider(_ => response)], [account], [previous], new ManualTime(Now));

        await coordinator.RefreshAsync(account.Id);

        Assert.Equal(UsageState.Ready, coordinator.GetStatus(account.Id).State);
        Assert.False(coordinator.GetStatus(account.Id).IsRetryScheduled);
        Assert.Same(response, Assert.Single(coordinator.GetSnapshots()));
    }

    [Fact]
    public async Task CreditsDoNotHideLossOfAllPreviouslyKnownQuotas()
    {
        var account = Account();
        var previous = Snapshot(account, new UsageWindow("session", "Session", 20, null));
        var response = Snapshot(account) with { Credits = [new("credits", "Credits", "credits", Balance: 100)] };
        await using var coordinator = new UsageCoordinator([new FakeProvider(_ => response)], [account], [previous], new ManualTime(Now));

        await coordinator.RefreshAsync(account.Id);

        var status = coordinator.GetStatus(account.Id);
        AssertRetry(status, Now.AddSeconds(30));
        Assert.Empty(status.Snapshot!.Windows);
        Assert.Equal(100m, Assert.Single(status.Snapshot.Credits!).Balance);
        Assert.Same(previous, Assert.Single(coordinator.GetSnapshots()));
    }

    [Fact]
    public async Task GenuineCreditOnlyAccountRemainsReady()
    {
        var account = Account();
        var response = Snapshot(account) with { Credits = [new("credits", "Credits", "credits", Balance: 0)] };
        await using var coordinator = new UsageCoordinator([new FakeProvider(_ => response)], [account], timeProvider: new ManualTime(Now));

        await coordinator.RefreshAsync(account.Id);

        Assert.Equal(UsageState.Ready, coordinator.GetStatus(account.Id).State);
        Assert.False(coordinator.GetStatus(account.Id).IsRetryScheduled);
        Assert.Same(response, Assert.Single(coordinator.GetSnapshots()));
    }

    [Fact]
    public async Task UnknownQuotaValuesCannotBeMaskedByCreditsWithoutExplicitSelection()
    {
        var account = Account();
        var response = Snapshot(account, new UsageWindow("session", "Session", null, Now.AddHours(1)))
            with { Credits = [new("credits", "Credits", "credits", Balance: 100)] };
        await using var coordinator = new UsageCoordinator([new FakeProvider(_ => response)], [account], timeProvider: new ManualTime(Now));

        await coordinator.RefreshAsync(account.Id);

        AssertRetry(coordinator.GetStatus(account.Id), Now.AddSeconds(30));
        Assert.Empty(coordinator.GetSnapshots());
        Assert.Null(Assert.Single(coordinator.GetStatus(account.Id).Snapshot!.Windows).UsedPercent);
    }

    [Fact]
    public async Task ZeroPercentAndMissingResetTimestampAreValid()
    {
        var account = Account();
        account.WidgetWindows = [new("session", "Session")];
        var response = Snapshot(account, new UsageWindow("session", "Session", 0, null));
        await using var coordinator = new UsageCoordinator([new FakeProvider(_ => response)], [account], timeProvider: new ManualTime(Now));

        await coordinator.RefreshAsync(account.Id);

        Assert.Equal(UsageState.Ready, coordinator.GetStatus(account.Id).State);
        Assert.False(coordinator.GetStatus(account.Id).IsRetryScheduled);
        Assert.Equal(0, Assert.Single(coordinator.GetStatus(account.Id).Snapshot!.Windows).UsedPercent);
    }

    [Fact]
    public async Task ExplicitlyDisabledSelectedLimitDoesNotRetryMissingValue()
    {
        var account = Account();
        account.WidgetWindows = [new("session", "Session"), new("disabled", "Disabled")];
        var previous = Snapshot(account, new UsageWindow("session", "Session", 20, null), new UsageWindow("disabled", "Disabled", 30, null));
        var response = Snapshot(account, new UsageWindow("session", "Session", 25, null), new UsageWindow("disabled", "Disabled", null, null, IsDisabled: true));
        await using var coordinator = new UsageCoordinator([new FakeProvider(_ => response)], [account], [previous], new ManualTime(Now));

        await coordinator.RefreshAsync(account.Id);

        Assert.Equal(UsageState.Ready, coordinator.GetStatus(account.Id).State);
        Assert.False(coordinator.GetStatus(account.Id).IsRetryScheduled);
        Assert.True(coordinator.GetStatus(account.Id).Snapshot!.Windows.Single(w => w.Id == "disabled").IsDisabled);
    }

    [Fact]
    public async Task NeverKnownUnselectedValueDoesNotInvalidateKnownUsage()
    {
        var account = Account();
        account.WidgetWindows = [new("session", "Session")];
        var response = Snapshot(account, new UsageWindow("session", "Session", 25, null), new UsageWindow("optional", "Optional", null, null));
        await using var coordinator = new UsageCoordinator([new FakeProvider(_ => response)], [account], timeProvider: new ManualTime(Now));

        await coordinator.RefreshAsync(account.Id);

        Assert.Equal(UsageState.Ready, coordinator.GetStatus(account.Id).State);
        Assert.False(coordinator.GetStatus(account.Id).IsRetryScheduled);
    }

    [Fact]
    public async Task IncompleteReadyCacheIsReevaluatedAndRetriedWithAutomaticRefreshOff()
    {
        var account = Account();
        account.AutoRefresh = false;
        account.WidgetWindows = [new("weekly", "Weekly")];
        var cached = Snapshot(account, new UsageWindow("session", "Session", 20, null));
        var recovered = Snapshot(account, new UsageWindow("session", "Session", 20, null), new UsageWindow("weekly", "Weekly", 30, null));
        var time = new ManualTime(Now);
        var provider = new FakeProvider(_ => recovered);
        await using var coordinator = new UsageCoordinator([provider], [account], [cached], time);

        AssertRetry(coordinator.GetStatus(account.Id), Now);
        Assert.Equal(UsageState.Unavailable, coordinator.GetStatus(account.Id).Snapshot!.State);
        Assert.Empty(coordinator.GetSnapshots());
        await coordinator.RefreshDueAsync();

        Assert.Equal(1, provider.Calls);
        Assert.Equal(UsageState.Ready, coordinator.GetStatus(account.Id).State);
    }

    [Fact]
    public async Task DuplicateIdsInOlderCacheFollowTheDisplayedFirstOccurrence()
    {
        var account = Account();
        account.WidgetWindows = [new("session", "Session")];
        var cached = Snapshot(account, new UsageWindow("session", "Session", 20, null),
            new UsageWindow("session", "Duplicate", null, null));
        await using var coordinator = new UsageCoordinator([new FakeProvider(_ => cached)], [account], [cached], new ManualTime(Now));

        Assert.Equal(UsageState.Ready, coordinator.GetStatus(account.Id).State);
        Assert.False(coordinator.GetStatus(account.Id).IsRetryScheduled);
        Assert.Equal(20, Assert.Single(UsageFormatting.SelectCompactWindows(account, coordinator.GetStatus(account.Id).Snapshot)).UsedPercent);
    }

    [Fact]
    public async Task RepeatedPartialResponsesBackOffAndSettingsUpdatesDoNotResetDelay()
    {
        var account = Account();
        account.AutoRefresh = false;
        account.WidgetWindows = [new("missing", "Missing")];
        var time = new ManualTime(Now);
        var response = Snapshot(account, new UsageWindow("session", "Session", 20, null));
        var provider = new FakeProvider(_ => response);
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await coordinator.RefreshAsync(account.Id);
        AssertRetry(coordinator.GetStatus(account.Id), Now.AddSeconds(30));

        account.Alias = "Updated alias";
        coordinator.UpdateAccount(account);
        AssertRetry(coordinator.GetStatus(account.Id), Now.AddSeconds(30));
        time.Advance(TimeSpan.FromSeconds(30));
        await coordinator.RefreshDueAsync();
        AssertRetry(coordinator.GetStatus(account.Id), Now.AddSeconds(90));

        account.RefreshMinutes = 1440;
        coordinator.UpdateAccount(account);
        AssertRetry(coordinator.GetStatus(account.Id), Now.AddSeconds(90));
        time.Advance(TimeSpan.FromSeconds(60));
        await coordinator.RefreshDueAsync();
        AssertRetry(coordinator.GetStatus(account.Id), Now.AddSeconds(210));
        Assert.Equal(3, provider.Calls);
    }

    [Fact]
    public async Task ChangingMissingSelectionUpdatesWarningWithoutMovingRetryDeadline()
    {
        var account = Account();
        account.WidgetWindows = [new("missing-weekly", "Weekly limit")];
        var time = new ManualTime(Now);
        var response = Snapshot(account, new UsageWindow("session", "Session", 20, null));
        var provider = new FakeProvider(_ => response);
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await coordinator.RefreshAsync(account.Id);
        Assert.Contains("Weekly limit", coordinator.GetStatus(account.Id).Message!);

        time.Advance(TimeSpan.FromSeconds(10));
        account.WidgetWindows = [new("missing-model", "Model limit")];
        coordinator.UpdateAccount(account);

        var status = coordinator.GetStatus(account.Id);
        AssertRetry(status, Now.AddSeconds(30));
        Assert.Contains("Model limit", status.Message!);
        Assert.DoesNotContain("Weekly limit", status.Message!);
        Assert.Equal(status.Message, status.Snapshot!.Message);
        Assert.Equal(response.FetchedAt, status.Snapshot.FetchedAt);
        Assert.Empty(coordinator.GetSnapshots());
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task SelectingAvailableLimitSchedulesImmediateFetchWithoutPromotingPartialSnapshot()
    {
        var account = Account();
        account.WidgetWindows = [new("missing", "Missing")];
        var response = Snapshot(account, new UsageWindow("session", "Session", 20, null));
        var provider = new FakeProvider(_ => response);
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: new ManualTime(Now));
        await coordinator.RefreshAsync(account.Id);
        Assert.True(coordinator.GetStatus(account.Id).IsRetryScheduled);

        account.WidgetWindows = [new("session", "Session")];
        coordinator.UpdateAccount(account);

        var status = coordinator.GetStatus(account.Id);
        AssertRetry(status, Now);
        Assert.Equal(UsageState.Unavailable, status.Snapshot!.State);
        Assert.Empty(coordinator.GetSnapshots());
        Assert.Equal(1, provider.Calls);

        await coordinator.RefreshDueAsync();

        status = coordinator.GetStatus(account.Id);
        Assert.Equal(UsageState.Ready, status.State);
        Assert.Equal(UsageState.Ready, status.Snapshot!.State);
        Assert.False(status.IsRetryScheduled);
        Assert.Null(status.Message);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task SelectingMissingLimitReevaluatesCurrentlyDisplayedSnapshot()
    {
        var account = Account();
        account.WidgetWindows = [new("session", "Session")];
        var response = Snapshot(account, new UsageWindow("session", "Session", 20, null));
        var provider = new FakeProvider(_ => response);
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: new ManualTime(Now));
        await coordinator.RefreshAsync(account.Id);

        account.WidgetWindows = [new("missing", "Missing")];
        coordinator.UpdateAccount(account);

        AssertRetry(coordinator.GetStatus(account.Id), Now.AddSeconds(30));
        Assert.Equal(1, provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityChangeClearsBothPartialDisplayAndCompleteCache(bool providerException)
    {
        var account = Account();
        account.WidgetWindows = [new("session", "Session"), new("weekly", "Weekly")];
        var previous = Snapshot(account, new UsageWindow("session", "Session", 20, null), new UsageWindow("weekly", "Weekly", 30, null));
        var partial = Snapshot(account, new UsageWindow("session", "Session", 25, null));
        var provider = new FakeProvider(_ => partial);
        await using var coordinator = new UsageCoordinator([provider], [account], [previous], new ManualTime(Now));
        await coordinator.RefreshAsync(account.Id);
        AssertRetry(coordinator.GetStatus(account.Id), Now.AddSeconds(30));

        provider.Fetch = _ => providerException
            ? throw new ProviderException(UsageState.SignInRequired, "Identity changed") { IdentityChanged = true }
            : partial with { IdentityKey = "different-account", State = UsageState.Unavailable };
        await coordinator.RefreshAsync(account.Id);

        var status = coordinator.GetStatus(account.Id);
        Assert.Equal(UsageState.SignInRequired, status.State);
        Assert.Null(status.Snapshot);
        Assert.False(status.IsRetryScheduled);
        Assert.Empty(coordinator.GetSnapshots());
    }

    [Fact]
    public async Task TransportFailureAfterPartialResultStillKeepsCompleteCache()
    {
        var account = Account();
        account.WidgetWindows = [new("session", "Session"), new("weekly", "Weekly")];
        var previous = Snapshot(account, new UsageWindow("session", "Session", 20, null), new UsageWindow("weekly", "Weekly", 30, null));
        var partial = Snapshot(account, new UsageWindow("session", "Session", 25, null));
        var provider = new FakeProvider(_ => partial);
        await using var coordinator = new UsageCoordinator([provider], [account], [previous], new ManualTime(Now));
        await coordinator.RefreshAsync(account.Id);
        var displayed = coordinator.GetStatus(account.Id).Snapshot;

        provider.Fetch = _ => throw new ProviderException(UsageState.Offline, "Offline");
        await coordinator.RefreshAsync(account.Id);

        var status = coordinator.GetStatus(account.Id);
        Assert.Equal(UsageState.Offline, status.State);
        Assert.True(status.IsRetryScheduled);
        Assert.Same(displayed, status.Snapshot);
        Assert.Same(previous, Assert.Single(coordinator.GetSnapshots()));
    }

    [Fact]
    public void CodexCreditsCannotMakeUnknownQuotaLookReady()
    {
        var account = Account(ProviderKind.Codex);
        var snapshot = CodexUsageParser.Parse("""
            {"rateLimits":{"primary":{"usedPercent":null,"resetsAt":null},
             "credits":{"hasCredits":true,"unlimited":false,"balance":"100"}}}
            """, account, Now);

        Assert.Equal(UsageState.Unavailable, snapshot.State);
        Assert.Null(Assert.Single(snapshot.Windows).UsedPercent);
        Assert.Equal(100m, Assert.Single(snapshot.Credits!).Balance);
    }

    [Fact]
    public async Task ParserUnavailableResultStillDisplaysFreshPartialDataAndSchedulesRetry()
    {
        var account = Account(ProviderKind.Codex);
        var previous = Snapshot(account, new UsageWindow("codex:primary", "Session", 42, null));
        var response = CodexUsageParser.Parse("""
            {"rateLimits":{"primary":{"usedPercent":null,"resetsAt":null},
             "credits":{"hasCredits":true,"unlimited":false,"balance":"100"}}}
            """, account, Now.AddMinutes(1));
        await using var coordinator = new UsageCoordinator([new FakeProvider(_ => response, ProviderKind.Codex)],
            [account], [previous], new ManualTime(Now));

        await coordinator.RefreshAsync(account.Id);

        var status = coordinator.GetStatus(account.Id);
        AssertRetry(status, Now.AddSeconds(30));
        Assert.Equal(response.FetchedAt, status.Snapshot!.FetchedAt);
        Assert.Null(Assert.Single(status.Snapshot.Windows).UsedPercent);
        Assert.Equal(100m, Assert.Single(status.Snapshot.Credits!).Balance);
        Assert.Same(previous, Assert.Single(coordinator.GetSnapshots()));
    }

    [Fact]
    public void ClaudeExtraUsageCannotMakeUnknownQuotaLookReady()
    {
        var account = Account();
        var snapshot = ClaudeUsageParser.Parse("""
            {"type":"assistant","usage_report":{"rate_limits":{
             "limits":[{"kind":"session","percent":null,"resets_at":null}],
             "extra_usage":{"is_enabled":true,"monthly_limit":10000,"used_credits":2500,"currency":"USD"}}}}
            """, account, Now);

        Assert.Equal(UsageState.Unavailable, snapshot.State);
        Assert.Null(Assert.Single(snapshot.Windows).UsedPercent);
        Assert.Equal(25m, Assert.Single(snapshot.Credits!).Used);
    }

    [Fact]
    public async Task PartialCodexQuotaRetriesWhenSelectedWeeklyPercentageIsMissing()
    {
        var account = Account(ProviderKind.Codex);
        account.WidgetWindows = [new("codex:primary", "Session"), new("codex:secondary", "Weekly")];
        var snapshot = CodexUsageParser.Parse("""
            {"rateLimits":{"primary":{"usedPercent":42,"windowDurationMins":300},
             "secondary":{"usedPercent":null,"windowDurationMins":10080},
             "credits":{"hasCredits":true,"unlimited":false,"balance":"100"}}}
            """, account, Now);
        var provider = new FakeProvider(_ => snapshot, ProviderKind.Codex);
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: new ManualTime(Now));

        await coordinator.RefreshAsync(account.Id);

        var status = coordinator.GetStatus(account.Id);
        AssertRetry(status, Now.AddSeconds(30));
        Assert.Equal(42, status.Snapshot!.Windows.Single(w => w.Id == "codex:primary").UsedPercent);
        Assert.Null(status.Snapshot.Windows.Single(w => w.Id == "codex:secondary").UsedPercent);
    }

    private static void AssertRetry(AccountUsageStatus status, DateTimeOffset due)
    {
        Assert.Equal(UsageState.Unavailable, status.State);
        Assert.False(status.IsRefreshing);
        Assert.True(status.IsRetryScheduled);
        Assert.Equal(due, status.NextAttemptAt);
        Assert.False(string.IsNullOrWhiteSpace(status.Message));
    }

    private static AccountSettings Account(ProviderKind provider = ProviderKind.Claude) => new()
    {
        Provider = provider,
        IdentityKey = "fixture-missing-usage"
    };

    private static UsageSnapshot Snapshot(AccountSettings account, params UsageWindow[] windows) =>
        new(account.Provider, account.Id, account.IdentityKey, "Pro", Now, windows);

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

    private sealed class FakeProvider(Func<AccountSettings, UsageSnapshot> fetch, ProviderKind kind = ProviderKind.Claude) : IUsageProvider
    {
        public Func<AccountSettings, UsageSnapshot> Fetch { get; set; } = fetch;
        public ProviderKind Kind => kind;
        public int Calls { get; private set; }
        public Task<UsageSnapshot> FetchAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Fetch(account));
        }
        public Task<ConnectionResult> ConnectAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task DisconnectAsync(AccountSettings account, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
