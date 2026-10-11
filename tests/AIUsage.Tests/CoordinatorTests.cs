using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class CoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private static AccountSettings Account(string identity = "one") => new() { Provider = ProviderKind.Claude, IdentityKey = identity };
    private static UsageSnapshot Snapshot(AccountSettings account, double percentage = 42, DateTimeOffset? reset = null)
        => new(account.Provider, account.Id, account.IdentityKey, "Pro", Now, [new("session", "5시간", percentage, reset ?? Now.AddHours(3))]);

    [Fact]
    public async Task ConcurrentManualAndAutomaticRequestsShareOneFetch()
    {
        var account = Account();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(async (a, _) => { await release.Task; return Snapshot(a); });
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: new ManualTime(Now));
        var first = coordinator.RefreshAsync(account.Id);
        var second = coordinator.RefreshAsync(account.Id);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, provider.Calls);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(42, coordinator.GetStatus(account.Id).Snapshot!.Windows[0].UsedPercent);
    }

    [Fact]
    public async Task GlobalConcurrencyNeverExceedsTwo()
    {
        var accounts = Enumerable.Range(0, 5).Select(i => Account(i.ToString())).ToArray();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var maximum = 0;
        var provider = new FakeProvider(async (a, _) =>
        {
            var running = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, running);
            if (running == 2) twoEntered.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref active);
            return Snapshot(a);
        });
        await using var coordinator = new UsageCoordinator([provider], accounts);
        var all = coordinator.RefreshAllAsync();
        await twoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, provider.Calls);
        release.SetResult();
        await all;
        Assert.Equal(2, maximum);
        Assert.Equal(5, provider.Calls);
    }

    [Fact]
    public async Task FailurePreservesLastGoodValueAndTimestamp()
    {
        var account = Account(); var previous = Snapshot(account);
        var provider = new FakeProvider((_, _) => throw new ProviderException(UsageState.Offline, "offline"));
        await using var coordinator = new UsageCoordinator([provider], [account], [previous]);
        await coordinator.RefreshAsync(account.Id);
        var status = coordinator.GetStatus(account.Id);
        Assert.Same(previous, status.Snapshot);
        Assert.Equal(UsageState.Offline, status.State);
        Assert.False(status.IsRefreshing);
    }

    [Fact]
    public async Task IdentityChangeClearsOldAccountValues()
    {
        var account = Account();
        var provider = new FakeProvider((a, _) => Task.FromResult(Snapshot(a) with { IdentityKey = "another" }));
        await using var coordinator = new UsageCoordinator([provider], [account], [Snapshot(account)]);
        await coordinator.RefreshAsync(account.Id);
        Assert.Null(coordinator.GetStatus(account.Id).Snapshot);
        Assert.Equal(UsageState.SignInRequired, coordinator.GetStatus(account.Id).State);
    }

    [Fact]
    public async Task ProviderDetectedIdentityChangeAlsoClearsCache()
    {
        var account = Account();
        var provider = new FakeProvider((_, _) => throw new ProviderException(UsageState.SignInRequired, "changed") { IdentityChanged = true });
        await using var coordinator = new UsageCoordinator([provider], [account], [Snapshot(account)]);
        await coordinator.RefreshAsync(account.Id);
        Assert.Null(coordinator.GetStatus(account.Id).Snapshot);
    }

    [Fact]
    public async Task RateLimitBlocksManualRefreshUntilRetryAfter()
    {
        var time = new ManualTime(Now); var account = Account();
        var provider = new FakeProvider((_, _) => throw new ProviderException(UsageState.RateLimited, "429", TimeSpan.FromMinutes(7)));
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await coordinator.RefreshAsync(account.Id);
        await coordinator.RefreshAsync(account.Id);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(Now.AddMinutes(7), coordinator.GetStatus(account.Id).NextAttemptAt);
        time.Advance(TimeSpan.FromMinutes(7));
        await coordinator.RefreshAsync(account.Id);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task DisabledAutomaticRefreshStillAllowsManualRefresh()
    {
        var account = Account(); account.AutoRefresh = false;
        var provider = new FakeProvider((a, _) => Task.FromResult(Snapshot(a)));
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: new ManualTime(Now));
        await coordinator.RefreshDueAsync();
        Assert.Equal(0, provider.Calls);
        await coordinator.RefreshAsync(account.Id);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task BackwardsClockChangeResumesPollingWithoutBypassingRateLimit()
    {
        var time = new ManualTime(Now); var account = Account();
        var provider = new FakeProvider((a, _) => Task.FromResult(Snapshot(a)));
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await coordinator.RefreshAsync(account.Id);
        time.Advance(TimeSpan.FromHours(-2));
        await coordinator.RefreshAfterClockChangeAsync();
        Assert.Equal(2, provider.Calls);

        var limited = new FakeProvider((_, _) => throw new ProviderException(UsageState.RateLimited, "429", TimeSpan.FromMinutes(7)));
        await using var rateLimited = new UsageCoordinator([limited], [account], timeProvider: time);
        await rateLimited.RefreshAsync(account.Id);
        time.Advance(TimeSpan.FromHours(-2));
        await rateLimited.RefreshAfterClockChangeAsync();
        Assert.Equal(1, limited.Calls);
    }

    [Fact]
    public async Task EachAccountKeepsItsOwnInterval()
    {
        var time = new ManualTime(Now); var one = Account("one"); var two = Account("two"); two.RefreshMinutes = 10;
        var provider = new FakeProvider((a, _) => Task.FromResult(Snapshot(a)));
        await using var coordinator = new UsageCoordinator([provider], [one, two], timeProvider: time);
        await coordinator.RefreshDueAsync();
        Assert.Equal(2, provider.Calls);
        time.Advance(TimeSpan.FromMinutes(5));
        await coordinator.RefreshDueAsync();
        Assert.Equal(3, provider.Calls);
        Assert.Equal(Now.AddMinutes(10), coordinator.GetStatus(two.Id).NextAttemptAt);
    }

    [Fact]
    public async Task PassedResetTriggersOnceWithoutInventingZero()
    {
        var time = new ManualTime(Now); var account = Account(); account.RefreshMinutes = 60;
        var snapshot = Snapshot(account, 91, Now.AddMinutes(1));
        var provider = new FakeProvider((_, _) => Task.FromResult(snapshot));
        await using var coordinator = new UsageCoordinator([provider], [account], [snapshot], time);
        time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(6));
        await coordinator.RefreshDueAsync(); await coordinator.RefreshDueAsync();
        Assert.Equal(1, provider.Calls);
        Assert.Equal(91, coordinator.GetStatus(account.Id).Snapshot!.Windows[0].UsedPercent);
    }

    [Fact]
    public async Task CacheForWrongIdentityIsIgnored()
    {
        var account = Account();
        await using var coordinator = new UsageCoordinator([], [account], [Snapshot(account) with { IdentityKey = "wrong" }]);
        Assert.Null(coordinator.GetStatus(account.Id).Snapshot);
    }

    [Fact]
    public async Task DisconnectDoesNotRemoveOtherAccount()
    {
        var one = Account(); var two = Account("two"); var provider = new FakeProvider((a, _) => Task.FromResult(Snapshot(a)));
        await using var coordinator = new UsageCoordinator([provider], [one, two]);
        await coordinator.RemoveAccountAsync(one.Id, default);
        Assert.Equal(two.Id, coordinator.GetStatus(two.Id).AccountId);
        Assert.Throws<KeyNotFoundException>(() => coordinator.GetStatus(one.Id));
    }

    [Fact]
    public async Task StaleRefreshAfterRemovalDoesNotInterruptOtherAccounts()
    {
        var one = Account(); var two = Account("two");
        var provider = new FakeProvider((a, _) => Task.FromResult(Snapshot(a)));
        await using var coordinator = new UsageCoordinator([provider], [one, two]);
        await coordinator.RemoveAccountAsync(one.Id, default);
        await coordinator.RefreshAsync(one.Id);
        await coordinator.RefreshDueAsync();
        Assert.Equal(1, provider.Calls);
        Assert.NotNull(coordinator.GetStatus(two.Id).Snapshot);
    }

    [Fact]
    public async Task ShutdownCancelsLoginAndWaitsForItsCleanup()
    {
        var account = Account();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider((a, _) => Task.FromResult(Snapshot(a)))
        {
            Connect = async token =>
            {
                entered.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { cleanupStarted.SetResult(); await cleanupFinished.Task; }
                return new ConnectionResult("one", null, null);
            }
        };
        var coordinator = new UsageCoordinator([provider], [account]);
        var login = coordinator.ConnectAsync(account.Id, null, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposing = coordinator.DisposeAsync().AsTask();
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(disposing.IsCompleted);
        cleanupFinished.SetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        Assert.Equal(0, provider.Calls);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task OnlyOneAntigravityAccountCanBeAdded()
    {
        await using var coordinator = new UsageCoordinator([], []);
        coordinator.AddAccount(new() { Provider = ProviderKind.Antigravity });
        Assert.Throws<InvalidOperationException>(() => coordinator.AddAccount(new() { Provider = ProviderKind.Antigravity }));
    }

    [Theory]
    [InlineData(-1, "초기화 확인 중")]
    [InlineData(0, "초기화 확인 중")]
    [InlineData(30, "30초")]
    [InlineData(3661, "1시간 1분")]
    [InlineData(90000, "1일 1시간")]
    public void CountdownUsesAbsoluteServerTime(int seconds, string expected)
        => Assert.Equal(expected, UsageFormatting.Remaining(Now.AddSeconds(seconds), Now));

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
    private sealed class FakeProvider(Func<AccountSettings, CancellationToken, Task<UsageSnapshot>> fetch) : IUsageProvider
    {
        public ProviderKind Kind => ProviderKind.Claude;
        public int Calls;
        public Func<CancellationToken, Task<ConnectionResult>>? Connect { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ConnectionResult> ConnectAsync(AccountSettings a, IProgress<ProviderProgress>? p, CancellationToken c) => Connect?.Invoke(c) ?? Task.FromResult(new ConnectionResult(a.IdentityKey!, "test@example.test", "Pro"));
        public Task<UsageSnapshot> FetchAsync(AccountSettings a, IProgress<ProviderProgress>? p, CancellationToken c)
        {
            Interlocked.Increment(ref Calls); Entered.TrySetResult(); return fetch(a, c);
        }
        public Task DisconnectAsync(AccountSettings a, CancellationToken c) => Task.CompletedTask;
    }
}
