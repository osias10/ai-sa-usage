using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class RetainedRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PendingRefreshKeepsExistingValuesAndTimestampUntilResponseArrives()
    {
        var account = Account();
        var previous = Snapshot(account, 12, 40) with { FetchedAt = Now.AddMinutes(-2) };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<UsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(async token =>
        {
            entered.TrySetResult();
            return await response.Task.WaitAsync(token);
        });
        await using var coordinator = new UsageCoordinator([provider], [account], [previous], new ManualTime(Now));

        var refresh = coordinator.RefreshAsync(account.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var pending = coordinator.GetStatus(account.Id);
        Assert.True(pending.IsRefreshing);
        Assert.Same(previous, pending.DisplaySnapshot);
        Assert.Equal(Now.AddMinutes(-2), pending.DisplaySnapshot!.FetchedAt);
        Assert.Equal(new double?[] { 12, 40 }, pending.DisplaySnapshot.Windows.Select(window => window.UsedPercent));

        var next = Snapshot(account, 15, 43);
        response.TrySetResult(next);
        await refresh;

        Assert.False(coordinator.GetStatus(account.Id).IsRefreshing);
        Assert.Same(next, coordinator.GetStatus(account.Id).DisplaySnapshot);
    }

    [Fact]
    public async Task PartialResponseAndFailedRetryKeepOneCompleteDisplayUntilRecovery()
    {
        var account = Account();
        account.AutoRefresh = false;
        var previous = Snapshot(account, 12, 40) with
        {
            FetchedAt = Now.AddMinutes(-2), Credits = [new("credit", "Credits", "credits", Balance: 5)]
        };
        var partial = Snapshot(account, 25, 60) with
        {
            Windows = [new("weekly", "Weekly", 60, Now.AddDays(2))],
            Credits = [new("credit", "Credits", "credits", Balance: 15)]
        };
        var provider = new FakeProvider(_ => Task.FromResult(partial));
        var time = new ManualTime(Now);
        await using var coordinator = new UsageCoordinator([provider], [account], [previous], time);

        await coordinator.RefreshAsync(account.Id);

        var failed = coordinator.GetStatus(account.Id);
        Assert.Equal(UsageState.Unavailable, failed.State);
        Assert.True(failed.IsPartialResponse);
        Assert.True(failed.IsRetryScheduled);
        Assert.Same(previous, failed.RetainedSnapshot);
        Assert.Same(previous, failed.DisplaySnapshot);
        Assert.Equal(60, Assert.Single(failed.Snapshot!.Windows).UsedPercent);
        Assert.Equal(5m, Assert.Single(failed.DisplaySnapshot!.Credits!).Balance);
        Assert.Equal(Now.AddMinutes(-2), failed.DisplaySnapshot.FetchedAt);
        Assert.Equal(UsageState.Unavailable, failed.Snapshot.State);
        Assert.Same(previous, Assert.Single(coordinator.GetSnapshots()));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = new TaskCompletionSource<UsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Fetch = async token =>
        {
            entered.TrySetResult();
            return await retry.Task.WaitAsync(token);
        };
        time.Advance(TimeSpan.FromSeconds(30));
        var refresh = coordinator.RefreshDueAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.GetStatus(account.Id).IsRefreshing);
        Assert.Same(previous, coordinator.GetStatus(account.Id).DisplaySnapshot);

        retry.TrySetException(new ProviderException(UsageState.Offline, "offline"));
        await refresh;

        var offline = coordinator.GetStatus(account.Id);
        Assert.Equal(UsageState.Offline, offline.State);
        Assert.False(offline.IsPartialResponse);
        Assert.Same(previous, offline.DisplaySnapshot);
        Assert.Same(previous, offline.RetainedSnapshot);
        Assert.Equal(Now.AddSeconds(90), offline.NextAttemptAt);

        var recovered = Snapshot(account, 26, 61) with { FetchedAt = Now.AddSeconds(90) };
        provider.Fetch = _ => Task.FromResult(recovered);
        time.Advance(TimeSpan.FromSeconds(60));
        await coordinator.RefreshDueAsync();

        var ready = coordinator.GetStatus(account.Id);
        Assert.Equal(UsageState.Ready, ready.State);
        Assert.Null(ready.RetainedSnapshot);
        Assert.Same(recovered, ready.DisplaySnapshot);
        Assert.False(ready.IsRetryScheduled);
        Assert.Same(recovered, Assert.Single(coordinator.GetSnapshots()));
    }

    [Fact]
    public async Task NoCompleteCacheStillDisplaysAvailablePartialData()
    {
        var account = Account();
        var partial = Snapshot(account, 25, 60) with { Windows = [new("weekly", "Weekly", 60, null)] };
        await using var coordinator = new UsageCoordinator([new FakeProvider(_ => Task.FromResult(partial))], [account], timeProvider: new ManualTime(Now));

        await coordinator.RefreshAsync(account.Id);

        var status = coordinator.GetStatus(account.Id);
        Assert.Null(status.RetainedSnapshot);
        Assert.Same(status.Snapshot, status.DisplaySnapshot);
        Assert.Equal(60, Assert.Single(status.DisplaySnapshot!.Windows).UsedPercent);
        Assert.True(status.IsPartialResponse);
        Assert.True(status.IsRetryScheduled);
        Assert.Empty(coordinator.GetSnapshots());
    }

    [Fact]
    public async Task SettingsChangesInspectRawPartialResponseAndDoNotMistakeRetainedValuesForRecovery()
    {
        var account = Account();
        var previous = Snapshot(account, 12, 40);
        var partial = Snapshot(account, 25, 60) with { Windows = [new("weekly", "Weekly", 60, null)] };
        var time = new ManualTime(Now);
        await using var coordinator = new UsageCoordinator([new FakeProvider(_ => Task.FromResult(partial))], [account], [previous], time);
        await coordinator.RefreshAsync(account.Id);

        time.Advance(TimeSpan.FromSeconds(10));
        account.Alias = "Changed alias";
        coordinator.UpdateAccount(account);

        var failed = coordinator.GetStatus(account.Id);
        Assert.Equal(UsageState.Unavailable, failed.State);
        Assert.Equal(Now.AddSeconds(30), failed.NextAttemptAt);
        Assert.Same(previous, failed.DisplaySnapshot);
        Assert.Contains("Session", failed.Message!);

        account.WidgetWindows = [new("weekly", "Weekly")];
        coordinator.UpdateAccount(account);
        Assert.Equal(UsageState.Unavailable, coordinator.GetStatus(account.Id).State);
        Assert.Equal(Now.AddSeconds(10), coordinator.GetStatus(account.Id).NextAttemptAt);
        Assert.Same(previous, coordinator.GetStatus(account.Id).DisplaySnapshot);

        await coordinator.RefreshDueAsync();
        Assert.Equal(UsageState.Ready, coordinator.GetStatus(account.Id).State);
        Assert.Null(coordinator.GetStatus(account.Id).RetainedSnapshot);
        Assert.Equal(60, Assert.Single(coordinator.GetStatus(account.Id).DisplaySnapshot!.Windows).UsedPercent);
    }

    [Fact]
    public async Task IdentityMismatchClearsRetainedDisplayAndCompleteCache()
    {
        var account = Account();
        var previous = Snapshot(account, 12, 40);
        var partial = Snapshot(account, 25, 60) with { Windows = [new("weekly", "Weekly", 60, null)] };
        var provider = new FakeProvider(_ => Task.FromResult(partial));
        await using var coordinator = new UsageCoordinator([provider], [account], [previous], new ManualTime(Now));
        await coordinator.RefreshAsync(account.Id);
        Assert.Same(previous, coordinator.GetStatus(account.Id).DisplaySnapshot);

        provider.Fetch = _ => Task.FromResult(partial with { IdentityKey = "different-account", State = UsageState.Unavailable });
        await coordinator.RefreshAsync(account.Id);

        var mismatch = coordinator.GetStatus(account.Id);
        Assert.Equal(UsageState.SignInRequired, mismatch.State);
        Assert.Null(mismatch.Snapshot);
        Assert.Null(mismatch.RetainedSnapshot);
        Assert.Null(mismatch.DisplaySnapshot);
        Assert.Empty(coordinator.GetSnapshots());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public async Task ValidUsageReplacesRetainedNumbersIncludingZeroAndAbsentReset(double usage)
    {
        var account = Account();
        var previous = Snapshot(account, 12, 40);
        var provider = new FakeProvider(_ => Task.FromResult(Snapshot(account, 25, 60) with { Windows = [] }));
        await using var coordinator = new UsageCoordinator([provider], [account], [previous], new ManualTime(Now));
        await coordinator.RefreshAsync(account.Id);
        Assert.Same(previous, coordinator.GetStatus(account.Id).DisplaySnapshot);

        var recovered = Snapshot(account, usage, usage) with
        {
            Windows = [new("session", "Session", usage, null), new("weekly", "Weekly", usage, null)]
        };
        provider.Fetch = _ => Task.FromResult(recovered);
        await coordinator.RefreshAsync(account.Id);

        Assert.Equal(UsageState.Ready, coordinator.GetStatus(account.Id).State);
        Assert.Null(coordinator.GetStatus(account.Id).RetainedSnapshot);
        Assert.Same(recovered, coordinator.GetStatus(account.Id).DisplaySnapshot);
    }

    private static AccountSettings Account() => new()
    {
        Provider = ProviderKind.Claude, IdentityKey = "retained-refresh-fixture",
        WidgetWindows = [new("session", "Session"), new("weekly", "Weekly")]
    };

    private static UsageSnapshot Snapshot(AccountSettings account, double session, double weekly) => new(
        account.Provider, account.Id, account.IdentityKey, "Pro", Now,
        [new("session", "Session", session, Now.AddHours(2)), new("weekly", "Weekly", weekly, Now.AddDays(2))]);

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class FakeProvider(Func<CancellationToken, Task<UsageSnapshot>> fetch) : IUsageProvider
    {
        public ProviderKind Kind => ProviderKind.Claude;
        public Func<CancellationToken, Task<UsageSnapshot>> Fetch { get; set; } = fetch;
        public Task<UsageSnapshot> FetchAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken token) => Fetch(token);
        public Task<ConnectionResult> ConnectAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken token) => throw new NotSupportedException();
        public Task DisconnectAsync(AccountSettings account, CancellationToken token) => Task.CompletedTask;
    }
}
