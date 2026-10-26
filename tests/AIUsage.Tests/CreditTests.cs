using System.Text.Json;
using AIUsage.Core;
using AIUsage.Providers;
using Xunit;

namespace AIUsage.Tests;

public sealed class CreditTests
{
    [Fact]
    public void CodexCreditsFollowAuthoritativeBucketsWithoutDuplicatingLegacyBalance()
    {
        var snapshot = CodexUsageParser.Parse("""
            {"rateLimits":{"credits":{"hasCredits":true,"unlimited":false,"balance":"999"}},
             "rateLimitsByLimitId":{
               "codex":{"primary":{"usedPercent":25},"credits":{"hasCredits":true,"unlimited":false,"balance":"10.25"}},
               "other":{"limitName":"Other","primary":{"usedPercent":30},"credits":{"hasCredits":true,"unlimited":false,"balance":"5"}}},
             "rateLimitResetCredits":{"availableCount":2,"credits":[]}}
            """, Account());
        Assert.Equal(UsageState.Ready, snapshot.State);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal(new decimal?[] { 10.25m, 5m, 2m }, snapshot.Credits!.Select(x => x.Balance));
        Assert.Equal(3, snapshot.Credits!.Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public void CreditOnlyAccountCanStillBeRefreshed()
    {
        var snapshot = CodexUsageParser.Parse("""
            {"rateLimits":{"primary":null,"secondary":null,"credits":{"hasCredits":true,"unlimited":false,"balance":"0"}}}
            """, Account());
        Assert.Equal(UsageState.Ready, snapshot.State);
        Assert.Empty(snapshot.Windows);
        Assert.Equal(0m, Assert.Single(snapshot.Credits!).Balance);
    }

    [Fact]
    public void MalformedOptionalCreditsDoNotDiscardValidQuota()
    {
        var snapshot = CodexUsageParser.Parse("""
            {"rateLimits":{"primary":{"usedPercent":33},"credits":{"hasCredits":"true","unlimited":false,"balance":"3"}},
             "rateLimitResetCredits":{"availableCount":-1}}
            """, Account());
        Assert.Equal(UsageState.Ready, snapshot.State);
        Assert.Equal(33, Assert.Single(snapshot.Windows).UsedPercent);
        Assert.Null(snapshot.Credits);
    }

    [Fact]
    public void ClaudeOverageBudgetDoesNotBecomePurchasedBalanceOrQuota()
    {
        var snapshot = ClaudeUsageParser.Parse("""
            {"type":"assistant","usage_report":{"rate_limits":{"limits":[],
             "extra_usage":{"is_enabled":true,"monthly_limit":10000,"used_credits":2500,"currency":"USD"}}}}
            """, new AccountSettings { Provider = ProviderKind.Claude, IdentityKey = "fixture-claude-credits" });
        Assert.Equal(UsageState.Ready, snapshot.State);
        Assert.Empty(snapshot.Windows);
        var credit = Assert.Single(snapshot.Credits!);
        Assert.Equal("25 / 100 USD", CreditFormatting.Value(credit));
        Assert.Null(credit.Balance);
        Assert.Contains("충전 잔액이 아닙니다", CreditFormatting.Detail(credit));
    }

    [Fact]
    public void FormattingKeepsUnknownZeroUnlimitedDisabledAndAvailabilityDistinct()
    {
        var empty = new AccountCredit("c", "크레딧", "크레딧");
        Assert.Equal("—", CreditFormatting.Value(empty));
        Assert.Equal("0 크레딧", CreditFormatting.Value(empty with { Balance = 0 }));
        Assert.Equal("0.0000000000000000000000000001 크레딧", CreditFormatting.Value(empty with { Balance = 0.0000000000000000000000000001m }));
        Assert.Equal("1,234.567 크레딧", CreditFormatting.Value(empty with { Balance = 1234.567m }));
        Assert.Equal("있음 · 잔액 미제공", CreditFormatting.Value(empty with { HasCredits = true }));
        Assert.Equal("없음", CreditFormatting.Value(empty with { HasCredits = false }));
        Assert.Equal("무제한", CreditFormatting.Value(empty with { Unlimited = true, Balance = 0 }));
        Assert.Equal("꺼짐", CreditFormatting.Value(empty with { IsEnabled = false, Unlimited = true }));
        Assert.Equal("— / 10 크레딧", CreditFormatting.Value(empty with { Limit = 10 }));
    }

    [Fact]
    public async Task CachedCreditFailureKeepsTimestampButFreshOmissionClearsBalance()
    {
        var account = Account();
        var initial = Snapshot(account);
        var failing = true;
        var provider = new CreditProvider(a => failing
            ? throw new ProviderException(UsageState.Offline, "offline")
            : Snapshot(a) with { Credits = null, FetchedAt = initial.FetchedAt.AddMinutes(5) });
        await using var coordinator = new UsageCoordinator([provider], [account], [initial]);
        await coordinator.RefreshAsync(account.Id);
        Assert.Equal(UsageState.Offline, coordinator.GetStatus(account.Id).State);
        Assert.Same(initial, coordinator.GetStatus(account.Id).Snapshot);
        failing = false;
        await coordinator.RefreshAsync(account.Id);
        var latest = coordinator.GetStatus(account.Id).Snapshot!;
        Assert.Null(latest.Credits);
        Assert.True(latest.FetchedAt > initial.FetchedAt);
    }

    [Fact]
    public async Task CachedCreditsNeverCrossAccountIdentity()
    {
        var account = Account();
        var previous = Snapshot(account);
        account.IdentityKey = "another-account";
        await using var coordinator = new UsageCoordinator([new CreditProvider(Snapshot)], [account], [previous]);
        Assert.Null(coordinator.GetStatus(account.Id).Snapshot);
    }

    [Fact]
    public async Task SettingsAndCacheRoundTripWhileOldFilesKeepCreditsUnknown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aisa-credit-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var paths = new AppPaths(directory);
            var store = new JsonSettingsStore(paths);
            var account = Account();
            Assert.False(account.ShowCreditsInWidget);
            account.ShowCreditsInWidget = true;
            await store.SaveSettingsAsync(new AppSettings { Accounts = [account] });
            await store.SaveSnapshotsAsync([Snapshot(account)]);
            Assert.True(Assert.Single((await store.LoadSettingsAsync()).Accounts).ShowCreditsInWidget);
            var restored = Assert.Single(await store.LoadSnapshotsAsync());
            Assert.Equal(10.25m, Assert.Single(restored.Credits!).Balance);
            var json = JsonSerializer.SerializeToNode(Snapshot(account))!.AsObject();
            json.Remove("Credits");
            await File.WriteAllTextAsync(paths.CacheFile, "[" + json.ToJsonString() + "]");
            Assert.Null(Assert.Single(await store.LoadSnapshotsAsync()).Credits);
            await store.SaveSnapshotsAsync([Snapshot(account) with { Credits = [null!, new("bad", "bad", "x", Balance: -1), new("ok", "크레딧", "크레딧", Balance: 0)] }]);
            var cleaned = Assert.Single(await store.LoadSnapshotsAsync());
            Assert.Equal(30, Assert.Single(cleaned.Windows).UsedPercent);
            Assert.Equal("ok", Assert.Single(cleaned.Credits!).Id);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static AccountSettings Account() => new() { Provider = ProviderKind.Codex, IdentityKey = "fixture-credits" };
    private static UsageSnapshot Snapshot(AccountSettings account) => new(account.Provider, account.Id, account.IdentityKey, "Pro",
        DateTimeOffset.UtcNow.AddMinutes(-10), [new("session", "세션", 30, null)],
        Credits: [new("codex:credits", "크레딧", "크레딧", Balance: 10.25m)]);

    private sealed class CreditProvider(Func<AccountSettings, UsageSnapshot> fetch) : IUsageProvider
    {
        public ProviderKind Kind => ProviderKind.Codex;
        public Task<UsageSnapshot> FetchAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken) => Task.FromResult(fetch(account));
        public Task<ConnectionResult> ConnectAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DisconnectAsync(AccountSettings account, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
