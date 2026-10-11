using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class UsageAlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);
    private static AccountSettings Account() => new()
    {
        Provider = ProviderKind.Claude,
        IdentityKey = "account-identity",
        UsageAlerts = [new() { WindowId = "session", Enabled = true }]
    };
    private static UsageSnapshot Snapshot(AccountSettings account, double? used = 80,
        DateTimeOffset? reset = null, DateTimeOffset? fetched = null) => new(
        account.Provider, account.Id, account.IdentityKey, "Max", fetched ?? Now,
        [new("session", "현재 세션", used, reset ?? Now.AddHours(2))]);

    [Fact]
    public void AlertsAreOptInAndTriggerAtTheThreshold()
    {
        var account = Account();
        var engine = new UsageAlertEngine();
        account.UsageAlerts.Clear();
        Assert.Empty(engine.Evaluate(account, Snapshot(account), Now, true));
        account.UsageAlerts.Add(new() { WindowId = "session" });
        Assert.False(account.UsageAlerts[0].Enabled);
        Assert.Equal(80, account.UsageAlerts[0].ThresholdPercent);
        Assert.Empty(engine.Evaluate(account, Snapshot(account), Now, true));
        account.UsageAlerts[0].Enabled = true;
        Assert.Empty(engine.Evaluate(account, Snapshot(account, 79.9), Now, true));
        var alert = Assert.Single(engine.Evaluate(account, Snapshot(account), Now, true));
        Assert.Equal(80, alert.UsedPercent);
        Assert.Equal("현재 세션", alert.WindowName);
    }

    [Fact]
    public void SessionAndWeeklyRulesAreIndependent()
    {
        var account = Account();
        account.UsageAlerts.Add(new() { WindowId = "weekly", Enabled = true, ThresholdPercent = 90 });
        var snapshot = Snapshot(account) with
        {
            Windows = [new("session", "현재 세션", 85, Now.AddHours(2)), new("weekly", "주간", 89, Now.AddDays(3))]
        };
        var engine = new UsageAlertEngine();
        Assert.Equal("session", Assert.Single(engine.Evaluate(account, snapshot, Now, true)).WindowId);
        var next = snapshot with { Windows = [snapshot.Windows[0], snapshot.Windows[1] with { UsedPercent = 90 }] };
        Assert.Equal("weekly", Assert.Single(engine.Evaluate(account, next, Now, true)).WindowId);
    }

    [Fact]
    public void ChangingThresholdOrUtilizationCannotRepeatAnActivePeriod()
    {
        var account = Account();
        var engine = new UsageAlertEngine();
        Assert.Single(engine.Evaluate(account, Snapshot(account), Now, true));
        Assert.Empty(engine.Evaluate(account, Snapshot(account, 30), Now, true));
        account.UsageAlerts[0].ThresholdPercent = 95;
        Assert.Empty(engine.Evaluate(account, Snapshot(account, 100), Now, true));
        // Server rounding or reset-time adjustments within the active period are not new periods.
        Assert.Empty(engine.Evaluate(account, Snapshot(account, 100, Now.AddHours(2).AddSeconds(1)), Now, true));
    }

    [Fact]
    public void TheNextResetPeriodCanAlertAgain()
    {
        var account = Account();
        var engine = new UsageAlertEngine();
        var firstReset = Now.AddHours(2);
        Assert.Single(engine.Evaluate(account, Snapshot(account, reset: firstReset), Now, true));
        var later = firstReset.AddMinutes(1);
        Assert.Single(engine.Evaluate(account, Snapshot(account, reset: later.AddHours(5), fetched: later), later, true));
        Assert.Equal(2, engine.GetState().Deliveries.Count);
    }

    [Fact]
    public void ReloadedStatePreventsASecondNotification()
    {
        var account = Account();
        var first = new UsageAlertEngine();
        Assert.Single(first.Evaluate(account, Snapshot(account), Now, true));
        var restarted = new UsageAlertEngine(first.GetState());
        Assert.Empty(restarted.Evaluate(account, Snapshot(account, 99), Now, true));
        var exported = restarted.GetState();
        exported.Deliveries.Clear();
        Assert.Empty(restarted.Evaluate(account, Snapshot(account, 99), Now, true));
    }

    [Fact]
    public void IdentityAndAccountBoundariesAreIndependent()
    {
        var account = Account();
        var engine = new UsageAlertEngine();
        var previousIdentity = Snapshot(account);
        Assert.Single(engine.Evaluate(account, previousIdentity, Now, true));
        account.IdentityKey = "replacement-identity";
        Assert.Empty(engine.Evaluate(account, previousIdentity, Now, true));
        Assert.Single(engine.Evaluate(account, Snapshot(account), Now, true));
        var another = Account();
        Assert.Single(engine.Evaluate(another, Snapshot(another), Now, true));
        account.IdentityKey = previousIdentity.IdentityKey;
        Assert.Empty(engine.Evaluate(account, previousIdentity, Now, true));
    }

    [Fact]
    public void CacheReplaysOldFutureFailedOrExpiredReadsNeverAlert()
    {
        var account = Account();
        var engine = new UsageAlertEngine();
        var valid = Snapshot(account);
        Assert.Empty(engine.Evaluate(account, valid, Now, false));
        Assert.Empty(engine.Evaluate(account, valid with { FetchedAt = Now.AddMinutes(-6) }, Now, true));
        Assert.Empty(engine.Evaluate(account, valid with { FetchedAt = Now.AddSeconds(1) }, Now, true));
        Assert.Empty(engine.Evaluate(account, valid with { State = UsageState.Offline }, Now, true));
        Assert.Empty(engine.Evaluate(account, Snapshot(account, reset: Now), Now, true));
        Assert.Empty(engine.Evaluate(account, Snapshot(account, reset: Now.AddMinutes(-1)), Now, true));
        Assert.Empty(engine.GetState().Deliveries);
        Assert.Single(engine.Evaluate(account, valid, Now, true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    [InlineData(101d)]
    public void InvalidPercentagesNeverNotify(double? used)
    {
        var account = Account();
        var engine = new UsageAlertEngine();
        Assert.Empty(engine.Evaluate(account, Snapshot(account, used), Now, true));
        Assert.Empty(engine.GetState().Deliveries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void InvalidRulesAreNotEvaluated(int threshold)
    {
        var account = Account();
        account.UsageAlerts[0].ThresholdPercent = threshold;
        Assert.Empty(new UsageAlertEngine().Evaluate(account, Snapshot(account, 100), Now, true));
    }

    [Fact]
    public void MissingResetDoesNotBecomeARecurringNotification()
    {
        var account = Account();
        var engine = new UsageAlertEngine();
        var unknown = Snapshot(account) with { Windows = [new("session", "현재 세션", 90, null)] };
        Assert.Single(engine.Evaluate(account, unknown, Now, true));
        var later = Now.AddDays(8);
        var restarted = new UsageAlertEngine(engine.GetState());
        Assert.Empty(restarted.Evaluate(account, unknown with { FetchedAt = later }, later, true));
        // A reset suddenly appearing cannot establish that the first notification was in a different period.
        Assert.Empty(restarted.Evaluate(account, Snapshot(account, reset: later.AddHours(1), fetched: later), later, true));
    }

    [Fact]
    public void LosingResetMetadataDoesNotNotifyAgain()
    {
        var account = Account();
        var engine = new UsageAlertEngine();
        Assert.Single(engine.Evaluate(account, Snapshot(account), Now, true));
        var unknown = Snapshot(account) with { Windows = [new("session", "현재 세션", 90, null)] };
        Assert.Empty(engine.Evaluate(account, unknown, Now, true));
    }

    [Fact]
    public void WrongAccountProviderOrMissingIdentityNeverNotify()
    {
        var account = Account();
        var engine = new UsageAlertEngine();
        Assert.Empty(engine.Evaluate(account, Snapshot(account) with { AccountId = Guid.NewGuid().ToString("N") }, Now, true));
        Assert.Empty(engine.Evaluate(account, Snapshot(account) with { Provider = ProviderKind.Codex }, Now, true));
        Assert.Empty(engine.Evaluate(account, Snapshot(account) with { IdentityKey = null }, Now, true));
        account.IdentityKey = null;
        Assert.Empty(engine.Evaluate(account, Snapshot(account), Now, true));
    }

    [Fact]
    public void DuplicateWindowIdsStillProduceOnlyOneAlert()
    {
        var account = Account();
        var snapshot = Snapshot(account);
        snapshot = snapshot with { Windows = [snapshot.Windows[0], snapshot.Windows[0]] };
        Assert.Single(new UsageAlertEngine().Evaluate(account, snapshot, Now, true));
    }

    [Fact]
    public void RemovingAnAccountClearsOnlyItsHistory()
    {
        var one = Account();
        var two = Account();
        var engine = new UsageAlertEngine();
        Assert.Single(engine.Evaluate(one, Snapshot(one), Now, true));
        Assert.Single(engine.Evaluate(two, Snapshot(two), Now, true));
        engine.RemoveAccount(one.Id);
        Assert.Equal(two.Id, Assert.Single(engine.GetState().Deliveries).AccountId);
        Assert.Empty(engine.Evaluate(two, Snapshot(two), Now, true));
    }
}
