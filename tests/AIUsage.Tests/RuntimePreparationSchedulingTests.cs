using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class RuntimePreparationSchedulingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoToolWaitsDoNotBlockAnotherProvidersUsageRead(bool resetMessages)
    {
        var accounts = new[] { Account(ProviderKind.Claude), Account(ProviderKind.Claude), Account(ProviderKind.Codex) };
        var twoWaiting = Signal();
        var ready = Signal();
        var otherRead = Signal();
        var waiting = 0;
        var claude = new PreparingProvider(ProviderKind.Claude)
        {
            Prepare = async token =>
            {
                if (Interlocked.Increment(ref waiting) == 2) twoWaiting.TrySetResult();
                await ready.Task.WaitAsync(token);
            }
        };
        var codex = new PreparingProvider(ProviderKind.Codex)
        {
            Read = (account, _) => { otherRead.TrySetResult(); return Task.FromResult(Snapshot(account)); }
        };
        await using var coordinator = new UsageCoordinator([claude, codex], accounts, accounts.Select(Snapshot));
        var pending = accounts.Take(2).Select(account => resetMessages
            ? (Task)coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, (_, _) => Task.FromResult(true), default)
            : coordinator.RefreshAsync(account.Id)).ToArray();
        try
        {
            await twoWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var reading = coordinator.RefreshAsync(accounts[2].Id);
            await otherRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await reading.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(pending, operation => Assert.False(operation.IsCompleted));
        }
        finally
        {
            ready.TrySetResult();
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task PreparedValidationAndReadsStillShareTheTwoRequestSlots()
    {
        var accounts = Enumerable.Range(0, 4).Select(_ => Account(ProviderKind.Claude)).ToArray();
        var twoActive = Signal();
        var finish = Signal();
        var active = 0;
        var maximum = 0;
        var sync = new object();
        async Task Work(CancellationToken token)
        {
            lock (sync)
            {
                maximum = Math.Max(maximum, ++active);
                if (active == 2) twoActive.TrySetResult();
            }
            try { await finish.Task.WaitAsync(token); }
            finally { lock (sync) active--; }
        }
        var provider = new PreparingProvider(ProviderKind.Claude)
        {
            Validate = Work,
            Read = async (account, token) => { await Work(token); return Snapshot(account); }
        };
        await using var coordinator = new UsageCoordinator([provider], accounts, accounts.Select(Snapshot));
        var operations = accounts.Select((account, index) => index % 2 == 0
            ? coordinator.RefreshAsync(account.Id)
            : (Task)coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, (_, _) => Task.FromResult(true), default)).ToArray();
        await twoActive.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (sync) Assert.Equal(2, active);
        finish.SetResult();
        await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, maximum);
        Assert.Equal(4, provider.Disposals);
    }

    [Fact]
    public async Task PeriodicPollingContinuesWhileAnotherProvidersToolIsStillPreparing()
    {
        var time = new AdvancingTime();
        var claudeAccount = Account(ProviderKind.Claude);
        var codexAccount = Account(ProviderKind.Codex);
        claudeAccount.RefreshMinutes = codexAccount.RefreshMinutes = 1;
        var preparing = Signal();
        var firstFinished = Signal();
        var secondRead = Signal();
        var reads = 0;
        var claude = new PreparingProvider(ProviderKind.Claude)
        {
            Prepare = async token => { preparing.SetResult(); await Task.Delay(Timeout.Infinite, token); }
        };
        var codex = new PreparingProvider(ProviderKind.Codex)
        {
            Read = (account, _) =>
            {
                if (Interlocked.Increment(ref reads) == 2) secondRead.TrySetResult();
                return Task.FromResult(Snapshot(account) with { FetchedAt = time.GetUtcNow() });
            }
        };
        await using var coordinator = new UsageCoordinator([claude, codex], [claudeAccount, codexAccount],
            [Snapshot(claudeAccount) with { FetchedAt = time.GetUtcNow().AddMinutes(-2) },
             Snapshot(codexAccount) with { FetchedAt = time.GetUtcNow().AddMinutes(-2) }], time);
        coordinator.Changed += (_, e) =>
        {
            if (e.Status.AccountId == codexAccount.Id && !e.Status.IsRefreshing && e.Status.State == UsageState.Ready)
                firstFinished.TrySetResult();
        };
        coordinator.Start();
        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await firstFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromMinutes(2));
        await secondRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.GetStatus(claudeAccount.Id).IsRefreshing);
    }

    [Fact]
    public async Task ValidationFailureReleasesPreparedToolWithoutReserving()
    {
        var account = Account(ProviderKind.Claude);
        var provider = new PreparingProvider(ProviderKind.Claude)
        { Validate = _ => throw new ProviderException(UsageState.SignInRequired, "expired") };
        await using var coordinator = new UsageCoordinator([provider], [account], [Snapshot(account)]);
        await Assert.ThrowsAsync<ProviderException>(() => coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!,
            (_, _) => throw new InvalidOperationException("Validation must precede reservation"), default));
        Assert.Equal(1, provider.Disposals);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class AdvancingTime : TimeProvider
    {
        private long _ticks = DateTimeOffset.UtcNow.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan span) => Interlocked.Add(ref _ticks, span.Ticks);
    }
    private static AccountSettings Account(ProviderKind kind) => new()
    { Provider = kind, IdentityKey = Guid.NewGuid().ToString("N"), ResetMessageEnabled = true };
    private static UsageSnapshot Snapshot(AccountSettings account) => new(account.Provider, account.Id, account.IdentityKey,
        "Pro", DateTimeOffset.UtcNow, [new("session", "Session", 0, null)]);

    private sealed class PreparingProvider(ProviderKind kind)
        : IUsageProvider, IPreparedUsageProvider, IResetMessageProvider, IPreparedResetMessageProvider
    {
        private int _disposals;
        public ProviderKind Kind => kind;
        public int Disposals => _disposals;
        public Func<CancellationToken, Task> Prepare { get; init; } = _ => Task.CompletedTask;
        public Func<CancellationToken, Task> Validate { get; init; } = _ => Task.CompletedTask;
        public Func<AccountSettings, CancellationToken, Task<UsageSnapshot>> Read { get; init; } = (account, _) => Task.FromResult(Snapshot(account));
        public async Task<IPreparedUsageRead> PrepareUsageReadAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken token)
        { await Prepare(token); return new Prepared(this, account); }
        public async Task<IPreparedResetMessage> PrepareResetMessageAsync(AccountSettings account, CancellationToken token)
        { await Prepare(token); return new Prepared(this, account); }
        public Task<ConnectionResult> ConnectAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken token) => throw new NotSupportedException();
        public Task<UsageSnapshot> FetchAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken token) => throw new InvalidOperationException("Prepared path required");
        public Task SendResetMessageAsync(AccountSettings account, CancellationToken token) => throw new InvalidOperationException("Prepared path required");
        public Task DisconnectAsync(AccountSettings account, CancellationToken token) => Task.CompletedTask;
        private sealed class Prepared(PreparingProvider owner, AccountSettings account) : IPreparedUsageRead, IPreparedResetMessage
        {
            public Task<UsageSnapshot> FetchAsync(CancellationToken token) => owner.Read(account, token);
            public Task ValidateAsync(CancellationToken token) => owner.Validate(token);
            public Task SendAsync(CancellationToken token) => Task.CompletedTask;
            public ValueTask DisposeAsync() { Interlocked.Increment(ref owner._disposals); return ValueTask.CompletedTask; }
        }
    }
}
