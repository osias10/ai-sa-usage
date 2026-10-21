using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class ResetMessageCoordinatorTests
{
    private static AccountSettings Account(string identity = "identity") => new()
    {
        Provider = ProviderKind.Claude, IdentityKey = identity, ResetMessageEnabled = true
    };
    private static UsageSnapshot Snapshot(AccountSettings account) => new(account.Provider, account.Id,
        account.IdentityKey, "Pro", DateTimeOffset.UtcNow, [new("session", "세션", 0, null)]);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(false, "identity", UsageState.Ready)]
    [InlineData(true, "changed", UsageState.Ready)]
    [InlineData(true, "identity", UsageState.SignInRequired)]
    [InlineData(true, "identity", UsageState.RateLimited)]
    [InlineData(true, "identity", UsageState.Offline)]
    public async Task DisabledWrongIdentityOrUnreadyAccountsNeverReserveOrSend(bool enabled, string expectedIdentity, UsageState state)
    {
        var account = Account();
        account.ResetMessageEnabled = enabled;
        var provider = new MessageProvider();
        await using var coordinator = new UsageCoordinator([provider], [account], [Snapshot(account) with { State = state }]);
        var reservations = 0;
        var sent = await coordinator.SendResetMessageAsync(account.Id, expectedIdentity, (_, _) =>
        {
            reservations++;
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.False(sent);
        Assert.Equal(0, reservations);
        Assert.Equal(0, provider.SendCalls);
    }

    [Fact]
    public async Task MissingSnapshotOrRemovedAccountNeverSends()
    {
        var account = Account();
        var provider = new MessageProvider();
        await using var coordinator = new UsageCoordinator([provider], [account]);
        Assert.False(await coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, MustNotReserve, CancellationToken.None));
        await coordinator.RemoveAccountAsync(account.Id, CancellationToken.None);
        Assert.False(await coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, MustNotReserve, CancellationToken.None));
        Assert.Equal(0, provider.SendCalls);

        static Task<bool> MustNotReserve(AccountUsageStatus _, CancellationToken __) => throw new InvalidOperationException("Unready account reserved a send.");
    }

    [Fact]
    public async Task RefusedReservationDoesNotGenerate()
    {
        var account = Account();
        var provider = new MessageProvider();
        await using var coordinator = new UsageCoordinator([provider], [account], [Snapshot(account)]);
        Assert.False(await coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, (_, _) => Task.FromResult(false), CancellationToken.None));
        Assert.Equal(0, provider.SendCalls);
    }

    [Fact]
    public async Task FailedReservationPersistenceDoesNotGenerate()
    {
        var account = Account();
        var provider = new MessageProvider();
        await using var coordinator = new UsageCoordinator([provider], [account], [Snapshot(account)]);
        await Assert.ThrowsAsync<IOException>(() => coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!,
            (_, _) => throw new IOException("disk full"), CancellationToken.None));
        Assert.Equal(0, provider.SendCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EligibilityIsRecheckedAfterReservation(bool disable)
    {
        var account = Account();
        var provider = new MessageProvider();
        await using var coordinator = new UsageCoordinator([provider], [account], [Snapshot(account)]);
        Assert.False(await coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, (_, _) =>
        {
            if (disable) account.ResetMessageEnabled = false;
            else account.IdentityKey = "changed-after-reservation";
            return Task.FromResult(true);
        }, CancellationToken.None));
        Assert.Equal(0, provider.SendCalls);
    }

    [Fact]
    public async Task SendWaitsForAnInProgressUsageReadOfTheSameAccount()
    {
        var account = Account();
        var entered = Signal();
        var release = Signal();
        var provider = new MessageProvider
        {
            Fetch = async (value, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return Snapshot(value);
            }
        };
        await using var coordinator = new UsageCoordinator([provider], [account], [Snapshot(account)]);
        var reading = coordinator.RefreshAsync(account.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reserved = false;
        var sending = coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, (status, _) =>
        {
            reserved = true;
            Assert.False(status.IsRefreshing);
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.False(sending.IsCompleted);
        Assert.False(reserved);
        Assert.Equal(0, provider.SendCalls);
        release.TrySetResult();
        await reading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await sending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, provider.SendCalls);
    }

    [Fact]
    public async Task ConcurrentRequestsSerializeReservationsSoOnlyOneCycleIsSent()
    {
        var account = Account();
        var entered = Signal();
        var release = Signal();
        var provider = new MessageProvider { Send = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        await using var coordinator = new UsageCoordinator([provider], [account], [Snapshot(account)]);
        var reservations = 0;
        Task<bool> Reserve(AccountUsageStatus _, CancellationToken __) => Task.FromResult(Interlocked.Increment(ref reservations) == 1);
        var first = coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, Reserve, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, Reserve, CancellationToken.None);
        Assert.Equal(1, reservations);
        release.TrySetResult();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, reservations);
        Assert.Equal(1, provider.SendCalls);
    }

    [Fact]
    public async Task GlobalProcessLimitIncludesMessagesAndUsageReads()
    {
        var accounts = Enumerable.Range(0, 4).Select(value => Account($"identity:{value}")).ToArray();
        var twoEntered = Signal();
        var release = Signal();
        var active = 0;
        var maximum = 0;
        var sync = new object();
        async Task Work(CancellationToken token)
        {
            lock (sync)
            {
                maximum = Math.Max(maximum, ++active);
                if (active == 2) twoEntered.TrySetResult();
            }
            try { await release.Task.WaitAsync(token); }
            finally { lock (sync) active--; }
        }
        var provider = new MessageProvider
        {
            Fetch = async (account, token) => { await Work(token); return Snapshot(account); },
            Send = (_, token) => Work(token)
        };
        await using var coordinator = new UsageCoordinator([provider], accounts, accounts.Select(Snapshot));
        var read = coordinator.RefreshAsync(accounts[0].Id);
        var sends = accounts.Skip(1).Select(account => coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!,
            (_, _) => Task.FromResult(true), CancellationToken.None)).ToArray();
        await twoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (sync) Assert.Equal(2, active);
        release.TrySetResult();
        await Task.WhenAll(sends.Cast<Task>().Append(read)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(sends, sending => Assert.True(sending.Result));
        Assert.Equal(2, maximum);
        Assert.Equal(3, provider.SendCalls);
    }

    [Fact]
    public async Task ShutdownCancelsAndAwaitsTheOwnedGeneration()
    {
        var account = Account();
        var entered = Signal();
        var stopped = Signal();
        var provider = new MessageProvider
        {
            Send = async (_, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { stopped.TrySetResult(); }
            }
        };
        await using var coordinator = new UsageCoordinator([provider], [account], [Snapshot(account)]);
        var sending = coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, (_, _) => Task.FromResult(true), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stopped.Task.IsCompletedSuccessfully);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.False(await coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, (_, _) => throw new InvalidOperationException(), CancellationToken.None));
        Assert.Equal(1, provider.SendCalls);
    }

    private sealed class MessageProvider : IUsageProvider, IResetMessageProvider
    {
        private int _sendCalls;
        public ProviderKind Kind => ProviderKind.Claude;
        public int SendCalls => Volatile.Read(ref _sendCalls);
        public Func<AccountSettings, CancellationToken, Task<UsageSnapshot>> Fetch { get; init; } = (account, _) => Task.FromResult(Snapshot(account));
        public Func<AccountSettings, CancellationToken, Task> Send { get; init; } = (_, _) => Task.CompletedTask;
        public Task<ConnectionResult> ConnectAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
            => Task.FromResult(new ConnectionResult(account.IdentityKey!, null, "Pro"));
        public Task<UsageSnapshot> FetchAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
            => Fetch(account, cancellationToken);
        public Task DisconnectAsync(AccountSettings account, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendResetMessageAsync(AccountSettings account, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCalls);
            return Send(account, cancellationToken);
        }
    }
}
