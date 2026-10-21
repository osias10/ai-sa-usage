using System.Text.Json;
using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class ResetMessageEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = Now.AddMinutes(10);
    private static AccountSettings Account() => new() { Provider = ProviderKind.Claude, IdentityKey = "test-identity" };
    private static UsageSnapshot Read(AccountSettings account, DateTimeOffset fetched, DateTimeOffset? reset,
        double? used = 20, UsageState state = UsageState.Ready)
        => new(account.Provider, account.Id, account.IdentityKey, "test-plan", fetched,
            [new("session", "Session", used, reset)], state);
    private static ResetMessageEngine Armed(AccountSettings account)
    {
        var engine = new ResetMessageEngine();
        engine.Observe(account, true, "session", Read(account, Now, Reset), Now, true);
        return engine;
    }
    private static ResetMessageTicket Due(ResetMessageEngine engine, AccountSettings account, DateTimeOffset? now = null)
        => Assert.IsType<ResetMessageTicket>(engine.GetDue(account, true, "session", now ?? Reset));
    private static ResetMessageEngine Restart(ResetMessageEngine engine)
        => new(JsonSerializer.Deserialize<ResetMessageState>(JsonSerializer.Serialize(engine.GetState())));

    [Fact]
    public void EnablingOnlyArmsAConfirmedFutureResetAndNeverSendsOnItsOwn()
    {
        var account = Account();
        var engine = new ResetMessageEngine();
        engine.Observe(account, true, "session", Read(account, Now, Now.AddMinutes(-1), 0), Now, true);
        Assert.Empty(engine.GetState().Observations);
        Assert.Null(engine.GetDue(account, true, "session", Now));
        engine.Observe(account, true, "session", Read(account, Now, Reset), Now, true);
        Assert.Null(engine.GetDue(account, true, "session", Reset.AddTicks(-1)));
        Assert.Equal(Reset, Due(engine, account).ResetsAt);
        Assert.Empty(engine.GetState().Attempts);
    }

    [Fact]
    public void DisabledAndCachedReadsCannotArm()
    {
        var account = Account();
        var engine = new ResetMessageEngine();
        engine.Observe(account, false, "session", Read(account, Now, Reset), Now, true);
        engine.Observe(account, true, "session", Read(account, Now, Reset), Now, false);
        Assert.Empty(engine.GetState().Observations);
        Assert.Null(engine.GetDue(account, true, "session", Reset));
    }

    [Theory]
    [InlineData(UsageState.Offline)]
    [InlineData(UsageState.SignInRequired)]
    [InlineData(UsageState.RateLimited)]
    [InlineData(UsageState.UpdateRequired)]
    [InlineData(UsageState.Unavailable)]
    public void FailedReadsDoNotArmOrAuthorize(UsageState state)
    {
        var account = Account();
        var engine = new ResetMessageEngine();
        engine.Observe(account, true, "session", Read(account, Now, Reset, state: state), Now, true);
        Assert.Empty(engine.GetState().Observations);
        engine = Armed(account);
        Assert.Equal(ResetMessageDecision.Waiting,
            engine.PrepareAttempt(Due(engine, account), Read(account, Reset, null, 0, state), Reset, true));
        Assert.Empty(engine.GetState().Attempts);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidUsageDoesNotArmOrAuthorize(double percent)
    {
        var account = Account();
        var engine = new ResetMessageEngine();
        engine.Observe(account, true, "session", Read(account, Now, Reset, percent), Now, true);
        Assert.Empty(engine.GetState().Observations);
        engine = Armed(account);
        Assert.Equal(ResetMessageDecision.Waiting,
            engine.PrepareAttempt(Due(engine, account), Read(account, Reset, null, percent), Reset, true));
    }

    [Fact]
    public void MissingResetOrUsageCannotEstablishABaseline()
    {
        var account = Account();
        var engine = new ResetMessageEngine();
        engine.Observe(account, true, "session", Read(account, Now, null), Now, true);
        engine.Observe(account, true, "session", Read(account, Now, Reset, null), Now, true);
        Assert.Empty(engine.GetState().Observations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyExpiredOrUnknownPeriodCanReserveExactlyOnce(bool missingReset)
    {
        var account = Account();
        var engine = Armed(account);
        var ticket = Due(engine, account);
        var fresh = Read(account, Reset, missingReset ? null : Reset, 0);
        Assert.Equal(ResetMessageDecision.Reserved, engine.PrepareAttempt(ticket, fresh, Reset, true));
        var attempt = Assert.Single(engine.GetState().Attempts);
        Assert.Equal(ResetMessageOutcome.Reserved, attempt.Outcome);
        Assert.Equal(ticket.ResetsAt, attempt.ResetsAt);
        Assert.Equal(ResetMessageDecision.Invalid, engine.PrepareAttempt(ticket, fresh, Reset, true));
        Assert.Null(engine.GetDue(account, true, "session", Reset));
    }

    [Fact]
    public void FirstNewPeriodWithUsageSkipsSendingAndArmsNextReset()
    {
        var account = Account();
        var engine = Armed(account);
        var ticket = Due(engine, account);
        var next = Reset.AddHours(5);
        var fresh = Read(account, Reset.AddSeconds(1), next, 0.1);
        // The ordinary refresh observer must preserve the due ticket until preflight evaluates it.
        engine.Observe(account, true, "session", fresh, fresh.FetchedAt, true);
        Assert.Equal(ticket, Due(engine, account, fresh.FetchedAt));
        Assert.Equal(ResetMessageDecision.AlreadyActive, engine.PrepareAttempt(ticket, fresh, fresh.FetchedAt, true));
        Assert.Empty(engine.GetState().Attempts);
        Assert.Equal(next, Assert.Single(engine.GetState().Observations).ResetsAt);
        Assert.Null(engine.GetDue(account, true, "session", fresh.FetchedAt));
        Assert.Equal(ResetMessageDecision.Invalid, engine.PrepareAttempt(ticket, fresh, fresh.FetchedAt, true));
    }

    [Fact]
    public void FreshZeroUsageInFuturePeriodCanReservePreviousResetAndObserveNext()
    {
        var account = Account();
        var engine = Armed(account);
        var next = Reset.AddHours(5);
        Assert.Equal(ResetMessageDecision.Reserved,
            engine.PrepareAttempt(Due(engine, account), Read(account, Reset, next, 0), Reset, true));
        Assert.Equal(next, Assert.Single(engine.GetState().Observations).ResetsAt);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void ExpiredOrUnknownPeriodWithNonzeroUsageWaits(double percent)
    {
        var account = Account();
        var engine = Armed(account);
        var ticket = Due(engine, account);
        Assert.Equal(ResetMessageDecision.Waiting,
            engine.PrepareAttempt(ticket, Read(account, Reset, Reset, percent), Reset, true));
        Assert.Equal(ResetMessageDecision.Waiting,
            engine.PrepareAttempt(ticket, Read(account, Reset, null, percent), Reset, true));
        Assert.Empty(engine.GetState().Attempts);
        Assert.Equal(ticket, Due(engine, account));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherExhaustedLimitBlocksUntilAFreshAvailableRead(bool missingReset)
    {
        var account = Account();
        var engine = Armed(account);
        var ticket = Due(engine, account);
        var fresh = Read(account, Reset, null, 0);
        fresh = fresh with { Windows = [.. fresh.Windows, new("weekly", "Weekly", 100, missingReset ? null : Reset.AddDays(1))] };
        Assert.Equal(ResetMessageDecision.Waiting, engine.PrepareAttempt(ticket, fresh, Reset, true));
        Assert.Empty(engine.GetState().Attempts);
        var available = Read(account, Reset.AddSeconds(1), null, 0);
        Assert.Equal(ResetMessageDecision.Reserved, engine.PrepareAttempt(ticket, available, available.FetchedAt, true));
    }

    [Fact]
    public void ExpiredOtherLimitDoesNotBlockConfirmedZeroTarget()
    {
        var account = Account();
        var engine = Armed(account);
        var fresh = Read(account, Reset, null, 0);
        fresh = fresh with { Windows = [.. fresh.Windows, new("weekly", "Weekly", 100, Reset)] };
        Assert.Equal(ResetMessageDecision.Reserved, engine.PrepareAttempt(Due(engine, account), fresh, Reset, true));
    }

    [Fact]
    public void MissingNullOrDuplicatedTargetNeverSubstitutesAnotherWindow()
    {
        var account = Account();
        var engine = Armed(account);
        var ticket = Due(engine, account);
        var fresh = Read(account, Reset, null, 0);
        Assert.Equal(ResetMessageDecision.Waiting, engine.PrepareAttempt(ticket,
            fresh with { Windows = [new("weekly", "Weekly", 0, null)] }, Reset, true));
        Assert.Equal(ResetMessageDecision.Waiting, engine.PrepareAttempt(ticket,
            fresh with { Windows = [new("session", "Session", null, null)] }, Reset, true));
        Assert.Equal(ResetMessageDecision.Waiting, engine.PrepareAttempt(ticket,
            fresh with { Windows = [.. fresh.Windows, .. fresh.Windows] }, Reset, true));
        Assert.Empty(engine.GetState().Attempts);
    }

    [Fact]
    public void PreflightRequiresNewFreshPostBoundaryReadAndRejectsFutureClockTimestamps()
    {
        var account = Account();
        var engine = Armed(account);
        var ticket = Due(engine, account);
        Assert.Equal(ResetMessageDecision.Waiting, engine.PrepareAttempt(ticket, Read(account, Reset, null, 0), Reset, false));
        Assert.Equal(ResetMessageDecision.Waiting, engine.PrepareAttempt(ticket,
            Read(account, Reset.AddTicks(-1), null, 0), Reset, true));
        Assert.Equal(ResetMessageDecision.Waiting, engine.PrepareAttempt(ticket,
            Read(account, Reset.AddTicks(1), null, 0), Reset, true));
        Assert.Equal(ResetMessageDecision.Waiting, engine.PrepareAttempt(ticket,
            Read(account, Reset, null, 0), Reset.AddMinutes(2).AddTicks(1), true));
        Assert.Empty(engine.GetState().Attempts);
    }

    [Fact]
    public void StaleOrFutureReadsCannotArm()
    {
        var account = Account();
        var engine = new ResetMessageEngine();
        engine.Observe(account, true, "session", Read(account, Now.AddMinutes(-3), Reset), Now, true);
        engine.Observe(account, true, "session", Read(account, Now.AddTicks(1), Reset), Now, true);
        Assert.Empty(engine.GetState().Observations);
    }

    [Fact]
    public void RestartPreservesArmingAndUncertainAttemptPreventsResending()
    {
        var account = Account();
        var engine = Restart(Armed(account));
        var ticket = Due(engine, account);
        Assert.Equal(ResetMessageDecision.Reserved,
            engine.PrepareAttempt(ticket, Read(account, Reset, null, 0), Reset, true));
        engine = Restart(engine); // Crash after durable reserve, without a result.
        Assert.Equal(ResetMessageDecision.Invalid,
            engine.PrepareAttempt(ticket, Read(account, Reset.AddSeconds(1), null, 0), Reset.AddSeconds(1), true));
        Assert.Single(engine.GetState().Attempts);
        Assert.Null(engine.GetDue(account, true, "session", Reset));
    }

    [Theory]
    [InlineData(ResetMessageOutcome.Succeeded)]
    [InlineData(ResetMessageOutcome.Failed)]
    [InlineData(ResetMessageOutcome.Unknown)]
    public void EveryCompletionOutcomeSuppressesAutomaticRetry(ResetMessageOutcome outcome)
    {
        var account = Account();
        var engine = Armed(account);
        var ticket = Due(engine, account);
        Assert.Equal(ResetMessageDecision.Reserved,
            engine.PrepareAttempt(ticket, Read(account, Reset, null, 0), Reset, true));
        engine.CompleteAttempt(ticket, outcome, Reset.AddSeconds(1), "test result");
        engine = Restart(engine);
        var attempt = Assert.Single(engine.GetState().Attempts);
        Assert.Equal(outcome, attempt.Outcome);
        Assert.Equal("test result", attempt.Message);
        Assert.Equal(ResetMessageDecision.Invalid,
            engine.PrepareAttempt(ticket, Read(account, Reset.AddSeconds(2), null, 0), Reset.AddSeconds(2), true));
    }

    [Fact]
    public void DisableAndReenableDiscardsArmingButKeepsReceipts()
    {
        var account = Account();
        var engine = Armed(account);
        var ticket = Due(engine, account);
        engine.SynchronizeSettings(account, false, "session");
        Assert.Equal(ResetMessageDecision.Invalid,
            engine.PrepareAttempt(ticket, Read(account, Reset, null, 0), Reset, true));
        engine.Observe(account, true, "session", Read(account, Reset, Reset, 0), Reset, true);
        Assert.Null(engine.GetDue(account, true, "session", Reset));

        engine = Armed(account);
        Assert.Equal(ResetMessageDecision.Reserved,
            engine.PrepareAttempt(Due(engine, account), Read(account, Reset, null, 0), Reset, true));
        engine.SynchronizeSettings(account, false, "session");
        engine.SynchronizeSettings(account, true, "session");
        Assert.Single(Restart(engine).GetState().Attempts);
    }

    [Fact]
    public void ChangingTargetProviderOrIdentityInvalidatesOutstandingTicket()
    {
        var account = Account();
        var engine = Armed(account);
        var ticket = Due(engine, account);
        engine.SynchronizeSettings(account, true, "weekly");
        Assert.Equal(ResetMessageDecision.Invalid,
            engine.PrepareAttempt(ticket, Read(account, Reset, null, 0), Reset, true));
        engine = Armed(account);
        account.IdentityKey = "changed-identity";
        engine.SynchronizeSettings(account, true, "session");
        Assert.Empty(engine.GetState().Observations);
        Assert.Null(engine.GetDue(account, true, "session", Reset));
        engine = Armed(account);
        account.Provider = ProviderKind.Codex;
        Assert.Null(engine.GetDue(account, true, "session", Reset));
    }

    [Fact]
    public void WrongIdentityAccountOrProviderSnapshotsCannotAuthorize()
    {
        var account = Account();
        var engine = Armed(account);
        var ticket = Due(engine, account);
        var fresh = Read(account, Reset, null, 0);
        Assert.Equal(ResetMessageDecision.Waiting, engine.PrepareAttempt(ticket, fresh with { IdentityKey = "another" }, Reset, true));
        Assert.Equal(ResetMessageDecision.Waiting, engine.PrepareAttempt(ticket, fresh with { AccountId = Guid.NewGuid().ToString("N") }, Reset, true));
        Assert.Equal(ResetMessageDecision.Waiting, engine.PrepareAttempt(ticket, fresh with { Provider = ProviderKind.Codex }, Reset, true));
        Assert.Empty(engine.GetState().Attempts);
    }

    [Fact]
    public void DifferentProviderIdentitiesAndPeriodsHaveIndependentReceipts()
    {
        var first = Account();
        var second = Account();
        second.IdentityKey = "different-identity";
        var third = Account();
        third.Provider = ProviderKind.Codex;
        var engine = Armed(first);
        engine.Observe(second, true, "session", Read(second, Now, Reset), Now, true);
        engine.Observe(third, true, "session", Read(third, Now, Reset), Now, true);
        Assert.Equal(ResetMessageDecision.Reserved,
            engine.PrepareAttempt(Due(engine, first), Read(first, Reset, null, 0), Reset, true));
        Assert.Equal(ResetMessageDecision.Reserved,
            engine.PrepareAttempt(Due(engine, second), Read(second, Reset, null, 0), Reset, true));
        Assert.Equal(ResetMessageDecision.Reserved,
            engine.PrepareAttempt(Due(engine, third), Read(third, Reset, null, 0), Reset, true));
        var next = Reset.AddHours(5);
        engine.Observe(first, true, "session", Read(first, Reset.AddMinutes(1), next), Reset.AddMinutes(1), true);
        Assert.Equal(ResetMessageDecision.Reserved,
            engine.PrepareAttempt(Due(engine, first, next), Read(first, next, null, 0), next, true));
        Assert.Equal(4, engine.GetState().Attempts.Count);
        engine.RemoveAccount(first.Id);
        Assert.Equal(4, engine.GetState().Attempts.Count);
    }

    [Fact]
    public void DuplicateRegistrationsOfOneProviderIdentityShareThePeriodReceipt()
    {
        var first = Account();
        var duplicate = Account();
        var engine = Armed(first);
        engine.Observe(duplicate, true, "session", Read(duplicate, Now, Reset), Now, true);
        var firstTicket = Due(engine, first);
        var duplicateTicket = Due(engine, duplicate);
        Assert.Equal(ResetMessageDecision.Reserved,
            engine.PrepareAttempt(firstTicket, Read(first, Reset, null, 0), Reset, true));
        // The second fresh read can still round to zero; it must not spend usage again.
        Assert.Equal(ResetMessageDecision.Invalid,
            engine.PrepareAttempt(duplicateTicket, Read(duplicate, Reset.AddSeconds(1), null, 0), Reset.AddSeconds(1), true));
        Assert.Null(engine.GetDue(duplicate, true, "session", Reset.AddSeconds(1)));
        Assert.Equal(first.Id, Assert.Single(engine.GetState().Attempts).AccountId);
        engine.CompleteAttempt(duplicateTicket, ResetMessageOutcome.Succeeded, Reset.AddSeconds(2));
        Assert.Equal(ResetMessageOutcome.Reserved, Assert.Single(engine.GetState().Attempts).Outcome);
        engine.CompleteAttempt(firstTicket, ResetMessageOutcome.Succeeded, Reset.AddSeconds(2));
        Assert.Equal(ResetMessageOutcome.Succeeded, Assert.Single(engine.GetState().Attempts).Outcome);
    }

    [Fact]
    public void RemovingOriginAndRestartingCannotAllowDuplicateOrReaddedIdentityToSend()
    {
        var first = Account();
        var duplicate = Account();
        var engine = Armed(first);
        engine.Observe(duplicate, true, "session", Read(duplicate, Now, Reset), Now, true);
        var duplicateTicket = Due(engine, duplicate);
        Assert.Equal(ResetMessageDecision.Reserved,
            engine.PrepareAttempt(Due(engine, first), Read(first, Reset, null, 0), Reset, true));
        engine.RemoveAccount(first.Id);
        engine = Restart(engine);
        Assert.Single(engine.GetState().Attempts);
        Assert.Equal(ResetMessageDecision.Invalid,
            engine.PrepareAttempt(duplicateTicket, Read(duplicate, Reset, null, 0), Reset, true));
        Assert.Null(engine.GetDue(duplicate, true, "session", Reset));
        engine.RemoveAccount(duplicate.Id);
        engine = Restart(engine);
        var readded = Account();
        // Even a clock rollback that makes the old reset future again cannot rearm it.
        engine.Observe(readded, true, "session", Read(readded, Now, Reset), Now, true);
        Assert.Empty(engine.GetState().Observations);
        Assert.Null(engine.GetDue(readded, true, "session", Reset));
        Assert.Single(engine.GetState().Attempts);
    }

    [Fact]
    public async Task DuplicateRegistrationsRacingToReserveCreateOnlyOneIdentityReceipt()
    {
        var accounts = Enumerable.Range(0, 12).Select(_ => Account()).ToArray();
        var engine = new ResetMessageEngine();
        foreach (var account in accounts)
            engine.Observe(account, true, "session", Read(account, Now, Reset), Now, true);
        var tickets = accounts.Select(account => Due(engine, account)).ToArray();
        var results = await Task.WhenAll(accounts.Select((account, index) => Task.Run(() =>
            engine.PrepareAttempt(tickets[index], Read(account, Reset, null, 0), Reset, true))));
        Assert.Single(results, result => result == ResetMessageDecision.Reserved);
        var state = engine.GetState();
        var receipt = Assert.Single(state.Attempts);
        state.Attempts.Add(receipt with { AccountId = Account().Id });
        Assert.Single(new ResetMessageEngine(state).GetState().Attempts);
    }

    [Fact]
    public void OverdueAfterLongSleepExpiresWithoutCatchUpMessage()
    {
        var account = Account();
        var engine = Armed(account);
        var ticket = Due(engine, account);
        var overdue = Reset + ResetMessageEngine.MaximumOverdue + TimeSpan.FromTicks(1);
        Assert.Equal(ResetMessageDecision.Expired,
            engine.PrepareAttempt(ticket, Read(account, overdue, null, 0), overdue, true));
        Assert.Empty(engine.GetState().Attempts);
        Assert.Empty(engine.GetState().Observations);
        engine = Armed(account);
        Assert.Null(engine.GetDue(account, true, "session", overdue));
        engine.Observe(account, true, "session", Read(account, overdue, overdue.AddHours(5)), overdue, true);
        Assert.Equal(overdue.AddHours(5), Assert.Single(engine.GetState().Observations).ResetsAt);
    }

    [Fact]
    public void WithinOverdueBoundaryCanStillReserve()
    {
        var account = Account();
        var engine = Armed(account);
        var atBoundary = Reset + ResetMessageEngine.MaximumOverdue;
        Assert.Equal(ResetMessageDecision.Reserved,
            engine.PrepareAttempt(Due(engine, account, atBoundary), Read(account, atBoundary, null, 0), atBoundary, true));
    }

    [Fact]
    public void ClockRollbackAndOutOfOrderReadsCannotRearmOrSendEarly()
    {
        var account = Account();
        var engine = Armed(account);
        engine.Observe(account, true, "session", Read(account, Now.AddMinutes(-1), Reset.AddHours(1)), Now.AddMinutes(-1), true);
        Assert.Equal(Reset, Assert.Single(engine.GetState().Observations).ResetsAt);
        var ticket = Due(engine, account);
        Assert.Null(engine.GetDue(account, true, "session", Now.AddMinutes(-1)));
        Assert.Equal(ResetMessageDecision.Waiting,
            engine.PrepareAttempt(ticket, Read(account, Now.AddMinutes(-1), null, 0), Now.AddMinutes(-1), true));
        engine.Observe(account, true, "session", Read(account, Now.AddMinutes(1), Reset.AddMinutes(1)), Now.AddMinutes(1), true);
        engine.Observe(account, true, "session", Read(account, Now.AddMinutes(2), Reset), Now.AddMinutes(2), true);
        Assert.Equal(Reset.AddMinutes(1), Assert.Single(engine.GetState().Observations).ResetsAt);
        Assert.Equal(ResetMessageDecision.Invalid,
            engine.PrepareAttempt(ticket, Read(account, Reset, null, 0), Reset, true));
    }

    [Fact]
    public async Task ConcurrentPreflightReservationsProduceOnlyOneAttempt()
    {
        var account = Account();
        var engine = Armed(account);
        var ticket = Due(engine, account);
        var fresh = Read(account, Reset, null, 0);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => engine.PrepareAttempt(ticket, fresh, Reset, true))));
        Assert.Single(results, item => item == ResetMessageDecision.Reserved);
        Assert.Single(engine.GetState().Attempts);
    }

    [Fact]
    public void ReturnedStateCannotMutateEngineAndNewerSchemaIsRejected()
    {
        var account = Account();
        var engine = Armed(account);
        engine.GetState().Observations.Clear();
        Assert.Single(engine.GetState().Observations);
        Assert.Throws<InvalidOperationException>(() => new ResetMessageEngine(new() { SchemaVersion = 2 }));
    }

    [Fact]
    public void MalformedAndDuplicatePersistedRecordsAreNormalized()
    {
        var account = Account();
        var engine = Armed(account);
        var baseline = Assert.Single(engine.GetState().Observations);
        var state = engine.GetState();
        state.Observations.Add(baseline);
        state.Observations.Add(baseline with { IdentityKey = "" });
        state.Observations.Add(baseline with { FetchedAt = Reset });
        state.Attempts.Add(new(account.Provider, account.Id, account.IdentityKey!, "session", Reset, Now));
        engine = new(state);
        Assert.Single(engine.GetState().Observations);
        Assert.Empty(engine.GetState().Attempts);
    }
}
