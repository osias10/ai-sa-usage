using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class ResetAlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private static AccountSettings Account() => new()
    {
        Provider = ProviderKind.Claude, IdentityKey = "synthetic-identity",
        UsageAlerts = [new() { WindowId = "session", ResetEnabled = true }]
    };
    private static UsageSnapshot Read(AccountSettings account, DateTimeOffset fetched, DateTimeOffset? reset, double? used = 20)
        => new(account.Provider, account.Id, account.IdentityKey, "Max", fetched, [new("session", "현재 세션", used, reset)]);

    [Fact]
    public void ResetAlertsAreOptInAndNeedTwoConfirmedPeriods()
    {
        Assert.False(new UsageAlertRule().ResetEnabled);
        var account = Account();
        var engine = new UsageAlertEngine();
        var firstReset = Now.AddHours(1);
        Assert.Empty(engine.Evaluate(account, Read(account, Now, firstReset), Now, true));
        Assert.Single(engine.GetState().ResetObservations);
        var afterReset = firstReset.AddSeconds(1);
        var nextReset = afterReset.AddHours(5);
        var alert = Assert.Single(engine.Evaluate(account, Read(account, afterReset, nextReset, 3), afterReset, true));
        Assert.Equal(UsageAlertKind.Reset, alert.Kind);
        Assert.Equal(nextReset, alert.ResetsAt);
        Assert.Equal(3, alert.UsedPercent);
        Assert.Empty(engine.GetState().Deliveries); // Reset notices do not consume threshold receipts.
        Assert.Empty(engine.Evaluate(account, Read(account, afterReset.AddSeconds(1), nextReset, 4), afterReset.AddSeconds(1), true));
    }

    [Fact]
    public void ThresholdAndResetRulesOperateIndependently()
    {
        var account = Account();
        account.UsageAlerts[0].Enabled = true;
        var engine = new UsageAlertEngine();
        var firstReset = Now.AddHours(1);
        Assert.Equal(UsageAlertKind.Threshold,
            Assert.Single(engine.Evaluate(account, Read(account, Now, firstReset, 90), Now, true)).Kind);
        var after = firstReset.AddMinutes(1);
        var next = after.AddHours(5);
        Assert.Equal(new[] { UsageAlertKind.Reset, UsageAlertKind.Threshold },
            engine.Evaluate(account, Read(account, after, next, 90), after, true).Select(alert => alert.Kind));
        Assert.Equal(2, engine.GetState().Deliveries.Count);

        // Invalid/off threshold settings cannot disable an independently enabled reset rule.
        account.UsageAlerts[0].Enabled = false;
        account.UsageAlerts[0].ThresholdPercent = 0;
        after = next.AddMinutes(1);
        Assert.Equal(UsageAlertKind.Reset,
            Assert.Single(engine.Evaluate(account, Read(account, after, after.AddHours(5)), after, true)).Kind);
    }

    [Fact]
    public void ActivePeriodCorrectionsAndFallingUsageAreNotResets()
    {
        var account = Account();
        var engine = new UsageAlertEngine();
        var original = Now.AddHours(1);
        Assert.Empty(engine.Evaluate(account, Read(account, Now, original, 80), Now, true));
        var corrected = original.AddMinutes(10);
        Assert.Empty(engine.Evaluate(account, Read(account, Now.AddMinutes(1), corrected, 0), Now.AddMinutes(1), true));
        Assert.Empty(engine.Evaluate(account, Read(account, Now.AddMinutes(2), original, 0), Now.AddMinutes(2), true));
        Assert.Equal(corrected, Assert.Single(engine.GetState().ResetObservations).ResetsAt);
        // The older deadline has elapsed, but the corrected active period has not ended.
        var between = original.AddMinutes(1);
        Assert.Empty(engine.Evaluate(account, Read(account, between, corrected, 5), between, true));
        var later = corrected.AddMinutes(1);
        Assert.Single(engine.Evaluate(account, Read(account, later, later.AddHours(5)), later, true));
    }

    [Fact]
    public void MerelyCrossingTheDeadlineOrLosingValuesCannotNotify()
    {
        var account = Account();
        var engine = new UsageAlertEngine();
        var reset = Now.AddMinutes(1);
        engine.Evaluate(account, Read(account, Now, reset), Now, true);
        var later = reset.AddMinutes(1);
        var unchanged = Read(account, later, reset, 0);
        Assert.Empty(engine.Evaluate(account, unchanged, later, true));
        Assert.Empty(engine.Evaluate(account, Read(account, later, null, 0), later, true));
        Assert.Empty(engine.Evaluate(account, Read(account, later, later.AddHours(5), null), later, true));
        Assert.Empty(engine.Evaluate(account, Read(account, later, later.AddHours(5)) with { Windows = [] }, later, true));
        Assert.Equal(reset, Assert.Single(engine.GetState().ResetObservations).ResetsAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    [InlineData(101d)]
    public void InvalidUsageCannotCreateOrAdvanceAResetObservation(double? used)
    {
        var account = Account();
        var engine = new UsageAlertEngine();
        var reset = Now.AddMinutes(1);
        Assert.Empty(engine.Evaluate(account, Read(account, Now, reset, used), Now, true));
        Assert.Empty(engine.GetState().ResetObservations);
        engine.Evaluate(account, Read(account, Now, reset), Now, true);
        var later = reset.AddMinutes(1);
        Assert.Empty(engine.Evaluate(account, Read(account, later, later.AddHours(5), used), later, true));
        Assert.Equal(reset, Assert.Single(engine.GetState().ResetObservations).ResetsAt);
    }

    [Fact]
    public void CacheErrorsStaleAndFutureReadsCannotEstablishOrAdvanceABaseline()
    {
        var account = Account();
        var engine = new UsageAlertEngine();
        var first = Read(account, Now, Now.AddMinutes(1));
        Assert.Empty(engine.Evaluate(account, first, Now, false));
        Assert.Empty(engine.GetState().ResetObservations);
        engine.Evaluate(account, first, Now, true);
        var later = Now.AddMinutes(2);
        var next = Read(account, later, later.AddHours(5));
        Assert.Empty(engine.Evaluate(account, next, later, false));
        Assert.Empty(engine.Evaluate(account, next with { State = UsageState.Offline }, later, true));
        Assert.Empty(engine.Evaluate(account, next with { FetchedAt = later.AddMinutes(-6) }, later, true));
        Assert.Empty(engine.Evaluate(account, next with { FetchedAt = later.AddSeconds(1) }, later, true));
        Assert.Equal(first.Windows[0].ResetsAt, Assert.Single(engine.GetState().ResetObservations).ResetsAt);
        Assert.Single(engine.Evaluate(account, next, later, true));
    }

    [Fact]
    public void AReadFetchedBeforeExpiryCannotConfirmAResetWhenProcessedLater()
    {
        var account = Account();
        var engine = new UsageAlertEngine();
        var reset = Now.AddMinutes(2);
        engine.Evaluate(account, Read(account, Now, reset), Now, true);
        var next = reset.AddHours(5);
        Assert.Empty(engine.Evaluate(account, Read(account, reset.AddSeconds(-1), next), reset.AddSeconds(1), true));
        Assert.Equal(next, Assert.Single(engine.GetState().ResetObservations).ResetsAt);
    }

    [Fact]
    public void ClockRollbackAndOutOfOrderResponsesDoNotManufactureAnotherTransition()
    {
        var account = Account();
        var engine = new UsageAlertEngine();
        var reset = Now.AddMinutes(2);
        engine.Evaluate(account, Read(account, Now, reset), Now, true);
        var later = reset.AddMinutes(1);
        var next = later.AddHours(5);
        Assert.Single(engine.Evaluate(account, Read(account, later, next), later, true));
        Assert.Empty(engine.Evaluate(account, Read(account, Now.AddMinutes(1), reset), Now.AddMinutes(1), true));
        Assert.Empty(engine.Evaluate(account, Read(account, later.AddSeconds(-1), next.AddMinutes(1)), later, true));
        Assert.Equal(next, Assert.Single(engine.GetState().ResetObservations).ResetsAt);

        // A backwards local clock with a newer fetched timestamp still cannot move local history back.
        var delayed = Read(account, later.AddSeconds(1), next);
        engine.Evaluate(account, delayed, later.AddMinutes(1), true);
        Assert.Empty(engine.Evaluate(account, Read(account, later.AddSeconds(2), next.AddMinutes(1)), later.AddSeconds(2), true));
        Assert.Equal(next, Assert.Single(engine.GetState().ResetObservations).ResetsAt);
    }

    [Fact]
    public void RestartRetainsBaselineAndPreventsDuplicateResetAlerts()
    {
        var account = Account();
        var reset = Now.AddMinutes(1);
        var first = new UsageAlertEngine();
        first.Evaluate(account, Read(account, Now, reset), Now, true);
        var restarted = new UsageAlertEngine(first.GetState());
        restarted.SynchronizeSettings(account);
        var later = reset.AddMinutes(1);
        var next = later.AddHours(5);
        Assert.Single(restarted.Evaluate(account, Read(account, later, next), later, true));
        var again = new UsageAlertEngine(restarted.GetState());
        Assert.Empty(again.Evaluate(account, Read(account, later.AddMinutes(1), next), later.AddMinutes(1), true));
        var detached = again.GetState();
        detached.ResetObservations.Clear();
        Assert.Single(again.GetState().ResetObservations);
    }

    [Fact]
    public void TurningResetOffAndOnWithoutAnyReadRequiresANewBaseline()
    {
        var account = Account();
        account.UsageAlerts[0].Enabled = true;
        var engine = new UsageAlertEngine();
        var reset = Now.AddMinutes(1);
        engine.Evaluate(account, Read(account, Now, reset, 90), Now, true);
        account.UsageAlerts[0].ResetEnabled = false;
        engine.SynchronizeSettings(account);
        Assert.Empty(engine.GetState().ResetObservations);
        Assert.Single(engine.GetState().Deliveries);
        account.UsageAlerts[0].ResetEnabled = true;
        engine.SynchronizeSettings(account);
        var later = reset.AddMinutes(1);
        Assert.Empty(engine.Evaluate(account, Read(account, later, later.AddHours(5)), later, true));
        Assert.Single(engine.GetState().ResetObservations);
    }

    [Fact]
    public void AccountIdentityAndWindowsHaveSeparateBaselinesAndRemovalCleansOnlyOneAccount()
    {
        var one = Account();
        var two = Account();
        one.UsageAlerts.Add(new() { WindowId = "weekly", ResetEnabled = true });
        var engine = new UsageAlertEngine();
        var first = Read(one, Now, Now.AddMinutes(1)) with
        { Windows = [new("session", "세션", 50, Now.AddMinutes(1)), new("weekly", "주간", 50, Now.AddDays(1))] };
        engine.Evaluate(one, first, Now, true);
        engine.Evaluate(two, Read(two, Now, Now.AddMinutes(1)), Now, true);
        var later = Now.AddMinutes(2);
        var second = first with { FetchedAt = later,
            Windows = [new("session", "세션", 0, later.AddHours(5)), first.Windows[1]] };
        Assert.Equal("session", Assert.Single(engine.Evaluate(one, second, later, true)).WindowId);
        one.IdentityKey = "different-synthetic-identity";
        engine.SynchronizeSettings(one);
        Assert.Equal(two.Id, Assert.Single(engine.GetState().ResetObservations).AccountId);
        Assert.Empty(engine.Evaluate(one, second, later, true));
        Assert.Empty(engine.Evaluate(one, Read(one, later, later.AddHours(5)), later, true));
        engine.RemoveAccount(one.Id);
        Assert.Equal(two.Id, Assert.Single(engine.GetState().ResetObservations).AccountId);
        two.UsageAlerts.Clear();
        engine.SynchronizeSettings(two);
        Assert.Empty(engine.GetState().ResetObservations);
    }

    [Fact]
    public void DuplicateWindowsDoNotRepeatAReset()
    {
        var account = Account();
        var engine = new UsageAlertEngine();
        engine.Evaluate(account, Read(account, Now, Now.AddMinutes(1)), Now, true);
        var later = Now.AddMinutes(2);
        var next = Read(account, later, later.AddHours(5));
        Assert.Single(engine.Evaluate(account, next with { Windows = [next.Windows[0], next.Windows[0]] }, later, true));
        Assert.Single(engine.GetState().ResetObservations);
    }
}
