using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class UsageStatisticsReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static AccountSettings Account() => new()
    {
        Provider = ProviderKind.Claude,
        IdentityKey = "synthetic-statistics-report-identity"
    };

    private static UsageStatisticSample Sample(AccountSettings account, int minutesAgo, double used,
        DateTimeOffset? reset = null) => new(account.Provider, account.Id, account.IdentityKey!,
            "session", "Session", used, reset, Now.AddMinutes(-minutesAgo));

    private static UsageStatisticsReport Report(AccountSettings account, params UsageStatisticSample[] samples)
        => UsageStatisticsReportBuilder.Build(samples, account, "session", Now, TimeSpan.FromHours(24));

    [Fact]
    public void EmptyHistoryDoesNotInventZeroOrAnObservationTime()
    {
        var report = Report(Account());
        Assert.Empty(report.Samples);
        Assert.Empty(report.Segments);
        Assert.Equal(0, report.SampleCount);
        Assert.Equal(0, report.ComparablePairCount);
        Assert.Null(report.LatestPercent);
        Assert.Null(report.PeakPercent);
        Assert.Null(report.ObservedIncreasePercent);
        Assert.Null(report.LastObservedAt);
        Assert.Equal(Now.AddHours(-24), report.From);
        Assert.Equal(Now, report.To);
    }

    [Fact]
    public void ASingleRealZeroReadingRemainsZeroButHasNoMeasuredIncrease()
    {
        var account = Account();
        var sample = Sample(account, 5, 0, Now.AddHours(1));
        var report = Report(account, sample);
        Assert.Same(sample, Assert.Single(report.Samples));
        Assert.Same(sample, Assert.Single(Assert.Single(report.Segments)));
        Assert.Equal(0, report.LatestPercent);
        Assert.Equal(0, report.PeakPercent);
        Assert.Null(report.ObservedIncreasePercent);
        Assert.Equal(sample.FetchedAt, report.LastObservedAt);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(24)]
    [InlineData(48)]
    public void RequestedRangeIncludesBothBoundariesAndCannotExceedRetention(int hours)
    {
        var account = Account();
        var from = Now.AddHours(-Math.Min(hours, 24));
        var template = Sample(account, 0, 20);
        var report = UsageStatisticsReportBuilder.Build([
            template with { FetchedAt = from.AddTicks(-1) },
            template with { FetchedAt = from }, template,
            template with { FetchedAt = Now.AddTicks(1) }
        ], account, "session", Now, TimeSpan.FromHours(hours));
        Assert.Equal(from, report.From);
        Assert.Equal(new[] { from, Now }, report.Samples.Select(sample => sample.FetchedAt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveRangesAreRejected(int minutes)
        => Assert.Throws<ArgumentOutOfRangeException>(() => UsageStatisticsReportBuilder.Build([], Account(),
            "session", Now, TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void CalendarLowerBoundaryDoesNotOverflow()
    {
        var now = DateTimeOffset.MinValue.AddHours(2);
        var report = UsageStatisticsReportBuilder.Build([], Account(), "session", now, TimeSpan.FromHours(24));
        Assert.Equal(DateTimeOffset.MinValue, report.From);
        Assert.Equal(now, report.To);
    }

    [Fact]
    public void LatestAndPeakAreObservedValuesWithoutARefreshFrequencyWeightedAverage()
    {
        var account = Account();
        var reset = Now.AddHours(1);
        var report = Report(account, Sample(account, 0, 30, reset), Sample(account, 15, 10, reset),
            Sample(account, 14, 80, reset), Sample(account, 13, 80, reset), Sample(account, 1, 20, reset));
        Assert.Equal(new[] { 10d, 80, 80, 20, 30 }, report.Samples.Select(sample => sample.UsedPercent));
        Assert.Equal(30, report.LatestPercent);
        Assert.Equal(80, report.PeakPercent);
        Assert.Equal(80, report.ObservedIncreasePercent);
        Assert.Equal(3, report.ComparablePairCount);
        Assert.Equal(5, report.SampleCount);
        Assert.Equal(Now, report.LastObservedAt);
        Assert.Equal(new[] { 3, 2 }, report.Segments.Select(segment => segment.Count));
    }

    [Fact]
    public void EachAccountProviderLoginIdentityAndQuotaMustMatchExactly()
    {
        var account = Account();
        var valid = Sample(account, 1, 20, Now.AddHours(1));
        var report = Report(account, valid,
            valid with { AccountId = Guid.NewGuid().ToString("N"), UsedPercent = 99 },
            valid with { Provider = ProviderKind.Codex, UsedPercent = 99 },
            valid with { IdentityKey = "old-login", UsedPercent = 99 },
            valid with { IdentityKey = account.IdentityKey!.ToUpperInvariant(), UsedPercent = 99 },
            valid with { WindowId = "weekly", UsedPercent = 99 },
            valid with { WindowId = "Session", UsedPercent = 99 });
        Assert.Equal(valid, Assert.Single(report.Samples));
        Assert.Equal(20, report.PeakPercent);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("account")]
    [InlineData("provider")]
    [InlineData("window")]
    public void MissingOrMalformedSelectionCannotExposeOtherAccounts(string invalidField)
    {
        var account = Account();
        var sample = Sample(account, 0, 20);
        var window = "session";
        switch (invalidField)
        {
            case "identity": account.IdentityKey = null; break;
            case "account": account.Id = "invalid"; break;
            case "provider": account.Provider = (ProviderKind)999; break;
            case "window": window = " "; break;
        }
        var report = UsageStatisticsReportBuilder.Build([sample], account, window, Now, TimeSpan.FromHours(1));
        Assert.Empty(report.Samples);
        Assert.Null(report.PeakPercent);
    }

    [Fact]
    public void MalformedAndFutureSamplesNeverContributeToChartOrSummary()
    {
        var account = Account();
        var valid = Sample(account, 0, 20);
        var report = Report(account, null!, valid,
            valid with { AccountId = "invalid" }, valid with { IdentityKey = "" },
            valid with { Provider = (ProviderKind)999 }, valid with { WindowId = "" },
            valid with { WindowName = " " }, valid with { UsedPercent = double.NaN },
            valid with { UsedPercent = double.PositiveInfinity }, valid with { UsedPercent = double.NegativeInfinity },
            valid with { UsedPercent = -1 }, valid with { UsedPercent = 101 },
            valid with { FetchedAt = default }, valid with { FetchedAt = Now.AddTicks(1) });
        Assert.Same(valid, Assert.Single(report.Samples));
    }

    [Fact]
    public void DuplicateTimestampsKeepTheFirstValidObservationIncludingEquivalentOffsets()
    {
        var account = Account();
        var first = Sample(account, 5, 10, Now.AddHours(1));
        var latest = Sample(account, 0, 15, first.ResetsAt);
        var report = Report(account, first with { UsedPercent = double.NaN }, first,
            first with { UsedPercent = 90 }, first with { FetchedAt = first.FetchedAt.ToOffset(TimeSpan.FromHours(9)) }, latest);
        Assert.Equal(new[] { first, latest }, report.Samples);
        Assert.Equal(1, report.ComparablePairCount);
        Assert.Equal(5, report.ObservedIncreasePercent);
    }

    [Fact]
    public void ResetChangesBreakTheChartAndNeverCountTheResetJumpAsUsage()
    {
        var account = Account();
        var oldReset = Now.AddMinutes(-12);
        var newReset = Now.AddHours(5);
        var report = Report(account, Sample(account, 20, 80, oldReset), Sample(account, 15, 90, oldReset),
            Sample(account, 10, 5, newReset), Sample(account, 5, 12, newReset));
        Assert.Equal(new[] { 2, 2 }, report.Segments.Select(segment => segment.Count));
        Assert.Equal(17, report.ObservedIncreasePercent);
        Assert.Equal(2, report.ComparablePairCount);
        // The earlier period expired before now, but was valid when the readings were collected.
        Assert.Equal(90, report.PeakPercent);
    }

    [Fact]
    public void UpwardResetChangeAlsoStartsANewSegment()
    {
        var account = Account();
        var report = Report(account, Sample(account, 10, 10, Now.AddHours(1)),
            Sample(account, 5, 80, Now.AddHours(2)));
        Assert.Equal(2, report.Segments.Count);
        Assert.Null(report.ObservedIncreasePercent);
        Assert.Equal(0, report.ComparablePairCount);
    }

    [Fact]
    public void DecreaseWithoutResetChangeBreaksComparisonAndRetainsBothReadings()
    {
        var account = Account();
        var reset = Now.AddHours(1);
        var report = Report(account, Sample(account, 15, 40, reset), Sample(account, 10, 50, reset),
            Sample(account, 5, 20, reset), Sample(account, 0, 25, reset));
        Assert.Equal(new[] { 2, 2 }, report.Segments.Select(segment => segment.Count));
        Assert.Equal(15, report.ObservedIncreasePercent);
        Assert.Equal(4, report.SampleCount);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(5, 15)]
    [InlineData(10, 20)]
    [InlineData(60, 120)]
    [InlineData(720, 1440)]
    [InlineData(0, 15)]
    [InlineData(-2147483648, 15)]
    public void GapBoundaryFollowsRefreshCadenceAndNeverBridgesLongerInterruptions(int refresh, int gapMinutes)
    {
        var account = Account();
        account.RefreshMinutes = refresh;
        var reset = Now.AddHours(1);
        var older = Sample(account, gapMinutes, 10, reset);
        var newer = Sample(account, 0, 20, reset);
        var allowed = Report(account, older, newer);
        Assert.Single(allowed.Segments);
        Assert.Equal(10, allowed.ObservedIncreasePercent);
        // Keep the older reading within retention when the boundary itself is 24 hours.
        var after = Now.AddTicks(1);
        var beyond = UsageStatisticsReportBuilder.Build([older, newer with { FetchedAt = after }],
            account, "session", after, TimeSpan.FromHours(24));
        if (gapMinutes < 1440) Assert.Equal(2, beyond.Segments.Count);
        Assert.Null(beyond.ObservedIncreasePercent);
    }

    [Theory]
    [InlineData(1440)]
    [InlineData(2147483647)]
    public void DailyRefreshDoesNotMistakeAnExpectedDailyIntervalForAnInterruption(int refresh)
    {
        var account = Account();
        account.RefreshMinutes = refresh;
        var reset = Now.AddDays(5);
        var report = Report(account, Sample(account, 1440, 10, reset), Sample(account, 0, 20, reset));
        Assert.Single(report.Segments);
        Assert.Equal(10, report.ObservedIncreasePercent);
    }

    [Fact]
    public void UnknownResetSamplesRemainPlottableButCannotProduceAnIncrease()
    {
        var account = Account();
        var report = Report(account, Sample(account, 10, 10), Sample(account, 5, 25), Sample(account, 0, 30));
        Assert.Equal(3, Assert.Single(report.Segments).Count);
        Assert.Equal(30, report.LatestPercent);
        Assert.Equal(30, report.PeakPercent);
        Assert.Null(report.ObservedIncreasePercent);
        Assert.Equal(0, report.ComparablePairCount);
    }

    [Fact]
    public void MissingResetBetweenKnownReadingsCannotBeSkippedToInventContinuity()
    {
        var account = Account();
        var reset = Now.AddHours(1);
        var report = Report(account, Sample(account, 15, 10, reset), Sample(account, 10, 20),
            Sample(account, 5, 30, reset), Sample(account, 0, 35, reset));
        Assert.Equal(new[] { 1, 1, 2 }, report.Segments.Select(segment => segment.Count));
        Assert.Equal(5, report.ObservedIncreasePercent);
        Assert.Equal(1, report.ComparablePairCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void AResetAtOrBeforeTheLaterReadingCannotCreateAComparablePair(int resetMinutes)
    {
        var account = Account();
        var reset = Now.AddMinutes(resetMinutes);
        var report = Report(account, Sample(account, 10, 10, reset), Sample(account, 5, 20, reset),
            Sample(account, 0, 30, reset));
        Assert.Equal(new[] { 2, 1 }, report.Segments.Select(segment => segment.Count));
        Assert.Equal(10, report.ObservedIncreasePercent);
        Assert.Equal(1, report.ComparablePairCount);
    }

    [Fact]
    public void ExpiredResetOnEveryReadingRetainsPointsWithoutConnectingStalePeriods()
    {
        var account = Account();
        var reset = Now.AddHours(-1);
        var report = Report(account, Sample(account, 10, 10, reset), Sample(account, 5, 20, reset));
        Assert.Equal(2, report.Segments.Count);
        Assert.Null(report.ObservedIncreasePercent);
        Assert.Equal(2, report.SampleCount);
    }

    [Fact]
    public void UnchangedComparableReadingsProduceAnObservedZeroIncrease()
    {
        var account = Account();
        var reset = Now.AddHours(1);
        var report = Report(account, Sample(account, 5, 20, reset), Sample(account, 0, 20, reset));
        Assert.Equal(0, report.ObservedIncreasePercent);
        Assert.Equal(1, report.ComparablePairCount);
    }

    [Fact]
    public void IncreasesAcrossDistinctPeriodsArePercentagePointsAndCanExceedOneHundred()
    {
        var account = Account();
        var oldReset = Now.AddMinutes(-12);
        var newReset = Now.AddHours(1);
        var report = Report(account, Sample(account, 20, 10, oldReset), Sample(account, 15, 90, oldReset),
            Sample(account, 10, 5, newReset), Sample(account, 0, 75, newReset));
        Assert.Equal(150, report.ObservedIncreasePercent);
        Assert.Equal(90, report.PeakPercent);
    }

    [Fact]
    public void BuildingAReportDoesNotChangeRawHistoryAndCollectionsCannotBeEdited()
    {
        var account = Account();
        var reset = Now.AddHours(1);
        var original = new[] { Sample(account, 0, 30, reset), Sample(account, 5, 20, reset) };
        var raw = original.ToList();
        var report = UsageStatisticsReportBuilder.Build(raw, account, "session", Now, TimeSpan.FromHours(1));
        Assert.Equal(original, raw);
        raw.Clear();
        Assert.Equal(2, report.SampleCount);
        Assert.Same(original[1], report.Samples[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<UsageStatisticSample>)report.Samples).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<IReadOnlyList<UsageStatisticSample>>)report.Segments).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<UsageStatisticSample>)report.Segments[0]).Clear());
    }
}
