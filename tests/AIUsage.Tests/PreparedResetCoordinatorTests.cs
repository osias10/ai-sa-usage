using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class PreparedResetCoordinatorTests
{
    [Fact]
    public async Task UpdatePreparationFinishesBeforeReservationAndLeaseSpansSend()
    {
        var account = Account();
        var entered = Signal();
        var ready = Signal();
        var events = new List<string>();
        var message = new PreparedMessage(events);
        var provider = new Provider(async token =>
        {
            events.Add("prepare");
            entered.SetResult();
            await ready.Task.WaitAsync(token);
            return message;
        });
        await using var coordinator = Coordinator(account, provider);
        var sending = coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, (_, _) =>
        {
            Assert.False(message.Disposed);
            events.Add("reserve");
            return Task.FromResult(true);
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "prepare" }, events);
        ready.SetResult();
        Assert.True(await sending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "prepare", "reserve", "send", "dispose" }, events);
        Assert.Equal(0, provider.LegacyCalls);
    }

    [Fact]
    public async Task PreparationFailureDoesNotConsumeAnAttempt()
    {
        var account = Account();
        var provider = new Provider(_ => throw new ProviderException(UsageState.UpdateRequired, "bad checksum"));
        await using var coordinator = Coordinator(account, provider);
        var reserved = false;
        await Assert.ThrowsAsync<ProviderException>(() => coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, (_, _) =>
        {
            reserved = true;
            return Task.FromResult(true);
        }, default));
        Assert.False(reserved);
        Assert.Equal(0, provider.LegacyCalls);
    }

    [Fact]
    public async Task CancellationWhileWaitingForToolDoesNotReserve()
    {
        var account = Account();
        var entered = Signal();
        var provider = new Provider(async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        });
        await using var coordinator = Coordinator(account, provider);
        using var cancellation = new CancellationTokenSource();
        var reservations = 0;
        var sending = coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, (_, _) =>
        {
            reservations++;
            return Task.FromResult(true);
        }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.Equal(0, reservations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedOrFailedReservationDisposesPreparedToolWithoutSending(bool fail)
    {
        var account = Account();
        var events = new List<string>();
        var message = new PreparedMessage(events);
        var provider = new Provider(_ => Task.FromResult<IPreparedResetMessage>(message));
        await using var coordinator = Coordinator(account, provider);
        var sending = coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, (_, _) =>
            fail ? throw new IOException("cannot save") : Task.FromResult(false), default);
        if (fail) await Assert.ThrowsAsync<IOException>(() => sending);
        else Assert.False(await sending);
        Assert.Equal(new[] { "dispose" }, events);
    }

    [Fact]
    public async Task AccountDisabledDuringPreparationIsRecheckedBeforeReservation()
    {
        var account = Account();
        var events = new List<string>();
        var provider = new Provider(_ =>
        {
            account.ResetMessageEnabled = false;
            return Task.FromResult<IPreparedResetMessage>(new PreparedMessage(events));
        });
        await using var coordinator = Coordinator(account, provider);
        Assert.False(await coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, (_, _) =>
            throw new InvalidOperationException("Disabled account must not reserve"), default));
        Assert.Equal(new[] { "dispose" }, events);
    }

    [Fact]
    public async Task SendFailureReleasesToolAndDoesNotInvokeLegacySend()
    {
        var account = Account();
        var events = new List<string>();
        var provider = new Provider(_ => Task.FromResult<IPreparedResetMessage>(new PreparedMessage(events, fail: true)));
        await using var coordinator = Coordinator(account, provider);
        var reservations = 0;
        await Assert.ThrowsAsync<IOException>(() => coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!, (_, _) =>
        { reservations++; return Task.FromResult(true); }, default));
        Assert.Equal(1, reservations);
        Assert.Equal(new[] { "send", "dispose" }, events);
        Assert.Equal(0, provider.LegacyCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionWaitsForPreparedOperationDisposal(bool disableWhilePreparing)
    {
        var account = Account();
        var disposing = Signal();
        var release = Signal();
        var provider = new Provider(_ =>
        {
            account.ResetMessageEnabled = !disableWhilePreparing;
            return Task.FromResult<IPreparedResetMessage>(new BlockingDisposalMessage(disposing, release));
        });
        await using var coordinator = Coordinator(account, provider);
        var sending = coordinator.SendResetMessageAsync(account.Id, account.IdentityKey!,
            (_, _) => Task.FromResult(true), default);
        try
        {
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(sending.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(!disableWhilePreparing, await sending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task LeaseIsReleasedExactlyOnceAcrossDisposalForms()
    {
        var releases = 0;
        var lease = new RuntimeLease("fixture.exe", "1.2.3", () => releases++);
        lease.Dispose();
        await lease.DisposeAsync();
        lease.Dispose();
        Assert.Equal(1, releases);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static AccountSettings Account() => new() { Provider = ProviderKind.Claude, IdentityKey = "fixture", ResetMessageEnabled = true };
    private static UsageCoordinator Coordinator(AccountSettings account, Provider provider) => new([provider], [account],
        [new UsageSnapshot(account.Provider, account.Id, account.IdentityKey, "Pro", DateTimeOffset.UtcNow, [new("session", "Session", 0, null)])]);
    private sealed class PreparedMessage(List<string> events, bool fail = false) : IPreparedResetMessage
    {
        public bool Disposed { get; private set; }
        public Task SendAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.False(Disposed);
            events.Add("send");
            return fail ? Task.FromException(new IOException("uncertain result")) : Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; events.Add("dispose"); return ValueTask.CompletedTask; }
    }
    private sealed class BlockingDisposalMessage(TaskCompletionSource disposing, TaskCompletionSource release) : IPreparedResetMessage
    {
        public Task SendAsync(CancellationToken token) => Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            disposing.TrySetResult();
            await release.Task;
        }
    }
    private sealed class Provider(Func<CancellationToken, Task<IPreparedResetMessage>> prepare)
        : IUsageProvider, IResetMessageProvider, IPreparedResetMessageProvider
    {
        public ProviderKind Kind => ProviderKind.Claude;
        public int LegacyCalls { get; private set; }
        public Task<IPreparedResetMessage> PrepareResetMessageAsync(AccountSettings account, CancellationToken token) => prepare(token);
        public Task SendResetMessageAsync(AccountSettings account, CancellationToken token) { LegacyCalls++; throw new InvalidOperationException("Prepared operation required"); }
        public Task<ConnectionResult> ConnectAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken token) => throw new NotSupportedException();
        public Task<UsageSnapshot> FetchAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken token) => throw new NotSupportedException();
        public Task DisconnectAsync(AccountSettings account, CancellationToken token) => Task.CompletedTask;
    }
}
