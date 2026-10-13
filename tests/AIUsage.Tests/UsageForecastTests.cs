using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class UsageForecastTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);
    private static AccountSettings Account() => new() { Provider = ProviderKind.Claude, IdentityKey = "synthetic-forecast-identity" };
    private static UsageSnapshot Read(AccountSettings account, DateTimeOffset at, double? used, DateTimeOffset? reset = null)
        => new(account.Provider, account.Id, account.IdentityKey, null, at,
            [new("session", "현재 세션", used, reset ?? Now.AddHours(6))]);
    private static AccountUsageStatus Status(UsageSnapshot snapshot) => new(snapshot.AccountId, snapshot, UsageState.Ready, null, false, null);
    private static UsageSnapshot Observe(UsageForecastEngine engine, AccountSettings account, DateTimeOffset at,
        double used, DateTimeOffset? reset = null)
    {
        var snapshot = Read(account, at, used, reset);
        Assert.True(engine.Observe(account, snapshot, at));
        return snapshot;
    }
    private static UsageSnapshot Trend(UsageForecastEngine engine, AccountSettings account, DateTimeOffset? reset = null)
    {
        Observe(engine, account, Now, 20, reset);
        Observe(engine, account, Now.AddMinutes(5), 25, reset);
        return Observe(engine, account, Now.AddMinutes(10), 30, reset);
    }

    [Fact]
    public void LinearEstimateUsesRealElapsedTimeAndRemainsAnchoredToLastRead()
    {
        var account = Account();
        var engine = new UsageForecastEngine();
        var last = Trend(engine, account);
        var forecast = engine.GetForecast(account, Status(last), "session", last.FetchedAt);
        Assert.Equal(UsageForecastState.Estimated, forecast.State);
        Assert.Equal(60d, forecast.PercentPerHour!.Value, precision: 8);
        AssertTimeClose(Now.AddMinutes(80), forecast.ExhaustsAt);
        Assert.Equal(last.FetchedAt, forecast.ObservedAt);
        Assert.Equal(3, forecast.SampleCount);
        Assert.Equal(TimeSpan.FromMinutes(10), forecast.SampleSpan);
        Assert.Equal(forecast, engine.GetForecast(account, Status(last), "session", last.FetchedAt.AddMinutes(10)));
        Assert.Equal(3, engine.GetHistory(last.FetchedAt.AddMinutes(10)).Samples.Count);
    }

    [Fact]
    public void UnevenPollingUsesElapsedTimeInsteadOfAssumingARefreshInterval()
    {
        var account = Account();
        account.RefreshMinutes = 30;
        var engine = new UsageForecastEngine();
        Observe(engine, account, Now, 10);
        Observe(engine, account, Now.AddMinutes(2), 11);
        var last = Observe(engine, account, Now.AddMinutes(20), 30);
        Assert.Equal(60d, engine.GetForecast(account, Status(last), "session", last.FetchedAt).PercentPerHour!.Value, precision: 8);
    }

    [Theory]
    [InlineData(79, UsageForecastState.ResetFirst)]
    [InlineData(80, UsageForecastState.ResetFirst)]
    [InlineData(81, UsageForecastState.Estimated)]
    public void ResetBeforeOrAtTheEstimateIsReportedSeparately(int resetMinutes, UsageForecastState expected)
    {
        var account = Account();
        var engine = new UsageForecastEngine();
        var last = Trend(engine, account, Now.AddMinutes(resetMinutes));
        var forecast = engine.GetForecast(account, Status(last), "session", last.FetchedAt);
        Assert.Equal(expected, forecast.State);
        AssertTimeClose(Now.AddMinutes(80), forecast.ExhaustsAt);
    }

    [Theory]
    [InlineData(2, 10, 10)]
    [InlineData(3, 8, 10)]
    [InlineData(3, 10, 0.5)]
    public void InsufficientCountDurationOrPositiveIncreaseKeepsCollecting(int count, int durationMinutes, double increase)
    {
        var account = Account();
        var engine = new UsageForecastEngine();
        UsageSnapshot? latest = null;
        for (var index = 0; index < count; index++)
            latest = Observe(engine, account, Now.AddMinutes(durationMinutes * index / (double)(count - 1)),
                20 + increase * index / (count - 1));
        var result = engine.GetForecast(account, Status(latest!), "session", latest!.FetchedAt);
        Assert.Equal(UsageForecastState.Collecting, result.State);
        Assert.Null(result.ExhaustsAt);
        Assert.Null(result.PercentPerHour);
    }

    [Fact]
    public void ExactlyOnePercentagePointIsEnoughButZeroGrowthHasNoExhaustionDate()
    {
        var account = Account();
        var engine = new UsageForecastEngine();
        Observe(engine, account, Now, 20);
        Observe(engine, account, Now.AddMinutes(5), 20);
        var flat = Observe(engine, account, Now.AddMinutes(10), 20);
        var noGrowth = engine.GetForecast(account, Status(flat), "session", flat.FetchedAt);
        Assert.Equal(UsageForecastState.NoGrowth, noGrowth.State);
        Assert.Null(noGrowth.ExhaustsAt);
        Assert.Equal(3, noGrowth.SampleCount);
        var growing = Observe(engine, account, Now.AddMinutes(15), 21);
        Assert.Equal(4d, engine.GetForecast(account, Status(growing), "session", growing.FetchedAt).PercentPerHour!.Value, precision: 8);
    }

    [Fact]
    public void ExhaustedUsageNeedsNoTrendButExpiredOrStaleUsageIsNotCurrentExhaustion()
    {
        var account = Account();
        var engine = new UsageForecastEngine();
        var exhausted = Read(account, Now, 100);
        Assert.Equal(UsageForecastState.Exhausted, engine.GetForecast(account, Status(exhausted), "session", Now).State);
        Assert.Equal(UsageForecastState.Stale, engine.GetForecast(account, Status(exhausted), "session", Now.AddMinutes(16)).State);
        var expired = exhausted with { Windows = [new("session", "세션", 100, Now)] };
        Assert.Equal(UsageForecastState.Unavailable, engine.GetForecast(account, Status(expired), "session", Now).State);
    }

    [Fact]
    public void EstimateThatPassedWithoutAConfirmingReadBecomesStale()
    {
        var account = Account();
        var engine = new UsageForecastEngine();
        Observe(engine, account, Now, 50);
        Observe(engine, account, Now.AddMinutes(5), 70);
        var last = Observe(engine, account, Now.AddMinutes(10), 90);
        var initial = engine.GetForecast(account, Status(last), "session", last.FetchedAt);
        Assert.Equal(UsageForecastState.Estimated, initial.State);
        AssertTimeClose(Now.AddMinutes(12.5), initial.ExhaustsAt);
        var expired = engine.GetForecast(account, Status(last), "session", initial.ExhaustsAt!.Value);
        Assert.Equal(UsageForecastState.Stale, expired.State);
        Assert.Equal(initial.ExhaustsAt, expired.ExhaustsAt);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(10, 20)]
    [InlineData(1440, 60)]
    public void StaleAgeUsesBoundedTwiceRefreshInterval(int refreshMinutes, int maximumAge)
    {
        var account = Account();
        account.RefreshMinutes = refreshMinutes;
        var engine = new UsageForecastEngine();
        var last = Trend(engine, account);
        Assert.Equal(UsageForecastState.Estimated,
            engine.GetForecast(account, Status(last), "session", last.FetchedAt.AddMinutes(maximumAge)).State);
        Assert.Equal(UsageForecastState.Stale,
            engine.GetForecast(account, Status(last), "session", last.FetchedAt.AddMinutes(maximumAge).AddSeconds(1)).State);
    }

    [Fact]
    public void ManualRefreshesCoalesceIntoUtcMinuteBucketsWithoutPreventingLaterSampling()
    {
        var account = Account();
        var engine = new UsageForecastEngine();
        for (var seconds = 0; seconds < 180; seconds += 10)
            Observe(engine, account, Now.AddSeconds(seconds).ToOffset(TimeSpan.FromHours(9)), 20 + seconds / 100d);
        var history = engine.GetHistory(Now.AddMinutes(3));
        Assert.Equal(3, history.Samples.Count);
        Assert.Equal(new[] { Now.AddSeconds(50), Now.AddSeconds(110), Now.AddSeconds(170) },
            history.Samples.Select(sample => sample.FetchedAt.ToUniversalTime()));
        var latest = Read(account, Now.AddSeconds(170), 21.7);
        Assert.Equal(UsageForecastState.Collecting, engine.GetForecast(account, Status(latest), "session", latest.FetchedAt).State);
    }

    [Fact]
    public void DuplicateCacheReplaysAndOlderResponsesCannotAddSamplesOrRewriteUsage()
    {
        var account = Account();
        var engine = new UsageForecastEngine();
        var first = Observe(engine, account, Now, 20);
        Assert.False(engine.Observe(account, first, Now.AddMinutes(1)));
        Assert.False(engine.Observe(account, first with { Windows = [first.Windows[0] with { UsedPercent = 40 }] }, Now));
        var latest = Observe(engine, account, Now.AddMinutes(5), 25);
        Assert.False(engine.Observe(account, first, latest.FetchedAt));
        Assert.Equal(2, engine.GetHistory(latest.FetchedAt).Samples.Count);
        Assert.Equal(UsageForecastState.Stale, engine.GetForecast(account, Status(first), "session", latest.FetchedAt).State);
    }

    [Fact]
    public void RestartRestoresOnlyRecordedSamplesAndReadingACacheNeverManufacturesNewOnes()
    {
        var account = Account();
        var first = new UsageForecastEngine();
        var latest = Trend(first, account);
        var persisted = first.GetHistory(latest.FetchedAt);
        var restored = new UsageForecastEngine(persisted);
        persisted.Samples.Clear();
        Assert.Equal(UsageForecastState.Estimated, restored.GetForecast(account, Status(latest), "session", latest.FetchedAt).State);
        Assert.False(restored.Observe(account, latest, latest.FetchedAt));
        for (var minute = 0; minute < 10; minute++)
            restored.GetForecast(account, Status(latest), "session", latest.FetchedAt.AddMinutes(minute));
        Assert.Equal(3, restored.GetHistory(latest.FetchedAt.AddMinutes(9)).Samples.Count);
        var newSnapshot = latest with { FetchedAt = latest.FetchedAt.AddMinutes(5) };
        Assert.Equal(UsageForecastState.Collecting, restored.GetForecast(account, Status(newSnapshot), "session", newSnapshot.FetchedAt).State);
        var empty = new UsageForecastEngine();
        Assert.Equal(UsageForecastState.Collecting, empty.GetForecast(account, Status(latest), "session", latest.FetchedAt).State);
        Assert.Empty(empty.GetHistory(latest.FetchedAt).Samples);
    }

    [Fact]
    public void ResetTimestampChangesAndUsageDecreasesStartNewObservationRuns()
    {
        var account = Account();
        var engine = new UsageForecastEngine();
        Trend(engine, account);
        var changedReset = Observe(engine, account, Now.AddMinutes(15), 35, Now.AddHours(7));
        Assert.Equal(UsageForecastState.Collecting, engine.GetForecast(account, Status(changedReset), "session", changedReset.FetchedAt).State);
        Assert.Single(engine.GetHistory(changedReset.FetchedAt).Samples);
        var oldTimestampReturns = Observe(engine, account, Now.AddMinutes(20), 40);
        Assert.Single(engine.GetHistory(oldTimestampReturns.FetchedAt).Samples);
        var decreased = Observe(engine, account, Now.AddMinutes(25), 20);
        Assert.Single(engine.GetHistory(decreased.FetchedAt).Samples);
        Assert.Equal(UsageForecastState.Collecting, engine.GetForecast(account, Status(decreased), "session", decreased.FetchedAt).State);
        Observe(engine, account, Now.AddMinutes(30), 25);
        var recovered = Observe(engine, account, Now.AddMinutes(35), 30);
        Assert.Equal(UsageForecastState.Estimated, engine.GetForecast(account, Status(recovered), "session", recovered.FetchedAt).State);
    }

    [Fact]
    public void AccountIdentityProviderAndWindowBoundariesRemainIndependent()
    {
        var one = Account();
        var two = Account();
        var engine = new UsageForecastEngine();
        var first = Trend(engine, one);
        Trend(engine, two);
        var weekly = first with { FetchedAt = first.FetchedAt.AddMinutes(1), Windows = [new("weekly", "주간", 80, Now.AddDays(2))] };
        Assert.True(engine.Observe(one, weekly, weekly.FetchedAt));
        Assert.Equal(UsageForecastState.Collecting, engine.GetForecast(one, Status(weekly), "weekly", weekly.FetchedAt).State);
        Assert.Equal(UsageForecastState.Unavailable, engine.GetForecast(two, Status(first), "session", first.FetchedAt).State);
        var wrongProvider = first with { Provider = ProviderKind.Codex, FetchedAt = first.FetchedAt.AddMinutes(1) };
        Assert.False(engine.Observe(one, wrongProvider, wrongProvider.FetchedAt));
        one.IdentityKey = "replacement-synthetic-identity";
        engine.SynchronizeAccount(one);
        Assert.All(engine.GetHistory(weekly.FetchedAt).Samples, sample => Assert.Equal(two.Id, sample.AccountId));
        Assert.False(engine.Observe(one, first, first.FetchedAt));
        Assert.Equal(UsageForecastState.Unavailable, engine.GetForecast(one, Status(first), "session", first.FetchedAt).State);
        var replacement = Observe(engine, one, Now.AddMinutes(15), 40);
        Assert.Equal(UsageForecastState.Collecting, engine.GetForecast(one, Status(replacement), "session", replacement.FetchedAt).State);
        engine.RemoveAccount(one.Id);
        Assert.All(engine.GetHistory(replacement.FetchedAt).Samples, sample => Assert.Equal(two.Id, sample.AccountId));
        engine.Clear();
        Assert.Empty(engine.GetHistory(replacement.FetchedAt).Samples);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    [InlineData(101d)]
    public void InvalidUsageNeverBecomesASampleOrEstimate(double? used)
    {
        var account = Account();
        var engine = new UsageForecastEngine();
        var invalid = Read(account, Now, used);
        Assert.False(engine.Observe(account, invalid, Now));
        Assert.Empty(engine.GetHistory(Now).Samples);
        Assert.Equal(UsageForecastState.Unavailable, engine.GetForecast(account, Status(invalid), "session", Now).State);
    }

    [Fact]
    public void MissingExpiredFailedAndFutureInputsCannotEnterTheHistory()
    {
        var account = Account();
        var engine = new UsageForecastEngine();
        var valid = Read(account, Now, 20);
        var missingReset = valid with { Windows = [valid.Windows[0] with { ResetsAt = null }] };
        var expired = valid with { Windows = [valid.Windows[0] with { ResetsAt = Now }] };
        var failed = valid with { State = UsageState.Offline };
        var future = valid with { FetchedAt = Now.AddSeconds(1) };
        var old = valid with { FetchedAt = Now.AddMinutes(-16) };
        Assert.All(new[] { missingReset, expired, failed, future, old }, snapshot => Assert.False(engine.Observe(account, snapshot, Now)));
        Assert.Equal(UsageForecastState.Unavailable, engine.GetForecast(account, Status(missingReset), "session", Now).State);
        Assert.Equal(UsageForecastState.Unavailable, engine.GetForecast(account, Status(expired), "session", Now).State);
        Assert.Equal(UsageForecastState.Unavailable, engine.GetForecast(account, Status(failed), "session", Now).State);
        Assert.Equal(UsageForecastState.Stale, engine.GetForecast(account, Status(future), "session", Now).State);
        Assert.Equal(UsageForecastState.Stale, engine.GetForecast(account, Status(old), "session", Now).State);
        Assert.Equal(UsageForecastState.Unavailable, engine.GetForecast(account, Status(valid), "missing-window", Now).State);
        Assert.Empty(engine.GetHistory(Now).Samples);
    }

    [Fact]
    public void FailedCurrentStatusSuppressesAnOtherwiseValidSavedEstimate()
    {
        var account = Account();
        var engine = new UsageForecastEngine();
        var latest = Trend(engine, account);
        foreach (var state in Enum.GetValues<UsageState>().Where(state => state != UsageState.Ready))
            Assert.Equal(UsageForecastState.Unavailable, engine.GetForecast(account,
                Status(latest) with { State = state }, "session", latest.FetchedAt).State);
        Assert.Equal(3, engine.GetHistory(latest.FetchedAt).Samples.Count);
    }

    [Fact]
    public void ClockRollbackCannotRewindSavedSamplesAndBuildAFalseTrend()
    {
        var account = Account();
        var engine = new UsageForecastEngine();
        var latest = Trend(engine, account);
        var earlier = Read(account, Now.AddMinutes(1), 1);
        Assert.False(engine.Observe(account, earlier, earlier.FetchedAt));
        var history = engine.GetHistory(earlier.FetchedAt);
        Assert.Equal(3, history.Samples.Count);
        var restarted = new UsageForecastEngine(history);
        Assert.False(restarted.Observe(account, earlier, earlier.FetchedAt));
        Assert.Equal(UsageForecastState.Stale, restarted.GetForecast(account, Status(earlier), "session", earlier.FetchedAt).State);
        Assert.Equal(UsageForecastState.Estimated, restarted.GetForecast(account, Status(latest), "session", latest.FetchedAt).State);
    }

    [Fact]
    public void FittingIgnoresDataOutsideTheLastHourEvenWhenRetainedForRestart()
    {
        var account = Account();
        var engine = new UsageForecastEngine();
        Observe(engine, account, Now, 0);
        Observe(engine, account, Now.AddMinutes(59), 59);
        Observe(engine, account, Now.AddMinutes(65), 65);
        var latest = Observe(engine, account, Now.AddMinutes(70), 70);
        var forecast = engine.GetForecast(account, Status(latest), "session", latest.FetchedAt);
        Assert.Equal(3, forecast.SampleCount);
        Assert.Equal(TimeSpan.FromMinutes(11), forecast.SampleSpan);
        Assert.Equal(60d, forecast.PercentPerHour!.Value, precision: 8);
        Assert.Equal(4, engine.GetHistory(latest.FetchedAt).Samples.Count);
    }

    [Fact]
    public void RawHistoryExpiresAfterTwentyFourHoursAndHasAPerWindowCap()
    {
        var account = Account();
        var samples = Enumerable.Range(0, 2000).Select(minute => new UsageForecastObservation(account.Provider,
            account.Id, account.IdentityKey!, "session", Now.AddDays(5), Now.AddMinutes(minute), 20)).ToList();
        var engine = new UsageForecastEngine(new() { Samples = samples });
        // A backwards clock preserves the high-water marks; input is still bounded in memory.
        Assert.Equal(UsageForecastEngine.MaximumSamplesPerWindow, engine.GetHistory(Now).Samples.Count);
        var last = samples[^1].FetchedAt;
        var recent = engine.GetHistory(last).Samples;
        Assert.Equal(1441, recent.Count);
        Assert.All(recent, sample => Assert.True(last - sample.FetchedAt <= TimeSpan.FromHours(24)));
        Assert.Empty(engine.GetHistory(last.AddHours(24).AddSeconds(1)).Samples);
    }

    [Fact]
    public void InvalidPersistedObservationsAreDiscardedAndExportedHistoryIsDetached()
    {
        var account = Account();
        var valid = new UsageForecastObservation(account.Provider, account.Id, account.IdentityKey!, "session", Now.AddHours(6), Now, 20);
        var engine = new UsageForecastEngine(new()
        {
            Samples = [null!, valid, valid, valid with { AccountId = "invalid" }, valid with { IdentityKey = "" },
                valid with { WindowId = "" }, valid with { Provider = (ProviderKind)999 }, valid with { UsedPercent = double.NaN },
                valid with { UsedPercent = 101 }, valid with { ResetsAt = Now }, valid with { FetchedAt = default }]
        });
        var state = engine.GetHistory(Now);
        Assert.Equal(valid, Assert.Single(state.Samples));
        state.Samples.Clear();
        Assert.Single(engine.GetHistory(Now).Samples);
        Assert.Throws<InvalidOperationException>(() => new UsageForecastEngine(new() { SchemaVersion = 99 }));
    }

    private static void AssertTimeClose(DateTimeOffset expected, DateTimeOffset? actual)
    {
        Assert.NotNull(actual);
        Assert.InRange((actual!.Value - expected).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
    }
}
