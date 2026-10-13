using System.Text.Json;
using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class UsageStatisticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static AccountSettings Account() => new() { Provider = ProviderKind.Claude, IdentityKey = "synthetic-statistics-identity" };
    private static UsageSnapshot Read(AccountSettings account, DateTimeOffset at, double? used = 20, DateTimeOffset? reset = null)
        => new(account.Provider, account.Id, account.IdentityKey, null, at,
            [new("session", "현재 세션", used, reset)]);
    private static UsageStatisticSample Sample(AccountSettings account, DateTimeOffset at, double used = 20, DateTimeOffset? reset = null)
        => new(account.Provider, account.Id, account.IdentityKey!, "session", "현재 세션", used, reset, at);

    [Fact]
    public void EveryDistinctReadingWithinTheSameMinuteIsRetained()
    {
        var account = Account();
        var statistics = new UsageStatistics();
        for (var seconds = 0; seconds < 60; seconds += 10)
        {
            var at = Now.AddSeconds(seconds);
            Assert.True(statistics.Observe(account, Read(account, at, 20 + seconds / 10d, Now.AddHours(1)), at));
        }
        Assert.Equal(6, statistics.Count(Now.AddMinutes(1)));
        Assert.Equal(new[] { 20d, 21, 22, 23, 24, 25 },
            statistics.GetHistory(Now.AddMinutes(1)).Samples.Select(sample => sample.UsedPercent));
    }

    [Fact]
    public void ReplayingTheSameTimestampCannotAppendOrRewriteARecordedResponse()
    {
        var account = Account();
        var statistics = new UsageStatistics();
        var first = Read(account, Now, 20, Now.AddHours(1));
        Assert.True(statistics.Observe(account, first, Now));
        Assert.False(statistics.Observe(account, first, Now.AddMinutes(1)));
        var altered = first with { Windows = [new("session", "변경된 이름", 99, Now.AddHours(2))] };
        Assert.False(statistics.Observe(account, altered, Now));
        Assert.Equal(Sample(account, Now, 20, Now.AddHours(1)), Assert.Single(statistics.GetHistory(Now).Samples));
        // Different offsets expressing the same instant are the same source timestamp.
        Assert.False(statistics.Observe(account, first with { FetchedAt = Now.ToOffset(TimeSpan.FromHours(9)) }, Now));
    }

    [Fact]
    public void OutOfOrderButFreshDistinctReadingsRemainRawData()
    {
        var account = Account();
        var statistics = new UsageStatistics();
        Assert.True(statistics.Observe(account, Read(account, Now.AddMinutes(2), 30), Now.AddMinutes(2)));
        Assert.True(statistics.Observe(account, Read(account, Now.AddMinutes(1), 25), Now.AddMinutes(2)));
        Assert.Equal(new[] { Now.AddMinutes(1), Now.AddMinutes(2) },
            statistics.GetHistory(Now.AddMinutes(2)).Samples.Select(sample => sample.FetchedAt));
    }

    [Fact]
    public void ResetTransitionsUsageDecreasesAndUnknownResetsNeverEraseRawHistory()
    {
        var account = Account();
        var statistics = new UsageStatistics();
        var reads = new[]
        {
            Read(account, Now, 90, Now.AddMinutes(1)),
            Read(account, Now.AddMinutes(2), 0, Now.AddHours(5)),
            Read(account, Now.AddMinutes(3), 10, Now.AddHours(5)),
            Read(account, Now.AddMinutes(4), 5, Now.AddHours(5)),
            Read(account, Now.AddMinutes(5), 6),
            Read(account, Now.AddMinutes(6), 7, Now.AddMinutes(-1))
        };
        foreach (var read in reads) Assert.True(statistics.Observe(account, read, read.FetchedAt));
        var history = statistics.GetHistory(reads[^1].FetchedAt);
        Assert.Equal(6, history.Samples.Count);
        Assert.Equal(new[] { 90d, 0, 10, 5, 6, 7 }, history.Samples.Select(sample => sample.UsedPercent));
        Assert.Null(history.Samples[4].ResetsAt);
        Assert.Equal(Now.AddMinutes(-1), history.Samples[5].ResetsAt);
        statistics.GetForecastHistory(reads[^1].FetchedAt);
        Assert.Equal(6, statistics.Count(reads[^1].FetchedAt));
    }

    [Fact]
    public void MissingAndExpiredResetTimesAreKeptButCannotBeInventedForForecasting()
    {
        var account = Account();
        var statistics = new UsageStatistics();
        Assert.True(statistics.Observe(account, Read(account, Now, reset: null), Now));
        Assert.True(statistics.Observe(account, Read(account, Now.AddMinutes(1), reset: Now), Now.AddMinutes(1)));
        Assert.True(statistics.Observe(account, Read(account, Now.AddMinutes(2), reset: Now.AddMinutes(2)), Now.AddMinutes(2)));
        Assert.Equal(3, statistics.Count(Now.AddMinutes(2)));
        Assert.Empty(statistics.GetForecastHistory(Now.AddMinutes(2)).Samples);
    }

    [Fact]
    public void ProjectionUsesOriginalSourceFieldsAndOnlyTheForecastCopyIsCoalesced()
    {
        var account = Account();
        var reset = Now.AddHours(6);
        var statistics = new UsageStatistics();
        var reads = new[]
        {
            Read(account, Now, 20, reset), Read(account, Now.AddSeconds(10), 20.5, reset),
            Read(account, Now.AddSeconds(50), 21, reset), Read(account, Now.AddMinutes(5), 25, reset),
            Read(account, Now.AddMinutes(10).AddSeconds(50), 31, reset)
        };
        foreach (var read in reads) statistics.Observe(account, read, read.FetchedAt);
        var latest = reads[^1];
        var projection = statistics.GetForecastHistory(latest.FetchedAt);
        Assert.Equal(5, statistics.Count(latest.FetchedAt));
        Assert.Equal(3, projection.Samples.Count);
        Assert.All(projection.Samples, sample => Assert.Contains(statistics.GetHistory(latest.FetchedAt).Samples,
            raw => raw.Provider == sample.Provider && raw.AccountId == sample.AccountId && raw.IdentityKey == sample.IdentityKey &&
                raw.WindowId == sample.WindowId && raw.UsedPercent == sample.UsedPercent &&
                raw.ResetsAt == sample.ResetsAt && raw.FetchedAt == sample.FetchedAt));
        Assert.Equal(Now.AddSeconds(50), projection.Samples[0].FetchedAt);
        var forecast = new UsageForecastEngine(projection).GetForecast(account,
            new(account.Id, latest, UsageState.Ready, null, false, null), "session", latest.FetchedAt);
        Assert.Equal(UsageForecastState.Estimated, forecast.State);
        Assert.Equal(60d, forecast.PercentPerHour!.Value, precision: 8);
        projection.Samples.Clear();
        Assert.Equal(5, statistics.Count(latest.FetchedAt));
    }

    [Fact]
    public void DenseRawReadingsAreCoalescedBeforeTheForecastHistoryCap()
    {
        var account = Account();
        var reset = Now.AddHours(6);
        // More than 1,500 raw readings in the last ten minutes must not hide the older minute
        // buckets needed for a ten-minute fitting span. All 3,601 readings fit the raw cap.
        var raw = Enumerable.Range(0, 3601).Select(index =>
            Sample(account, Now.AddMilliseconds(index * 250), 20 + index / 240d, reset)).ToList();
        var statistics = new UsageStatistics(new() { Samples = raw });
        var latest = raw[^1];
        var projection = statistics.GetForecastHistory(latest.FetchedAt);
        Assert.Equal(3601, statistics.Count(latest.FetchedAt));
        Assert.Equal(16, projection.Samples.Count);
        var snapshot = Read(account, latest.FetchedAt, latest.UsedPercent, reset);
        var forecast = new UsageForecastEngine(projection).GetForecast(account,
            new(account.Id, snapshot, UsageState.Ready, null, false, null), "session", latest.FetchedAt);
        Assert.Equal(UsageForecastState.Estimated, forecast.State);
        Assert.True(forecast.SampleSpan >= UsageForecastEngine.MinimumSampleSpan);
        Assert.Equal(60d, forecast.PercentPerHour!.Value, precision: 8);
        Assert.Equal(3601, statistics.Count(latest.FetchedAt));
    }

    [Fact]
    public void ForecastPeriodsStayIsolatedWithoutRemovingOriginalPeriods()
    {
        var account = Account();
        var statistics = new UsageStatistics();
        var oldReset = Now.AddMinutes(11);
        for (var minute = 0; minute <= 10; minute += 5)
            statistics.Observe(account, Read(account, Now.AddMinutes(minute), 80 + minute, oldReset), Now.AddMinutes(minute));
        var newReset = Now.AddHours(5);
        statistics.Observe(account, Read(account, Now.AddMinutes(12), 1, newReset), Now.AddMinutes(12));
        var projected = statistics.GetForecastHistory(Now.AddMinutes(12));
        Assert.Equal(4, statistics.Count(Now.AddMinutes(12)));
        var firstNewPeriod = Assert.Single(projected.Samples);
        Assert.Equal(newReset, firstNewPeriod.ResetsAt);
        Assert.Equal(1, firstNewPeriod.UsedPercent);
    }

    [Fact]
    public void ForecastEnabledOrDisabledAndCollectionPausesDoNotImplicitlyDeleteHistory()
    {
        var account = Account();
        var statistics = new UsageStatistics();
        statistics.Observe(account, Read(account, Now, 20, Now.AddHours(6)), Now);
        var retained = statistics.GetHistory(Now);
        // Collection is controlled by the caller not invoking Observe; forecasting is only a projection.
        Assert.Equal(retained.Samples, statistics.GetHistory(Now.AddMinutes(10)).Samples);
        statistics.GetForecastHistory(Now.AddMinutes(10));
        Assert.Equal(retained.Samples, statistics.GetHistory(Now.AddMinutes(10)).Samples);
        var reopened = new UsageStatistics(statistics.GetHistory(Now.AddMinutes(10)));
        Assert.Single(reopened.GetHistory(Now.AddMinutes(10)).Samples);
        Assert.True(reopened.Observe(account, Read(account, Now.AddMinutes(11), 30, Now.AddHours(6)), Now.AddMinutes(11)));
        Assert.Equal(2, reopened.Count(Now.AddMinutes(11)));
    }

    [Fact]
    public void SerializationAndRestartPreserveEveryRawReadAndReplayDeduplication()
    {
        var account = Account();
        var original = new UsageStatistics();
        var latest = Read(account, Now.AddSeconds(20), 1, Now.AddHours(6));
        original.Observe(account, Read(account, Now, 90, Now.AddSeconds(10)), Now);
        original.Observe(account, latest, latest.FetchedAt);
        var json = JsonSerializer.Serialize(original.GetHistory(latest.FetchedAt));
        var restored = new UsageStatistics(JsonSerializer.Deserialize<UsageStatisticsHistory>(json));
        Assert.Equal(original.GetHistory(latest.FetchedAt).Samples, restored.GetHistory(latest.FetchedAt).Samples);
        Assert.False(restored.Observe(account, latest, latest.FetchedAt));
        Assert.Equal(2, restored.Count(latest.FetchedAt));
        var exported = restored.GetHistory(latest.FetchedAt);
        exported.Samples.Clear();
        Assert.Equal(2, restored.Count(latest.FetchedAt));
    }

    [Fact]
    public void EachAccountIdentityProviderAndWindowIsIsolated()
    {
        var one = Account();
        var two = Account();
        var statistics = new UsageStatistics();
        var first = Read(one, Now);
        first = first with { Windows = [first.Windows[0], new("weekly", "주간", 80, Now.AddDays(2))] };
        Assert.True(statistics.Observe(one, first, Now));
        Assert.True(statistics.Observe(two, Read(two, Now), Now));
        Assert.Equal(3, statistics.Count(Now));
        Assert.False(statistics.Observe(one, first with { Provider = ProviderKind.Codex }, Now));
        Assert.False(statistics.Observe(one, first with { AccountId = two.Id }, Now));
        Assert.False(statistics.Observe(one, first with { IdentityKey = "another-identity" }, Now));
        one.IdentityKey = "replacement-identity";
        statistics.SynchronizeAccount(one);
        Assert.Equal(two.Id, Assert.Single(statistics.GetHistory(Now).Samples).AccountId);
        Assert.True(statistics.Observe(one, Read(one, Now), Now));
        one.Provider = ProviderKind.Codex;
        statistics.SynchronizeAccount(one);
        Assert.Equal(two.Id, Assert.Single(statistics.GetHistory(Now).Samples).AccountId);
    }

    [Fact]
    public void RemovingOneAccountAndExplicitClearCleanOnlyTheRequestedDataAndAllowNewReads()
    {
        var one = Account();
        var two = Account();
        var statistics = new UsageStatistics();
        statistics.Observe(one, Read(one, Now), Now);
        statistics.Observe(two, Read(two, Now), Now);
        statistics.RemoveAccount(one.Id);
        Assert.Equal(two.Id, Assert.Single(statistics.GetHistory(Now).Samples).AccountId);
        Assert.True(statistics.Observe(one, Read(one, Now), Now));
        statistics.Clear();
        Assert.Equal(0, statistics.Count(Now));
        Assert.Empty(statistics.GetForecastHistory(Now).Samples);
        Assert.True(statistics.Observe(two, Read(two, Now), Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    [InlineData(101d)]
    public void MissingOrInvalidUsageCannotCreateRawReadings(double? used)
    {
        var account = Account();
        var statistics = new UsageStatistics();
        Assert.False(statistics.Observe(account, Read(account, Now, used), Now));
        Assert.Empty(statistics.GetHistory(Now).Samples);
    }

    [Fact]
    public void FailedStaleFutureAndMissingWindowResponsesAreRejectedWithoutInventingZeroes()
    {
        var account = Account();
        var statistics = new UsageStatistics();
        var valid = Read(account, Now);
        Assert.False(statistics.Observe(account, valid with { State = UsageState.Offline }, Now));
        Assert.False(statistics.Observe(account, valid with { FetchedAt = Now.AddMinutes(-16) }, Now));
        Assert.False(statistics.Observe(account, valid with { FetchedAt = Now.AddSeconds(1) }, Now));
        Assert.False(statistics.Observe(account, valid with { FetchedAt = default }, Now));
        Assert.False(statistics.Observe(account, valid with { Windows = [] }, Now));
        Assert.False(statistics.Observe(account, valid with { Windows = null! }, Now));
        Assert.False(statistics.Observe(account, valid with { Windows = [null!, new("", "무효", 0, null), new("id", "", 0, null)] }, Now));
        Assert.Equal(0, statistics.Count(Now));
        Assert.True(statistics.Observe(account, valid with { Windows = [new("session", "세션", 0, null)] }, Now));
        Assert.Equal(0, Assert.Single(statistics.GetHistory(Now).Samples).UsedPercent);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(10, 20)]
    [InlineData(1440, 60)]
    public void SnapshotAgeUsesClampedTwiceRefreshInterval(int interval, int maximumAge)
    {
        var account = Account();
        account.RefreshMinutes = interval;
        var statistics = new UsageStatistics();
        Assert.True(statistics.Observe(account, Read(account, Now), Now.AddMinutes(maximumAge)));
        Assert.False(new UsageStatistics().Observe(account, Read(account, Now), Now.AddMinutes(maximumAge).AddSeconds(1)));
    }

    [Fact]
    public void DuplicateWindowRowsAtOneTimestampCannotMultiplyReadings()
    {
        var account = Account();
        var statistics = new UsageStatistics();
        var read = Read(account, Now);
        Assert.True(statistics.Observe(account, read with { Windows = [read.Windows[0], read.Windows[0]] }, Now));
        Assert.Single(statistics.GetHistory(Now).Samples);
    }

    [Fact]
    public void InvalidRestoredDataIsNormalizedWithoutLosingValidUnknownResetReadings()
    {
        var account = Account();
        var valid = Sample(account, Now);
        var statistics = new UsageStatistics(new()
        {
            Samples = [null!, valid, valid, valid with { AccountId = "invalid" }, valid with { IdentityKey = "" },
                valid with { WindowId = "" }, valid with { WindowName = "" }, valid with { Provider = (ProviderKind)999 },
                valid with { UsedPercent = double.NaN }, valid with { UsedPercent = -1 }, valid with { UsedPercent = 101 },
                valid with { FetchedAt = default }]
        });
        Assert.Equal(valid, Assert.Single(statistics.GetHistory(Now).Samples));
        Assert.Throws<InvalidOperationException>(() => new UsageStatistics(new() { SchemaVersion = 99 }));
        Assert.Empty(new UsageStatistics(new() { Samples = null! }).GetHistory(Now).Samples);
    }

    [Fact]
    public void RetentionKeepsTheTwentyFourHourBoundaryAndDropsOlderRawRecords()
    {
        var account = Account();
        var statistics = new UsageStatistics(new()
        {
            Samples = [Sample(account, Now.AddHours(-24).AddSeconds(-1)), Sample(account, Now.AddHours(-24)), Sample(account, Now)]
        });
        Assert.Equal(2, statistics.Count(Now));
        Assert.Equal(Now.AddHours(-24), statistics.GetHistory(Now).Samples[0].FetchedAt);
        Assert.Single(statistics.GetHistory(Now.AddSeconds(1)).Samples);
        Assert.Empty(statistics.GetHistory(Now.AddHours(24).AddSeconds(1)).Samples);
    }

    [Fact]
    public void EachAccountWindowHasItsOwnTenThousandSampleCapDuringRestoreAndCollection()
    {
        var account = Account();
        var restored = new UsageStatistics(new()
        {
            Samples = Enumerable.Range(0, 10005).Select(second => Sample(account, Now.AddSeconds(second)))
                .Append(Sample(account, Now) with { WindowId = "weekly", WindowName = "주간" }).ToList()
        });
        var latest = Now.AddSeconds(10004);
        Assert.Equal(10001, restored.Count(latest));
        Assert.Equal(10000, restored.GetHistory(latest).Samples.Count(sample => sample.WindowId == "session"));
        Assert.Equal(Now.AddSeconds(5), restored.GetHistory(latest).Samples.First(sample => sample.WindowId == "session").FetchedAt);
        var next = latest.AddSeconds(1);
        Assert.True(restored.Observe(account, Read(account, next), next));
        Assert.Equal(10001, restored.Count(next));
        Assert.Equal(Now.AddSeconds(6), restored.GetHistory(next).Samples.First(sample => sample.WindowId == "session").FetchedAt);
        Assert.Contains(restored.GetHistory(next).Samples, sample => sample.WindowId == "weekly");
    }
}
