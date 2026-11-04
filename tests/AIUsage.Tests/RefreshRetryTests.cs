using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class RefreshRetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(UsageState.Offline, false)]
    [InlineData(UsageState.Offline, true)]
    [InlineData(UsageState.Unavailable, false)]
    [InlineData(UsageState.Unavailable, true)]
    [InlineData(UsageState.RateLimited, false)]
    [InlineData(UsageState.RateLimited, true)]
    public async Task FailedRefreshRecoversAutomaticallyRegardlessOfPeriodicRefreshSetting(UsageState failure, bool autoRefresh)
    {
        var time = new ManualTime(Now);
        var account = Account(autoRefresh);
        var previous = Snapshot(account, Now.AddMinutes(-10), 84);
        var attempt = 0;
        var provider = new FakeProvider((a, _) => ++attempt == 1
            ? throw new ProviderException(failure, "temporary failure")
            : Task.FromResult(Snapshot(a, time.GetUtcNow(), 86)));
        await using var coordinator = new UsageCoordinator([provider], [account], [previous], time);

        await coordinator.RefreshAsync(account.Id);
        var failed = coordinator.GetStatus(account.Id);
        Assert.True(failed.IsRetryScheduled);
        Assert.False(failed.IsRefreshing);
        Assert.Equal(Now.AddSeconds(30), failed.NextAttemptAt);
        Assert.Same(previous, failed.Snapshot);
        Assert.Equal(previous.FetchedAt, failed.Snapshot!.FetchedAt);
        Assert.Equal(84, failed.Snapshot.Windows[0].UsedPercent);

        time.Advance(TimeSpan.FromSeconds(29));
        await coordinator.RefreshDueAsync();
        Assert.Equal(1, provider.Calls);
        time.Advance(TimeSpan.FromSeconds(1));
        await coordinator.RefreshDueAsync();

        Assert.Equal(2, provider.Calls);
        var recovered = coordinator.GetStatus(account.Id);
        Assert.Equal(UsageState.Ready, recovered.State);
        Assert.False(recovered.IsRetryScheduled);
        Assert.Null(recovered.Message);
        Assert.Equal(86, recovered.Snapshot!.Windows[0].UsedPercent);
        Assert.Equal(time.GetUtcNow(), recovered.Snapshot.FetchedAt);
    }

    [Fact]
    public async Task RepeatedFailuresBackOffFromThirtySecondsAndStayAtFifteenMinutes()
    {
        var time = new ManualTime(Now);
        var account = Account(false);
        var provider = new FakeProvider((_, _) => throw new ProviderException(UsageState.Offline, "offline"));
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await coordinator.RefreshAsync(account.Id);

        int[] delaysInSeconds = [30, 60, 120, 240, 480, 900, 900, 900, 900, 900, 900, 900];
        for (var index = 0; index < delaysInSeconds.Length; index++)
        {
            var delay = TimeSpan.FromSeconds(delaysInSeconds[index]);
            var status = coordinator.GetStatus(account.Id);
            Assert.True(status.IsRetryScheduled);
            Assert.Equal(time.GetUtcNow().Add(delay), status.NextAttemptAt);
            time.Advance(delay - TimeSpan.FromMilliseconds(1));
            await coordinator.RefreshDueAsync();
            Assert.Equal(index + 1, provider.Calls);
            time.Advance(TimeSpan.FromMilliseconds(1));
            await coordinator.RefreshDueAsync();
            Assert.Equal(index + 2, provider.Calls);
        }
    }

    [Fact]
    public async Task SuccessfulRecoveryResetsBackoffForTheNextFailure()
    {
        var time = new ManualTime(Now);
        var account = Account(false);
        var attempt = 0;
        var provider = new FakeProvider((a, _) => ++attempt == 3
            ? Task.FromResult(Snapshot(a, time.GetUtcNow()))
            : throw new ProviderException(UsageState.Unavailable, "temporary failure"));
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await coordinator.RefreshAsync(account.Id);
        time.Advance(TimeSpan.FromSeconds(30));
        await coordinator.RefreshDueAsync();
        Assert.Equal(time.GetUtcNow().AddMinutes(1), coordinator.GetStatus(account.Id).NextAttemptAt);
        time.Advance(TimeSpan.FromMinutes(1));
        await coordinator.RefreshDueAsync();
        Assert.False(coordinator.GetStatus(account.Id).IsRetryScheduled);

        await coordinator.RefreshAsync(account.Id);
        Assert.True(coordinator.GetStatus(account.Id).IsRetryScheduled);
        Assert.Equal(time.GetUtcNow().AddSeconds(30), coordinator.GetStatus(account.Id).NextAttemptAt);
    }

    [Fact]
    public async Task RecoveryDoesNotEnablePeriodicRefreshWhenItWasDisabled()
    {
        var time = new ManualTime(Now);
        var account = Account(false);
        var attempt = 0;
        var provider = new FakeProvider((a, _) => ++attempt == 1
            ? throw new ProviderException(UsageState.Offline, "offline")
            : Task.FromResult(Snapshot(a, time.GetUtcNow())));
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await coordinator.RefreshAsync(account.Id);
        time.Advance(TimeSpan.FromSeconds(30));
        await coordinator.RefreshDueAsync();
        Assert.Equal(2, provider.Calls);
        Assert.False(account.AutoRefresh);
        Assert.False(coordinator.GetStatus(account.Id).IsRetryScheduled);

        time.Advance(TimeSpan.FromDays(2));
        await coordinator.RefreshDueAsync();
        await coordinator.RefreshAfterClockChangeAsync();
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task RecoveryRestoresTheAccountsConfiguredPeriodicInterval()
    {
        var time = new ManualTime(Now);
        var account = Account(true);
        account.RefreshMinutes = 17;
        var attempt = 0;
        var provider = new FakeProvider((a, _) => ++attempt == 1
            ? throw new ProviderException(UsageState.Offline, "offline")
            : Task.FromResult(Snapshot(a, time.GetUtcNow())));
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await coordinator.RefreshAsync(account.Id);
        time.Advance(TimeSpan.FromSeconds(30));
        await coordinator.RefreshDueAsync();
        Assert.Equal(time.GetUtcNow().AddMinutes(17), coordinator.GetStatus(account.Id).NextAttemptAt);
        time.Advance(TimeSpan.FromMinutes(16));
        await coordinator.RefreshDueAsync();
        Assert.Equal(2, provider.Calls);
        time.Advance(TimeSpan.FromMinutes(1));
        await coordinator.RefreshDueAsync();
        Assert.Equal(3, provider.Calls);
    }

    [Theory]
    [InlineData(UsageState.Offline)]
    [InlineData(UsageState.Unavailable)]
    [InlineData(UsageState.RateLimited)]
    public async Task ProviderRetryAfterBlocksBothManualAndAutomaticRequests(UsageState failure)
    {
        var time = new ManualTime(Now);
        var account = Account(false);
        var provider = new FakeProvider((_, _) => throw new ProviderException(failure, "retry later", TimeSpan.FromMinutes(7)));
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await coordinator.RefreshAsync(account.Id);
        Assert.True(coordinator.GetStatus(account.Id).IsRetryScheduled);
        Assert.Equal(Now.AddMinutes(7), coordinator.GetStatus(account.Id).NextAttemptAt);

        await coordinator.RefreshAsync(account.Id);
        time.Advance(TimeSpan.FromMinutes(7) - TimeSpan.FromMilliseconds(1));
        await coordinator.RefreshAllAsync();
        await coordinator.RefreshDueAsync();
        Assert.Equal(1, provider.Calls);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await coordinator.RefreshDueAsync();
        Assert.Equal(2, provider.Calls);
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(0)]
    public async Task NonPositiveRetryAfterStillWaitsAtLeastOneSecond(int seconds)
    {
        var time = new ManualTime(Now);
        var account = Account(false);
        var provider = new FakeProvider((_, _) => throw new ProviderException(UsageState.RateLimited, "retry later", TimeSpan.FromSeconds(seconds)));
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await coordinator.RefreshAsync(account.Id);
        Assert.Equal(Now.AddSeconds(1), coordinator.GetStatus(account.Id).NextAttemptAt);
        await coordinator.RefreshAsync(account.Id);
        await coordinator.RefreshDueAsync();
        Assert.Equal(1, provider.Calls);
        time.Advance(TimeSpan.FromSeconds(1));
        await coordinator.RefreshDueAsync();
        Assert.Equal(2, provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingAccountOptionsPreservesPendingRetry(bool providerDeadline)
    {
        var time = new ManualTime(Now);
        var account = Account(true);
        var previous = Snapshot(account, Now.AddHours(-1));
        var delay = providerDeadline ? TimeSpan.FromMinutes(7) : TimeSpan.FromSeconds(30);
        var provider = new FakeProvider((_, _) => throw new ProviderException(UsageState.Offline, "offline", providerDeadline ? delay : null));
        await using var coordinator = new UsageCoordinator([provider], [account], [previous], time);
        await coordinator.RefreshAsync(account.Id);
        account.RefreshMinutes = 1;
        account.AutoRefresh = false;
        account.Alias = "Renamed account";
        coordinator.UpdateAccount(account);
        Assert.True(coordinator.GetStatus(account.Id).IsRetryScheduled);
        Assert.Equal(Now.Add(delay), coordinator.GetStatus(account.Id).NextAttemptAt);

        await coordinator.RefreshDueAsync();
        Assert.Equal(1, provider.Calls);
        time.Advance(delay);
        await coordinator.RefreshDueAsync();
        Assert.Equal(2, provider.Calls);
    }

    [Theory]
    [InlineData(UsageState.SignInRequired)]
    [InlineData(UsageState.UpdateRequired)]
    public async Task ErrorsThatNeedUserActionDoNotScheduleRetries(UsageState failure)
    {
        var time = new ManualTime(Now);
        var account = Account(true);
        var provider = new FakeProvider((_, _) => throw new ProviderException(failure, "user action required"));
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await coordinator.RefreshAsync(account.Id);
        Assert.False(coordinator.GetStatus(account.Id).IsRetryScheduled);
        Assert.Null(coordinator.GetStatus(account.Id).NextAttemptAt);
        time.Advance(TimeSpan.FromDays(2));
        await coordinator.RefreshDueAsync();
        await coordinator.RefreshAfterClockChangeAsync();
        Assert.Equal(1, provider.Calls);
        await coordinator.RefreshAsync(account.Id);
        Assert.Equal(2, provider.Calls);
    }

    [Theory]
    [InlineData(UsageState.Offline)]
    [InlineData(UsageState.Unavailable)]
    [InlineData(UsageState.RateLimited)]
    public async Task LoginFailureDoesNotStartAutomaticUsageRetries(UsageState failure)
    {
        var time = new ManualTime(Now);
        var account = Account(true);
        var provider = new FakeProvider((a, _) => Task.FromResult(Snapshot(a, time.GetUtcNow())))
        {
            Connect = _ => throw new ProviderException(failure, "login failed")
        };
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await Assert.ThrowsAsync<ProviderException>(() => coordinator.ConnectAsync(account.Id, null, default));
        Assert.False(coordinator.GetStatus(account.Id).IsRetryScheduled);
        time.Advance(TimeSpan.FromDays(2));
        await coordinator.RefreshDueAsync();
        await coordinator.RefreshAfterClockChangeAsync();
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData(UsageState.Offline)]
    [InlineData(UsageState.Unavailable)]
    [InlineData(UsageState.RateLimited)]
    public async Task RefreshQueuedBehindLoginRespectsItsNewRetryAfter(UsageState failure)
    {
        var time = new ManualTime(Now);
        var account = Account(true);
        var loginEntered = Signal();
        var releaseLogin = Signal();
        var provider = new FakeProvider((a, _) => Task.FromResult(Snapshot(a, time.GetUtcNow())))
        {
            Connect = async token =>
            {
                loginEntered.TrySetResult();
                await releaseLogin.Task.WaitAsync(token);
                throw new ProviderException(failure, "login temporarily blocked", TimeSpan.FromMinutes(7));
            }
        };
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        var login = coordinator.ConnectAsync(account.Id, null, default);
        await loginEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // This refresh is accepted before login reports its provider cooldown.
        var queuedRefresh = coordinator.RefreshAsync(account.Id);
        releaseLogin.TrySetResult();
        await Assert.ThrowsAsync<ProviderException>(() => login);
        await queuedRefresh;
        Assert.Equal(0, provider.Calls);
        Assert.False(coordinator.GetStatus(account.Id).IsRetryScheduled);

        await coordinator.RefreshAsync(account.Id);
        Assert.Equal(0, provider.Calls);
        time.Advance(TimeSpan.FromMinutes(7));
        await coordinator.RefreshDueAsync();
        Assert.Equal(0, provider.Calls);
        await coordinator.RefreshAsync(account.Id);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(UsageState.Ready, coordinator.GetStatus(account.Id).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackwardsClockChangePreservesServerDeadlineAndBoundsLocalDelay(bool providerDeadline)
    {
        var time = new ManualTime(Now);
        var account = Account(true);
        var delay = providerDeadline ? TimeSpan.FromMinutes(7) : TimeSpan.FromSeconds(30);
        var provider = new FakeProvider((_, _) => throw new ProviderException(UsageState.Offline, "offline", providerDeadline ? delay : null));
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await coordinator.RefreshAsync(account.Id);
        time.Advance(TimeSpan.FromHours(-2));
        await coordinator.RefreshAfterClockChangeAsync();
        Assert.Equal(1, provider.Calls);
        Assert.True(coordinator.GetStatus(account.Id).IsRetryScheduled);
        var expectedDeadline = providerDeadline ? Now.Add(delay) : time.GetUtcNow().Add(delay);
        Assert.Equal(expectedDeadline, coordinator.GetStatus(account.Id).NextAttemptAt);
        time.Advance(expectedDeadline - time.GetUtcNow() - TimeSpan.FromMilliseconds(1));
        await coordinator.RefreshDueAsync();
        Assert.Equal(1, provider.Calls);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await coordinator.RefreshAfterClockChangeAsync();
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task ResumeAfterRetryDeadlineRefreshesImmediatelyEvenWhenPeriodicRefreshIsOff()
    {
        var time = new ManualTime(Now);
        var account = Account(false);
        var provider = new FakeProvider((_, _) => throw new ProviderException(UsageState.Offline, "offline"));
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await coordinator.RefreshAsync(account.Id);
        time.Advance(TimeSpan.FromHours(8));
        await coordinator.RefreshDueAsync();
        Assert.Equal(2, provider.Calls);
        Assert.Equal(time.GetUtcNow().AddMinutes(1), coordinator.GetStatus(account.Id).NextAttemptAt);
        await coordinator.RefreshDueAsync();
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task AutomaticRetryAndManualRefreshShareTheSameFetch()
    {
        var time = new ManualTime(Now);
        var account = Account(false);
        var entered = Signal();
        var release = Signal();
        var attempt = 0;
        var provider = new FakeProvider(async (a, token) =>
        {
            if (Interlocked.Increment(ref attempt) == 1) throw new ProviderException(UsageState.Offline, "offline");
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Snapshot(a, time.GetUtcNow());
        });
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await coordinator.RefreshAsync(account.Id);
        time.Advance(TimeSpan.FromSeconds(30));
        var automatic = coordinator.RefreshDueAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manual = coordinator.RefreshAsync(account.Id);
        var anotherAutomatic = coordinator.RefreshDueAsync();
        Assert.Equal(2, provider.Calls);
        release.TrySetResult();
        await Task.WhenAll(automatic, manual, anotherAutomatic);
        Assert.Equal(2, provider.Calls);
        Assert.False(coordinator.GetStatus(account.Id).IsRetryScheduled);
    }

    [Fact]
    public async Task RetryingSeveralAccountsStillLimitsConcurrentFetchesToTwo()
    {
        var time = new ManualTime(Now);
        var accounts = Enumerable.Range(0, 5).Select(_ => Account(false)).ToArray();
        var fail = true;
        var enteredTwo = Signal();
        var release = Signal();
        var counters = new object();
        var active = 0;
        var maximum = 0;
        var provider = new FakeProvider(async (a, token) =>
        {
            if (fail) throw new ProviderException(UsageState.Offline, "offline");
            lock (counters)
            {
                active++;
                maximum = Math.Max(maximum, active);
                if (active == 2) enteredTwo.TrySetResult();
            }
            try { await release.Task.WaitAsync(token); return Snapshot(a, time.GetUtcNow()); }
            finally { lock (counters) active--; }
        });
        await using var coordinator = new UsageCoordinator([provider], accounts, timeProvider: time);
        await coordinator.RefreshAllAsync();
        Assert.Equal(5, provider.Calls);
        fail = false;
        time.Advance(TimeSpan.FromSeconds(30));
        var retries = coordinator.RefreshDueAsync();
        await enteredTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(7, provider.Calls);
        release.TrySetResult();
        await retries;
        Assert.Equal(10, provider.Calls);
        Assert.Equal(2, maximum);
        Assert.All(accounts, a => Assert.False(coordinator.GetStatus(a.Id).IsRetryScheduled));
    }

    [Fact]
    public async Task RemovedAccountDoesNotRetry()
    {
        var time = new ManualTime(Now);
        var account = Account(false);
        var provider = new FakeProvider((_, _) => throw new ProviderException(UsageState.Offline, "offline"));
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await coordinator.RefreshAsync(account.Id);
        await coordinator.RemoveAccountAsync(account.Id, default);
        time.Advance(TimeSpan.FromMinutes(1));
        await coordinator.RefreshDueAsync();
        await coordinator.RefreshAsync(account.Id);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task ShutdownCancelsAnActiveRetryAndPreventsLaterRetries()
    {
        var time = new ManualTime(Now);
        var account = Account(false);
        var entered = Signal();
        var cancelled = Signal();
        var attempt = 0;
        var provider = new FakeProvider(async (a, token) =>
        {
            if (Interlocked.Increment(ref attempt) == 1) throw new ProviderException(UsageState.Offline, "offline");
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); }
            return Snapshot(a, time.GetUtcNow());
        });
        var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        try
        {
            await coordinator.RefreshAsync(account.Id);
            time.Advance(TimeSpan.FromSeconds(30));
            var retry = coordinator.RefreshDueAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await coordinator.DisposeAsync();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await retry;
            time.Advance(TimeSpan.FromDays(1));
            await coordinator.RefreshDueAsync();
            await coordinator.RefreshAsync(account.Id);
            Assert.Equal(2, provider.Calls);
        }
        finally { await coordinator.DisposeAsync(); }
    }

    [Fact]
    public async Task UnexpectedTransportFailureAlsoSchedulesRecovery()
    {
        var time = new ManualTime(Now);
        var account = Account(false);
        var provider = new FakeProvider((_, _) => throw new IOException("connection interrupted"));
        await using var coordinator = new UsageCoordinator([provider], [account], timeProvider: time);
        await coordinator.RefreshAsync(account.Id);
        var status = coordinator.GetStatus(account.Id);
        Assert.Equal(UsageState.Offline, status.State);
        Assert.True(status.IsRetryScheduled);
        Assert.Equal(Now.AddSeconds(30), status.NextAttemptAt);
        time.Advance(TimeSpan.FromSeconds(30));
        await coordinator.RefreshDueAsync();
        Assert.Equal(2, provider.Calls);
    }

    private static AccountSettings Account(bool autoRefresh) => new()
    {
        Provider = ProviderKind.Claude,
        IdentityKey = "test-account",
        AutoRefresh = autoRefresh,
        RefreshMinutes = 1440
    };

    private static UsageSnapshot Snapshot(AccountSettings account, DateTimeOffset fetchedAt, double percent = 42)
        => new(account.Provider, account.Id, account.IdentityKey, "Pro", fetchedAt,
            [new UsageWindow("session", "Session", percent, fetchedAt.AddHours(3))]);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        public Task<ConnectionResult> ConnectAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken token)
            => Connect?.Invoke(token) ?? Task.FromResult(new ConnectionResult(account.IdentityKey!, null, "Pro"));
        public Task<UsageSnapshot> FetchAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            return fetch(account, token);
        }
        public Task DisconnectAsync(AccountSettings account, CancellationToken token) => Task.CompletedTask;
    }
}
